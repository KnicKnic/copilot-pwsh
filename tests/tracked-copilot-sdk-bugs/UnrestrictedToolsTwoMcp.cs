// ============================================================================
// Test: Unrestricted session tools with two MCP servers
// ============================================================================
//
// Baseline / control test. Two MCP servers (mcp1, mcp2) are attached to a
// session, each exposing its own tools. No custom agent and no AvailableTools
// restriction are applied, so the session is fully unrestricted.
//
// The model is asked to dump every tool it can see. Because nothing is
// restricting the session, it should see tools from BOTH servers — proving the
// session has unrestricted tool access. This is the control against which the
// agent-scoped test (AgentScopedDefaultMcpTwoMcp) is compared.
//
// EXPECTED: Model sees tools from both mcp1 (mcp1-*) and mcp2 (mcp2-*)
// PASS:     At least one mcp1-* tool AND one mcp2-* tool are reported
// FAIL:     Either server's tools are missing
//
// Run:  dotnet run -- UnrestrictedToolsTwoMcp
// ============================================================================

using GitHub.Copilot;

public class UnrestrictedToolsTwoMcp : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "Two MCP servers, no agent/AvailableTools restriction: session is unrestricted and sees tools from both servers";

    private const string Mcp1Name = "mcp1";
    private const string Mcp2Name = "mcp2";

    public async Task<int> RunAsync(string cliPath)
    {
        var project = TestMcpServerHelper.ResolveTestServerProject();
        if (project is null) return 2;

        // Same test server program, registered twice under different names.
        var serverTools = await TestMcpServerHelper.ValidateTestServerAsync(project);
        if (serverTools is null) return 2;

        var mcp1 = TestMcpServerHelper.CreateMcpConfig(project);
        var mcp2 = TestMcpServerHelper.CreateMcpConfig(project);

        Console.WriteLine($"MCP server 1: {Mcp1Name} (tools: {Mcp1Name}-alpha, {Mcp1Name}-beta, {Mcp1Name}-gamma)");
        Console.WriteLine($"MCP server 2: {Mcp2Name} (tools: {Mcp2Name}-alpha, {Mcp2Name}-beta, {Mcp2Name}-gamma)");
        Console.WriteLine("Agent: <none> — session is unrestricted");
        Console.WriteLine();

        await using var client = new CopilotClient(new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "claude-haiku-4.5",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                [Mcp1Name] = mcp1,
                [Mcp2Name] = mcp2,
            },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with two MCP servers and no restrictions...");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();

        Console.WriteLine("Asking model to dump all its tools...");
        var response = await TestMcpServerHelper.QueryAsync(session, TestMcpServerHelper.ListToolsPrompt);

        Console.WriteLine();
        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Response ---");
        Console.WriteLine();

        var reportedTools = ParseTools(response);
        Console.WriteLine($"Tools reported: {reportedTools.Count}");
        foreach (var t in reportedTools)
            Console.WriteLine($"  - {t}");
        Console.WriteLine();

        var hasMcp1 = reportedTools.Any(t => t.StartsWith($"{Mcp1Name}-", StringComparison.OrdinalIgnoreCase));
        var hasMcp2 = reportedTools.Any(t => t.StartsWith($"{Mcp2Name}-", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"Sees mcp1 tools: {hasMcp1}");
        Console.WriteLine($"Sees mcp2 tools: {hasMcp2}");
        Console.WriteLine();

        if (hasMcp1 && hasMcp2)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Session is unrestricted: tools from both MCP servers are visible.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Expected tools from BOTH servers, but at least one server's tools are missing.");
        Console.ResetColor();
        return 1;
    }

    private static List<string> ParseTools(string response)
    {
        var ignoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent", "sql" };
        return response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().Trim('`', '"', '\''))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !t.Contains(' ') && !ignoredTools.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
