// ============================================================================
// Test: MCP server with session AvailableTools = ["test-mcp/alpha", ...]
//       (explicit namespaced / slash names)
// ============================================================================
//
// Attaches a local test MCP server and sets SessionConfig.AvailableTools to the
// explicit namespaced (slash) tool names (test-mcp/alpha, test-mcp/beta,
// test-mcp/gamma). The runtime paired with SDK 1.0.11 resolves these selectors
// against MCP server and leaf-tool metadata.
//
// Run:  dotnet run -- McpToolExplicitNamespaced
// ============================================================================

using GitHub.Copilot;

public class McpToolExplicitNamespaced : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "MCP server with session AvailableTools = [\"test-mcp/alpha\", ...]: tools exposed";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcpServer = TestMcpServerHelper.CreateMcpConfig(project);

        Console.WriteLine($"MCP server: {TestMcpServerHelper.McpServerName}");
        Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}");
        Console.WriteLine($"  AvailableTools ({TestMcpServerHelper.NamespacedToolNames.Count}): [{string.Join(", ", TestMcpServerHelper.NamespacedToolNames)}]");
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
            AvailableTools = new List<string>(TestMcpServerHelper.NamespacedToolNames),
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with explicit namespaced MCP tool names in AvailableTools...");
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
