// ============================================================================
// Test: MCP server tool discovery (local test server, no agent)
// ============================================================================
//
// Adds a local test MCP server and asks the model to list its tools.
// No agent, no AvailableTools — just the MCP server attached to the session.
// Validates that MCP tools are visible to the model.
//
// Run:  dotnet run -- McpToolDiscovery
// ============================================================================

using GitHub.Copilot.SDK;

public class McpToolDiscovery : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "MCP server (local test): attach and list all discovered tools";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions { CliPath = cliPath });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "gpt5-mini",
            McpServers = new Dictionary<string, object>
            {
                [TestMcpServerHelper.McpServerName] = mcpServer
            },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with local MCP server...");
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
