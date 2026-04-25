// ============================================================================
// Test: MCP tools with explicit AvailableTools (no agent, local test server)
// ============================================================================
//
// Attaches a local test MCP server and sets SessionConfig.AvailableTools to
// the full prefixed tool names (test-mcp-alpha, test-mcp-beta, test-mcp-gamma).
// No agent is used. The CLI should expose exactly those MCP tools to the model.
//
// Run:  dotnet run -- McpToolExplicit
// ============================================================================

using GitHub.Copilot.SDK;

public class McpToolExplicit : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "MCP server with explicit AvailableTools (no agent): tools should be exposed";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"  AvailableTools ({TestMcpServerHelper.PrefixedToolNames.Count}): [{string.Join(", ", TestMcpServerHelper.PrefixedToolNames)}]");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions { CliPath = cliPath });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [TestMcpServerHelper.McpServerName] = mcpServer
            },
            AvailableTools = new List<string>(TestMcpServerHelper.PrefixedToolNames),
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with explicit MCP tool names in AvailableTools...");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
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
