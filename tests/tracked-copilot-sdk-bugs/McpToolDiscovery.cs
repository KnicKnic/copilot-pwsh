// ============================================================================
// Test: MCP server tool discovery via HTTP
// ============================================================================
//
// Adds an MCP server (github-mcp-server) using the HTTP endpoint
// https://api.enterprise.githubcopilot.com/mcp/readonly and lists all tools
// the session exposes.
//
// This validates that MCP servers are correctly attached to a session
// and their tools become visible to the model.
//
// Run:  dotnet run -- McpToolDiscovery
// ============================================================================

using GitHub.Copilot.SDK;

public class McpToolDiscovery : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "MCP server (github-mcp-server HTTP): attach and list all discovered tools";

    public async Task<int> RunAsync(string cliPath)
    {
        var mcpServer = new McpRemoteServerConfig
        {
            Url = "https://api.enterprise.githubcopilot.com/mcp/readonly",
            Type = "http",
            Tools = new List<string> { "*" }
        };

        Console.WriteLine($"MCP server: github-mcp-server");
        Console.WriteLine($"  Type: {mcpServer.Type}");
        Console.WriteLine($"  URL:  {mcpServer.Url}");
        Console.WriteLine();

        Console.WriteLine($"Starting client with CLI: {cliPath}");
        await using var client = new CopilotClient(new CopilotClientOptions { CliPath = cliPath });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "gpt5-mini",
            McpServers = new Dictionary<string, object>
            {
                ["github-mcp-server"] = mcpServer
            },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with remote MCP server...");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();

        Console.WriteLine("Asking model to list all its tools...");
        var response = await QueryAsync(session,
            "List every tool name you have access to. Output ONLY a comma-separated list of tool names, nothing else. No descriptions, no categories, no markdown.");

        Console.WriteLine();
        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Response ---");
        Console.WriteLine();

        // Parse reported tools
        var ignoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent" };
        var reportedTools = response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !ignoredTools.Contains(t))
            .ToList();

        Console.WriteLine($"Tools reported: {reportedTools.Count} (ignoring {string.Join(", ", ignoredTools)})");
        foreach (var t in reportedTools)
            Console.WriteLine($"  - {t}");
        Console.WriteLine();

        // Validate expected MCP tools (should appear alongside core CLI tools)
        var expectedMcpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "github-mcp-server-actions_list", "github-mcp-server-actions_get", "github-mcp-server-get_job_logs",
            "github-mcp-server-list_pull_requests", "github-mcp-server-search_pull_requests",
            "github-mcp-server-pull_request_read", "github-mcp-server-list_issues", "github-mcp-server-search_issues",
            "github-mcp-server-issue_read", "github-mcp-server-list_commits", "github-mcp-server-get_commit",
            "github-mcp-server-list_branches", "github-mcp-server-search_code", "github-mcp-server-search_repositories",
            "github-mcp-server-get_file_contents", "github-mcp-server-search_users", "github-mcp-server-list_copilot_spaces",
            "github-mcp-server-get_copilot_space"
        };

        var found = reportedTools.Where(t => expectedMcpTools.Contains(t)).ToList();
        var missing = expectedMcpTools.Where(t => !reportedTools.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"Expected MCP tools: {expectedMcpTools.Count}");
        Console.WriteLine($"Found:   {found.Count}");
        Console.WriteLine($"Missing: {missing.Count}");

        if (missing.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Missing MCP tools:");
            foreach (var t in missing)
                Console.WriteLine($"  - {t}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("All expected MCP tools present (alongside core CLI tools).");
        Console.ResetColor();
        return 0;
    }

    private static async Task<string> QueryAsync(CopilotSession session, string prompt)
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
}
