// ============================================================================
// Self-contained repro — MCP tools not exposed via explicit agent Tools
// ============================================================================
//
// A local stdio MCP server ("test-mcp", tools: alpha/beta/gamma) is attached
// to the session. A custom agent lists all three MCP tools by their full
// prefixed names (test-mcp-alpha, test-mcp-beta, test-mcp-gamma) and is
// selected via Rpc.Agent.SelectAsync. The model should see exactly those tools.
//
// EXPECTED: model sees test-mcp-alpha, test-mcp-beta, test-mcp-gamma.
// ACTUAL:   model sees only built-in tools; no MCP tools are exposed.
//
// Returns 0 if all expected MCP tools are present, 1 if the bug reproduces.
//
// Run:  dotnet run                       (auto-downloads matching CLI)
//       dotnet run -- C:\path\copilot.exe (use an explicit CLI)
// ============================================================================

using GitHub.Copilot;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

var cliPath = await CliBootstrap.EnsureAsync(args);
if (cliPath is null) return 2;
Console.WriteLine($"CLI path: {cliPath}\n");

var project = McpHelper.ResolveServerProject();
if (project is null) return 2;

var mcpServer = McpHelper.CreateConfig(project);

var agent = new CustomAgentConfig
{
    Name = "mcp-agent-explicit",
    Description = "Agent with explicit test MCP tool names",
    Prompt = "You have access to test MCP server tools. When asked to list tools, output ONLY a comma-separated list of tool names.",
    Tools = new List<string>(McpHelper.Prefixed)
};

Console.WriteLine($"MCP server: {McpHelper.ServerName}");
Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
Console.WriteLine($"Agent: {agent.Name}  Tools: [{string.Join(", ", agent.Tools!)}]\n");

