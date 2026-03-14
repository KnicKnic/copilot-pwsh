// ============================================================================
// Bug: CustomAgentConfig.Tools not enforced — SessionConfig.Agent preselect
// ============================================================================
//
// Agent is pre-selected at session creation via SessionConfig.Agent.
// The CLI should restrict tool visibility to that agent's Tools list.
//
// EXPECTED: Model sees only "view"
// ACTUAL:   Model sees all session tools
//
// Run:  dotnet run -- AgentToolScopingSessionAgent
// ============================================================================

using GitHub.Copilot.SDK;

public class AgentToolScopingSessionAgent : IBugRepro
{
    public bool ExpectsFail => true;
    public string Description =>
        "SessionConfig.Agent preselect: agent Tools not enforced, model sees all session tools";

    public async Task<int> RunAsync(string cliPath)
    {
        var restricted = new CustomAgentConfig
        {
            Name = "restricted",
            Description = "Agent with only 1 tool",
            Prompt = "You are a restricted agent. You should only have access to 'view' tool.",
            Tools = new List<string> { "view" }
        };

        var unrestricted = new CustomAgentConfig
        {
            Name = "unrestricted",
            Description = "Agent with all tools",
            Prompt = "You are an unrestricted agent with access to everything."
        };

        Console.WriteLine($"restricted.Tools   = [{string.Join(", ", restricted.Tools!)}]");
        Console.WriteLine($"unrestricted.Tools = <null> (all session tools)");
        Console.WriteLine();

        Console.WriteLine($"Starting client with CLI: {cliPath}");
        await using var client = new CopilotClient(new CopilotClientOptions { CliPath = cliPath });
        await client.StartAsync();

        // Pre-select via SessionConfig.Agent — no post-creation SelectAsync
        var sessionConfig = new SessionConfig
        {
            Model = "gpt5-mini",
            CustomAgents = new List<CustomAgentConfig> { restricted, unrestricted },
            Agent = "restricted",
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session with SessionConfig.Agent = 'restricted'...");
        Console.WriteLine("(No post-creation Rpc.Agent.SelectAsync call)");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();



        // Ask the model what tools it sees
        Console.WriteLine("Asking model to list its tools...");
        var toolsResponse = await QueryAsync(session,
            "List every tool name you have access to. Output ONLY a comma-separated list of tool names, nothing else. No descriptions, no categories, no markdown.");

        Console.WriteLine();
        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(toolsResponse);
        Console.WriteLine("--- End Response ---");
        Console.WriteLine();

        // Validate
        var allowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "view" };

        // Tools injected by the CLI that aren't part of the agent's declared list — ignore these
        var ignoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent" };

        var reportedTools = toolsResponse
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Trim('`', '"', '\''))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !t.Contains(' '))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extraTools = reportedTools.Where(t => !allowedTools.Contains(t) && !ignoredTools.Contains(t)).ToList();

        Console.WriteLine($"Agent 'restricted' Tools: [{string.Join(", ", allowedTools)}]");
        Console.WriteLine($"Tools model reported: {reportedTools.Count}");
        Console.WriteLine($"Extra tools (should be 0): {extraTools.Count}");

        if (extraTools.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Extra tools the model sees (should NOT be visible):");
            foreach (var tool in extraTools)
                Console.WriteLine($"  - {tool}");
            Console.WriteLine();
            Console.WriteLine("CustomAgentConfig.Tools is NOT enforced by the CLI.");
            return 1;
        }

        Console.WriteLine("Per-agent Tools scoping is working correctly.");
        return 0;
    }

    private static async Task<string> QueryAsync(CopilotSession session, string prompt)
    {
        var done = new TaskCompletionSource();
        string? content = null;
        using var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg: content = msg.Data.Content; break;
                case SessionIdleEvent: done.TrySetResult(); break;
                case SessionErrorEvent err: done.TrySetException(new Exception(err.Data.Message)); break;
            }
        });
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (completed != done.Task) throw new TimeoutException("Timed out waiting for response");
        await done.Task;
        return content?.Trim() ?? "";
    }
}
