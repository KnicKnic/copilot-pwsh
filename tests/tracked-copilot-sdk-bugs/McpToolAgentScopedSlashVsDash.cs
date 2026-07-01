// ============================================================================
// Test: Agent-scoped explicit MCP tool — slash vs dash spelling
// ============================================================================
//
// Determines whether an agent's Tools entry for a single MCP tool works when
// written as the namespaced slash form ("test-mcp/alpha") versus the dashed
// prefixed form ("test-mcp-alpha"). The model presents MCP tools to itself in the
// dashed form (test-mcp-alpha), so for each spelling we register the agent, select
// it, and check whether "test-mcp-alpha" shows up in the tool dump.
//
// This answers the open question of whether callers must rewrite "/" to "-" (or
// vice versa) when scoping an agent to a specific MCP tool.
//
// EXPECTED: Only the slash form is matched at the agent level
// RESULT:   "test-mcp/alpha" exposes the tool; "test-mcp-alpha" does NOT
// PASS(0):  Both spellings expose the tool (would mean the asymmetry is gone)
// FAIL(1):  Only one spelling works — currently the case (slash only)
//
// Tracked behavior: ExpectsFail = true because, as of the pinned CLI, the dash
// form is not matched at the agent level, so the test returns non-zero. If the
// dash form ever starts working, this flips to PASS and should be revisited.
//
// Run:  dotnet run -- McpToolAgentScopedSlashVsDash
// ============================================================================

using GitHub.Copilot;

public class McpToolAgentScopedSlashVsDash : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "Agent Tools single MCP tool: only the slash form (test-mcp/alpha) is matched; the dash form (test-mcp-alpha) is not";

    private const string TargetTool = "test-mcp-alpha"; // form the model reports back

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
        await client.StartAsync();

        var slashVisible = await RunFormAsync(client, project, "slash", "test-mcp/alpha");
        var dashVisible = await RunFormAsync(client, project, "dash", "test-mcp-alpha");

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"  Tools = [\"test-mcp/alpha\"] (slash) -> {TargetTool} visible: {slashVisible}");
        Console.WriteLine($"  Tools = [\"test-mcp-alpha\"] (dash)  -> {TargetTool} visible: {dashVisible}");
        Console.WriteLine();

        if (slashVisible && dashVisible)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Both spellings work — callers do not need to rewrite '/' to '-'.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        if (slashVisible && !dashVisible)
            Console.WriteLine("Only the SLASH form (test-mcp/alpha) works — the dash form is not matched at the agent level.");
        else if (!slashVisible && dashVisible)
            Console.WriteLine("Only the DASH form (test-mcp-alpha) works — the slash form is not matched at the agent level.");
        else
            Console.WriteLine("Neither spelling exposed the MCP tool.");
        Console.ResetColor();
        return 1;
    }

    private static async Task<bool> RunFormAsync(CopilotClient client, string project, string label, string toolSelector)
    {
        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        var agent = new CustomAgentConfig
        {
            Name = $"mcp-agent-{label}",
            Description = "Agent scoped to a single MCP tool.",
            Prompt = "You are a helpful assistant.",
            Tools = new List<string> { toolSelector }
        };

        Console.WriteLine();
        Console.WriteLine($"--- Form: {label} — agent Tools = [\"{toolSelector}\"] ---");

        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [TestMcpServerHelper.McpServerName] = mcpServer
            },
            CustomAgents = new List<CustomAgentConfig> { agent },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        await using var session = await client.CreateSessionAsync(sessionConfig);
        await session.Rpc.Agent.SelectAsync(agent.Name);

        var response = await TestMcpServerHelper.QueryAsync(session, TestMcpServerHelper.ListToolsPrompt);

        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Response ---");

        var reportedTools = response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().Trim('`', '"', '\''))
            .ToList();

        var visible = reportedTools.Contains(TargetTool, StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"{TargetTool} visible via {label} form: {visible}");
        return visible;
    }
}
