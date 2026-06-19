// ============================================================================
// Test: MCP server with session AvailableTools = ["test-mcp"] (bare server name)
// ============================================================================
//
// Attaches a local test MCP server and sets SessionConfig.AvailableTools to the
// bare server name (test-mcp) with no tool segment or wildcard. At the session
// level only the dashed explicit names (test-mcp-alpha, ...) are honored, so the
// bare server name does NOT expose the MCP tools.
//
// Run:  dotnet run -- McpToolServerName
// ============================================================================

using GitHub.Copilot;

public class McpToolServerName : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "MCP server with session AvailableTools = [\"test-mcp\"] (bare server name): tools NOT exposed";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"  AvailableTools: [\"{TestMcpServerHelper.McpServerName}\"]");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [TestMcpServerHelper.McpServerName] = mcpServer
            },
            AvailableTools = new List<string> { TestMcpServerHelper.McpServerName },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with bare MCP server name in AvailableTools...");
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
