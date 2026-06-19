// ============================================================================
// Test: MCP server with session AvailableTools = ["test-mcp-*"] (dash wildcard)
// ============================================================================
//
// Attaches a local test MCP server and sets SessionConfig.AvailableTools to
// the dash-form wildcard (test-mcp-*). At the session level no wildcard form is
// honored (neither test-mcp-* nor test-mcp/*) — only explicit dashed tool names
// (test-mcp-alpha, ...) are matched — so the MCP tools are NOT exposed.
//
// Run:  dotnet run -- McpToolSpecified
// ============================================================================

using GitHub.Copilot;

public class McpToolSpecified : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "MCP server with session AvailableTools = [\"test-mcp-*\"] (dash wildcard): tools NOT exposed";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"  AvailableTools: [\"{TestMcpServerHelper.DashWildcardSelector}\"]");
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
            AvailableTools = new List<string> { TestMcpServerHelper.DashWildcardSelector },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with MCP server + AvailableTools...");
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
