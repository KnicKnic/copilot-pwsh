// ============================================================================
// Shared helper for McpTool* tests — local test MCP server management
// ============================================================================

using GitHub.Copilot.SDK;
using System.Diagnostics;
using System.Text.Json;

/// <summary>
/// Common utilities for McpTool* bug repro tests that use the local test MCP server.
/// The server exposes 3 tools: alpha, beta, gamma. When registered under server name
/// "test-mcp", the CLI prefixes them as test-mcp-alpha, test-mcp-beta, test-mcp-gamma.
/// </summary>
internal static class TestMcpServerHelper
{
    public const string McpServerName = "test-mcp";

    /// <summary>
    /// The 3 tool names as exposed by the MCP server (without server-name prefix).
    /// </summary>
    public static readonly List<string> RawToolNames = new() { "alpha", "beta", "gamma" };

    /// <summary>
    /// The 3 tool names as the CLI presents them (server-name prefixed).
    /// </summary>
    public static readonly List<string> PrefixedToolNames = new()
    {
        "test-mcp-alpha",
        "test-mcp-beta",
        "test-mcp-gamma",
    };

    /// <summary>
    /// Resolves the path to test-mcp-server.csproj relative to the test runner binary.
    /// </summary>
    public static string? ResolveTestServerProject()
    {
        var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var testServerProject = Path.Combine(baseDir, "test-mcp-server", "test-mcp-server.csproj");
        if (!File.Exists(testServerProject))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ERROR: test-mcp-server project not found at {testServerProject}");
            Console.ResetColor();
            return null;
        }
        return testServerProject;
    }

    /// <summary>
    /// Creates a McpLocalServerConfig pointing at the test MCP server.
    /// </summary>
    public static McpLocalServerConfig CreateMcpConfig(string testServerProject)
    {
        return new McpLocalServerConfig
        {
            Command = "dotnet",
            Args = new List<string> { "run", "--project", testServerProject, "-c", "Release" },
            Type = "stdio",
            Tools = new List<string> { "*" }
        };
    }

    /// <summary>
    /// Starts the test MCP server, performs JSON-RPC initialize + tools/list handshake,
    /// and validates the expected tools are present. Returns discovered tool names or
    /// null on failure.
    /// </summary>
    public static async Task<List<string>?> ValidateTestServerAsync(string testServerProject)
    {
        Console.WriteLine("Validating test MCP server...");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "run", "--project", testServerProject, "-c", "Release" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Failed to start test-mcp-server process.");
            Console.ResetColor();
            return null;
        }

        try
        {
            // Send initialize
            var initRequest = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "test", version = "1.0" } }
            });
            await proc.StandardInput.WriteLineAsync(initRequest);
            await proc.StandardInput.FlushAsync();

            var initResponse = await ReadJsonLineAsync(proc.StandardOutput, TimeSpan.FromSeconds(30));
            if (initResponse is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: test-mcp-server did not respond to initialize within 30s.");
                Console.ResetColor();
                return null;
            }
            Console.WriteLine($"  initialize response: OK");

            // Send notifications/initialized
            var notif = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" });
            await proc.StandardInput.WriteLineAsync(notif);
            await proc.StandardInput.FlushAsync();

            // Send tools/list
            var toolsRequest = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            await proc.StandardInput.WriteLineAsync(toolsRequest);
            await proc.StandardInput.FlushAsync();

            var toolsResponse = await ReadJsonLineAsync(proc.StandardOutput, TimeSpan.FromSeconds(10));
            if (toolsResponse is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: test-mcp-server did not respond to tools/list within 10s.");
                Console.ResetColor();
                return null;
            }

            // Parse tool names
            using var doc = JsonDocument.Parse(toolsResponse);
            var toolsArray = doc.RootElement.GetProperty("result").GetProperty("tools");
            var toolNames = new List<string>();
            foreach (var tool in toolsArray.EnumerateArray())
                toolNames.Add(tool.GetProperty("name").GetString()!);

            Console.WriteLine($"  tools/list response: {toolNames.Count} tools [{string.Join(", ", toolNames)}]");

            // Validate expected tools
            var missing = RawToolNames.Where(e => !toolNames.Contains(e)).ToList();
            if (missing.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: test-mcp-server missing expected tools: {string.Join(", ", missing)}");
                Console.ResetColor();
                return null;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Test MCP server validated successfully.");
            Console.ResetColor();
            Console.WriteLine();

            return toolNames;
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// Parse the model's tool-list response and validate against expected MCP tool names.
    /// Returns 0 on success, 1 on missing tools.
    /// </summary>
    public static int ValidateToolResponse(string response, IEnumerable<string> expectedTools)
    {
        var ignoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent" };
        var reportedTools = response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !ignoredTools.Contains(t))
            .ToList();

        Console.WriteLine($"Tools reported: {reportedTools.Count} (ignoring {string.Join(", ", ignoredTools)})");
        foreach (var t in reportedTools)
            Console.WriteLine($"  - {t}");
        Console.WriteLine();

        var expected = new HashSet<string>(expectedTools, StringComparer.OrdinalIgnoreCase);
        var found = reportedTools.Where(t => expected.Contains(t)).ToList();
        var missing = expected.Where(t => !reportedTools.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        var extra = reportedTools.Where(t => !expected.Contains(t)).ToList();

        Console.WriteLine($"Expected MCP tools: {expected.Count}");
        Console.WriteLine($"Found:   {found.Count}");
        Console.WriteLine($"Missing: {missing.Count}");
        Console.WriteLine($"Extra:   {extra.Count}");

        if (missing.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Missing MCP tools:");
            foreach (var t in missing)
                Console.WriteLine($"  - {t}");
            Console.ResetColor();
        }

        if (extra.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Extra tools (not in expected list):");
            foreach (var t in extra)
                Console.WriteLine($"  - {t}");
            Console.ResetColor();
        }

        if (missing.Count > 0) return 1;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("All expected MCP tools present.");
        Console.ResetColor();
        return 0;
    }

    /// <summary>
    /// Send a prompt to a Copilot session and return the assistant's response text.
    /// </summary>
    public static async Task<string> QueryAsync(CopilotSession session, string prompt)
    {
        var done = new TaskCompletionSource();
        string? content = null;
        using var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg: content = msg.Data.Content; break;
                case SessionIdleEvent: done.TrySetResult(); break;
                case SessionErrorEvent err: done.TrySetException(new Exception(err.Data.Message)); break;
            }
        });
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(120)));
        if (completed != done.Task) throw new TimeoutException("Timed out waiting for response");
        await done.Task;
        return content?.Trim() ?? "";
    }

    /// <summary>
    /// The standard prompt used to ask the model to list its tools.
    /// </summary>
    public const string ListToolsPrompt =
        "List every tool you have access to. Output ONLY the exact full internal tool identifier for each tool, as a comma-separated list. No descriptions, no categories, no markdown, no short names.";

    private static async Task<string?> ReadJsonLineAsync(StreamReader reader, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) return null;
                // Skip non-JSON lines (e.g. dotnet build output on stdout)
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('{'))
                    return trimmed;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
