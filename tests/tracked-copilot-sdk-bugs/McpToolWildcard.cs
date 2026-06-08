// ============================================================================
// Test: MCP server with AvailableTools = ["test-mcp/*"] (server wildcard)
// ============================================================================
//
// Attaches a local test MCP server and sets SessionConfig.AvailableTools to
// a server wildcard. The CLI should expose the MCP tools to the model if
// wildcard MCP server selectors are supported.
//
// Run:  dotnet run -- McpToolWildcard
// ============================================================================

using GitHub.Copilot;

public class McpToolWildcard : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "MCP server with AvailableTools = [\"test-mcp/*\"]: wildcard should expose tools";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);
        var wildcardToolSelector = $"{TestMcpServerHelper.McpServerName}/*";

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"  AvailableTools: [\"{wildcardToolSelector}\"]");
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
            AvailableTools = new List<string> { wildcardToolSelector },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with MCP server + wildcard AvailableTools...");
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
