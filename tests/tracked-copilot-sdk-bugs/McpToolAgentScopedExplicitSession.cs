// ============================================================================
// Test: MCP tools via agent + session AvailableTools (local test server)
// ============================================================================
//
// Same as McpToolAgentScopedExplicit, but the MCP tools are also passed at the
// session level via SessionConfig.AvailableTools. Tests whether the agent can
// see MCP tools when they are explicitly listed in both places.
//
// Run:  dotnet run -- McpToolAgentScopedExplicitSession
// ============================================================================

using GitHub.Copilot.SDK;

public class McpToolAgentScopedExplicitSession : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "Agent with explicit local MCP tool names + session AvailableTools: MCP tools should be exposed";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        var agent = new CustomAgentConfig
        {
            Name = "mcp-agent-explicit-session",
            Description = "Agent with explicit test MCP tool names",
            Prompt = "You have access to test MCP server tools. When asked to list tools, output ONLY a comma-separated list of tool names.",
            Tools = new List<string>(TestMcpServerHelper.PrefixedToolNames)
        };

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"Agent: {agent.Name}");
        Console.WriteLine($"  Agent Tools ({agent.Tools!.Count}): [{string.Join(", ", agent.Tools)}]");
        Console.WriteLine($"  Session AvailableTools ({TestMcpServerHelper.PrefixedToolNames.Count}): [{string.Join(", ", TestMcpServerHelper.PrefixedToolNames)}]");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions { CliPath = cliPath });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, object>
            {
                [TestMcpServerHelper.McpServerName] = mcpServer
            },
            CustomAgents = new List<CustomAgentConfig> { agent },
            AvailableTools = new List<string>(TestMcpServerHelper.PrefixedToolNames),
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with AvailableTools set to explicit MCP tool names...");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();

        Console.WriteLine("Selecting agent 'mcp-agent-explicit-session' via Rpc.Agent.SelectAsync...");
        await session.Rpc.Agent.SelectAsync("mcp-agent-explicit-session");
        Console.WriteLine("Agent selected.");
        Console.WriteLine();

        Console.WriteLine("Asking model to list all its tools...");
        var response = await TestMcpServerHelper.QueryAsync(session, TestMcpServerHelper.ListToolsPrompt);

        Console.WriteLine();
        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Response ---");
        Console.WriteLine();

        return TestMcpServerHelper.ValidateToolResponse(response, TestMcpServerHelper.PrefixedToolNames);
    }
}