await using var client = new CopilotClient(
    new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
await client.StartAsync();

var sessionConfig = new SessionConfig
{
    Model = "claude-haiku-4.5",
    McpServers = new Dictionary<string, McpServerConfig> { [McpHelper.ServerName] = mcpServer },
    CustomAgents = new List<CustomAgentConfig> { agent },
    OnPermissionRequest = PermissionHandler.ApproveAll,
};

Console.WriteLine("Creating session...");
await using var session = await client.CreateSessionAsync(sessionConfig);

Console.WriteLine("Selecting agent 'mcp-agent-explicit' via Rpc.Agent.SelectAsync...");
await session.Rpc.Agent.SelectAsync("mcp-agent-explicit");
Console.WriteLine();

Console.WriteLine("Asking model to list all its tools...");
var response = await Repro.QueryAsync(session, McpHelper.ListToolsPrompt);

Console.WriteLine($"\n--- Model Response ---\n{response}\n--- End Response ---\n");

return McpHelper.ValidateToolResponse(response, McpHelper.Prefixed);

// ============================================================================
// Helpers
// ============================================================================

static class Repro
{
    public static async Task<string> QueryAsync(CopilotSession session, string prompt)
    {
        var done = new TaskCompletionSource();
        string? content = null;
        using var sub = session.On<SessionEvent>(evt =>
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
}

static class McpHelper
{
    public const string ServerName = "test-mcp";

    public static readonly string[] Prefixed = { "test-mcp-alpha", "test-mcp-beta", "test-mcp-gamma" };

    public const string ListToolsPrompt =
        "List every tool you have access to. Output ONLY the exact full internal tool identifier for each tool, as a comma-separated list. No descriptions, no categories, no markdown, no short names.";

    public static string? ResolveServerProject()
    {
        // bin/<cfg>/net8.0 -> scenario folder
        var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var proj = Path.Combine(baseDir, "test-mcp-server", "test-mcp-server.csproj");
        if (!File.Exists(proj))
        {
            Console.WriteLine($"ERROR: test-mcp-server project not found at {proj}");
            return null;
        }
        return proj;
    }

    public static McpStdioServerConfig CreateConfig(string project) => new()
    {
        Command = "dotnet",
        Args = new List<string> { "run", "--project", project, "-c", "Release" },
        Tools = new List<string> { "*" }
    };

    public static int ValidateToolResponse(string response, IEnumerable<string> expectedTools)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent", "sql" };
        var reported = response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !ignored.Contains(t))
            .ToList();

        var expected = new HashSet<string>(expectedTools, StringComparer.OrdinalIgnoreCase);
        var missing = expected.Where(t => !reported.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        var extra = reported.Where(t => !expected.Contains(t)).ToList();

        Console.WriteLine($"Reported tools ({reported.Count}): {string.Join(", ", reported)}");
        Console.WriteLine($"Expected MCP tools: {expected.Count}  Found: {expected.Count - missing.Count}  Missing: {missing.Count}  Extra: {extra.Count}");

        if (missing.Count > 0)
        {
            Console.WriteLine("Missing MCP tools: " + string.Join(", ", missing));
            Console.WriteLine("\nFAIL: expected MCP tools were not exposed to the model.");
            return 1;
        }

        Console.WriteLine("\nPASS: all expected MCP tools present.");
        return 0;
    }
}

static class CliBootstrap
{
    public static async Task<string?> EnsureAsync(string[] args)
    {
        var sdkVersion = typeof(CopilotClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var requiredCli = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CopilotCliVersion")?.Value;

        Console.WriteLine($"SDK version:          {sdkVersion}");
        Console.WriteLine($"Required CLI version: {requiredCli ?? "unknown"}");

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";

        // 1) explicit path argument
        var explicitArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (!string.IsNullOrWhiteSpace(explicitArg) && File.Exists(explicitArg))
        {
            Console.WriteLine($"Using CLI from argument: {explicitArg}");
            return explicitArg;
        }

        // 2) reuse an existing CLI in the current dir or any ancestor
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, exeName);
            if (File.Exists(candidate) && (requiredCli is null || VersionMatches(candidate, requiredCli)))
            {
                Console.WriteLine($"Using existing CLI: {candidate}");
                return candidate;
            }
            dir = dir.Parent;
        }

        // 3) download the matching CLI into the current directory
        if (requiredCli is null)
        {
            Console.WriteLine("ERROR: cannot determine required CLI version from assembly metadata.");
            return null;
        }
        var target = Path.Combine(Directory.GetCurrentDirectory(), exeName);
        Console.WriteLine($"Downloading Copilot CLI v{requiredCli}...");
        return await DownloadAsync(requiredCli, target) ? target : null;
    }

    static bool VersionMatches(string path, string required)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion ?? info.FileVersion;
            return v is not null && v.Contains(required.Split('-')[0]);
        }
        catch { return true; }
    }

    static async Task<bool> DownloadAsync(string version, string targetPath)
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"win32-{arch}"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"darwin-{arch}"
            : $"linux-{arch}";
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var url = $"https://registry.npmjs.org/@github/copilot-{platform}/-/copilot-{platform}-{version}.tgz";
        Console.WriteLine($"  Source: {url}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var tgz = targetPath + ".tgz";
            try
            {
                await using (var fs = new FileStream(tgz, FileMode.Create))
                    await resp.Content.CopyToAsync(fs);

                await using var tgzStream = new FileStream(tgz, FileMode.Open, FileAccess.Read);
                await using var gz = new GZipStream(tgzStream, CompressionMode.Decompress);
                using var tar = new TarReader(gz);
                while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
                {
                    var name = entry.Name.StartsWith("package/") ? entry.Name["package/".Length..] : entry.Name;
                    if (name == exeName)
                    {
                        await entry.ExtractToFileAsync(targetPath, overwrite: true);
                        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            File.SetUnixFileMode(targetPath,
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        Console.WriteLine($"  Downloaded to {targetPath}");
                        return true;
                    }
                }
                Console.WriteLine("  ERROR: binary not found in tarball");
                return false;
            }
            finally { try { File.Delete(tgz); } catch { } }
        }
        catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); return false; }
    }
}
