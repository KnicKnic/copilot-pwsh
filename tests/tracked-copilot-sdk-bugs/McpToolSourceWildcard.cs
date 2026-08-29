// ============================================================================
// Test: MCP tools with the SDK 1.0.11 source-qualified wildcard
// ============================================================================
//
// Attaches a local MCP server and sets SessionConfig.AvailableTools to the
// ToolSet-generated "mcp:*" selector. This is the preferred way to allow every
// MCP tool without allowing built-in or SDK/custom tools with colliding names.
//
// Run:  dotnet run -- McpToolSourceWildcard
// ============================================================================

using GitHub.Copilot;

public class McpToolSourceWildcard : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "SDK ToolSet source wildcard (mcp:*) exposes all MCP tools";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);
        var availableTools = new ToolSet().AddMcp("*");

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  AvailableTools: [{string.Join(", ", availableTools)}]");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(path: cliPath)
        });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [TestMcpServerHelper.McpServerName] = mcpServer
            },
            AvailableTools = availableTools,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        await using var session = await client.CreateSessionAsync(sessionConfig);
        var response = await TestMcpServerHelper.GetCurrentToolNamesAsync(session);

        Console.WriteLine("--- Resolved Tools ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Tools ---");
        Console.WriteLine();

        return TestMcpServerHelper.ValidateToolResponse(response, TestMcpServerHelper.PrefixedToolNames);
    }
}
