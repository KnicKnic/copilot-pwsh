// ============================================================================
// Test: Default agent scoped to one MCP server cannot see the other
// ============================================================================
//
// Two MCP servers (mcp1, mcp2) are attached to a session, each exposing its own
// tools. A custom agent ("scoped-agent") is created with Tools = ["mcp1/*"] and
// set as the session's DEFAULT agent via SessionConfig.Agent (no post-creation
// Rpc.Agent.SelectAsync). The agent is also granted the "task" tool so it can
// create subagents.
//
// The agent session is then asked to dump its tools. Because the agent is scoped
// to mcp1/*, it should see mcp1's tools but NOT mcp2's tools — the second MCP
// server's tools must be hidden by the agent's scope.
//
// This is the agent-scoped counterpart to UnrestrictedToolsTwoMcp: same two
// servers, but a default agent restricts visibility to one of them.
//
// EXPECTED: Model sees mcp1-* tools, does NOT see mcp2-* tools
// PASS:     mcp1-* present AND mcp2-* absent
// FAIL:     mcp2-* leaks through (scope not enforced) or mcp1-* missing
//
// Run:  dotnet run -- AgentScopedDefaultMcpTwoMcp
// ============================================================================

using GitHub.Copilot;

public class AgentScopedDefaultMcpTwoMcp : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "Default agent (SessionConfig.Agent) scoped to mcp1/* with two MCP servers: sees mcp1 tools but NOT mcp2 tools";

    private const string Mcp1Name = "mcp1";
    private const string Mcp2Name = "mcp2";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcp1 = TestMcpServerHelper.CreateMcpConfig(project);
        var mcp2 = TestMcpServerHelper.CreateMcpConfig(project);

        // The agent prompt and description are intentionally neutral — they do NOT
        // mention mcp1/mcp2 or any restriction. Any tool filtering must come purely
        // from the Tools allow-list below, not from prompt instructions.
        var agent = new CustomAgentConfig
        {
            Name = "scoped-agent",
            Description = "A general-purpose agent.",
            Prompt = "You are a helpful assistant.",
            // Agent-level scoping uses the namespaced slash form; "task" enables subagent creation.
            Tools = new List<string> { $"{Mcp1Name}/*", "task" }
        };

        Console.WriteLine($"MCP server 1: {Mcp1Name} (tools: {Mcp1Name}-alpha, {Mcp1Name}-beta, {Mcp1Name}-gamma)");
        Console.WriteLine($"MCP server 2: {Mcp2Name} (tools: {Mcp2Name}-alpha, {Mcp2Name}-beta, {Mcp2Name}-gamma)");
        Console.WriteLine($"Agent: {agent.Name} (default)");
        Console.WriteLine($"  Tools: [{string.Join(", ", agent.Tools!)}]");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
        await client.StartAsync();

        // Pre-select via SessionConfig.Agent — the agent is the session default.
        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [Mcp1Name] = mcp1,
                [Mcp2Name] = mcp2,
            },
            CustomAgents = new List<CustomAgentConfig> { agent },
            Agent = "scoped-agent",
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with SessionConfig.Agent = 'scoped-agent'...");
        Console.WriteLine("(No post-creation Rpc.Agent.SelectAsync call)");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();

        Console.WriteLine("Asking the default agent to dump its tools...");
        var response = await TestMcpServerHelper.QueryAsync(session, TestMcpServerHelper.ListToolsPrompt);

        Console.WriteLine();
        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Response ---");
        Console.WriteLine();

        var reportedTools = ParseTools(response);
        Console.WriteLine($"Tools reported: {reportedTools.Count}");
        foreach (var t in reportedTools)
            Console.WriteLine($"  - {t}");
        Console.WriteLine();

        var mcp1Tools = reportedTools.Where(t => t.StartsWith($"{Mcp1Name}-", StringComparison.OrdinalIgnoreCase)).ToList();
        var mcp2Tools = reportedTools.Where(t => t.StartsWith($"{Mcp2Name}-", StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"mcp1 tools visible ({mcp1Tools.Count}): [{string.Join(", ", mcp1Tools)}]");
        Console.WriteLine($"mcp2 tools visible ({mcp2Tools.Count}): [{string.Join(", ", mcp2Tools)}]");
        Console.WriteLine();

        if (mcp2Tools.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("LEAK: agent scoped to mcp1/* can see mcp2 tools — scope NOT enforced:");
            foreach (var t in mcp2Tools)
                Console.WriteLine($"  - {t}");
            Console.ResetColor();
            return 1;
        }

        if (mcp1Tools.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Agent reported no mcp1 tools — the scoped MCP server's tools were not surfaced.");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Scope enforced: agent sees mcp1 tools and CANNOT see mcp2 tools.");
        Console.ResetColor();
        return 0;
    }

    private static List<string> ParseTools(string response)
    {
        var ignoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent", "sql" };
        return response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().Trim('`', '"', '\''))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !t.Contains(' ') && !ignoredTools.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
