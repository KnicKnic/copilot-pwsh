// ============================================================================
// Compatibility: AvailableTools = ["test-mcp-*"] is not a wildcard
// ============================================================================
//
// Attaches a local test MCP server and sets SessionConfig.AvailableTools to
// the legacy dash-glob form (test-mcp-*). The filter grammar treats it as a
// literal tool name, so it must not expose MCP tools. Use test-mcp/* for one
// server or mcp:* for every MCP tool.
//
// Run:  dotnet run -- McpToolSpecified
// ============================================================================

using GitHub.Copilot;

public class McpToolSpecified : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "Legacy dash glob is a literal and does not expose MCP tools";

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

        Console.WriteLine("Reading resolved tool metadata from the runtime...");
        var response = await TestMcpServerHelper.GetCurrentToolNamesAsync(session);

        Console.WriteLine();
        Console.WriteLine("--- Resolved Tools ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Tools ---");
        Console.WriteLine();

        var exposed = TestMcpServerHelper.ValidateToolResponse(response, TestMcpServerHelper.PrefixedToolNames) == 0;
        if (!exposed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Dash glob correctly matched no MCP tools.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Dash glob unexpectedly exposed MCP tools.");
        Console.ResetColor();
        return 1;
    }
}
