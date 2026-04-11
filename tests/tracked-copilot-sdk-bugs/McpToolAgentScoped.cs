// ============================================================================
// Test: MCP tools via agent with Tools = ["test-mcp"] (bare server name)
// ============================================================================
//
// An agent is configured with Tools = ["test-mcp"] (bare MCP server name).
// The agent is selected via Rpc.Agent.SelectAsync. The model should see the
// MCP server's tools through the agent's tool scope.
//
// Run:  dotnet run -- McpToolAgentScoped
// ============================================================================

using GitHub.Copilot.SDK;

public class McpToolAgentScoped : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "Agent with Tools = [\"test-mcp\"] selected via SelectAsync: MCP tools should be exposed";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        var agent = new CustomAgentConfig
        {
            Name = "mcp-agent",
            Description = "Agent with access to test-mcp tools",
            Prompt = "You have access to test MCP server tools. When asked to list tools, output ONLY a comma-separated list of tool names.",
            Tools = new List<string> { TestMcpServerHelper.McpServerName }
        };

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"Agent: {agent.Name}");
        Console.WriteLine($"  Tools: [{string.Join(", ", agent.Tools!)}]");
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
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session...");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();

        Console.WriteLine("Selecting agent 'mcp-agent' via Rpc.Agent.SelectAsync...");
        await session.Rpc.Agent.SelectAsync("mcp-agent");
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
