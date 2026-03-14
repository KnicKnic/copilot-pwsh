// ============================================================================
// Bug: CustomAgentConfig.Tools not enforced — subagent delegation via task tool
// ============================================================================
//
// The restricted agent (Tools = ["view", "task"]) is selected, then asked to
// use the "task" tool to delegate to the "unrestricted" agent which has no
// Tools constraint. The unrestricted agent should see all session tools, while
// the restricted agent should only see "view" and "task".
//
// This tests whether subagent delegation via the task tool correctly switches
// tool scope. The unrestricted agent (no Tools constraint) should see all
// session tools even though it was invoked from a restricted agent.
//
// EXPECTED: Unrestricted agent (via task) sees all session tools
// PASS:     Unrestricted agent reports tools beyond restricted's view+task
// FAIL:     Unrestricted agent only sees restricted's tools (scope leaked)
//
// Run:  dotnet run -- AgentToolScopingSubagent
// ============================================================================

using GitHub.Copilot.SDK;

public class AgentToolScopingSubagent : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "Subagent via task tool: restricted agent delegates to unrestricted, unrestricted should see all tools";

    public async Task<int> RunAsync(string cliPath)
    {
        var restricted = new CustomAgentConfig
        {
            Name = "restricted",
            Description = "Agent with only view and task tools. Use task to delegate to other agents.",
            Prompt = "You are a restricted agent. You have access to 'view' and 'task' tools only. Use 'task' to delegate work to other agents when needed.",
            Tools = new List<string> { "view", "task" }
        };

        var unrestricted = new CustomAgentConfig
        {
            Name = "unrestricted",
            Description = "Agent with all tools. Lists all tools when asked.",
            Prompt = "You are an unrestricted agent with access to everything. When asked to list tools, output ONLY a comma-separated list of tool names."
        };

        Console.WriteLine($"restricted.Tools   = [{string.Join(", ", restricted.Tools!)}]");
        Console.WriteLine($"unrestricted.Tools = <null> (all session tools)");
        Console.WriteLine();

        Console.WriteLine($"Starting client with CLI: {cliPath}");
        await using var client = new CopilotClient(new CopilotClientOptions { CliPath = cliPath });
        await client.StartAsync();

        var sessionConfig = new SessionConfig
        {
            Model = "gpt5-mini",
            CustomAgents = new List<CustomAgentConfig> { restricted, unrestricted },
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        Console.WriteLine("Creating session...");
        await using var session = await client.CreateSessionAsync(sessionConfig);
        Console.WriteLine("Session created.");
        Console.WriteLine();

        // Select restricted agent
        Console.WriteLine("Selecting agent 'restricted' via Rpc.Agent.SelectAsync...");
        await session.Rpc.Agent.SelectAsync("restricted");
        Console.WriteLine("Agent selected.");
        Console.WriteLine();

        // Ask restricted agent to delegate to unrestricted agent via task tool
        Console.WriteLine("Asking restricted agent to delegate to unrestricted agent via task tool...");
        var response = await QueryAsync(session,
            "Use the task tool to run the 'unrestricted' agent with this prompt: " +
            "'List every tool name you have access to. Output ONLY a comma-separated list of tool names, nothing else. No descriptions, no categories, no markdown.' " +
            "Then repeat the unrestricted agent's response verbatim.");

        Console.WriteLine();
        Console.WriteLine("--- Model Response ---");
        Console.WriteLine(response);
        Console.WriteLine("--- End Response ---");
        Console.WriteLine();

        // Parse reported tools from the response
        var ignoredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent" };
        var reportedTools = response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !ignoredTools.Contains(t))
            .ToList();

        Console.WriteLine($"Unrestricted agent reported {reportedTools.Count} tools (ignoring {string.Join(", ", ignoredTools)})");

        // Sanity check: response must contain a known tool like "grep" to confirm
        // the delegation actually happened and returned real tool names.
        var knownTools = new[] { "grep", "glob", "view", "create", "edit" };
        var hasKnownTool = reportedTools.Any(t => knownTools.Contains(t, StringComparer.OrdinalIgnoreCase));
        if (!hasKnownTool)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Response does not contain any known tool ({string.Join(", ", knownTools)}) — delegation may have failed.");
            Console.ResetColor();
            return 1;
        }

        // The restricted agent (Tools=["view","task"]) used the task tool to delegate
        // to the unrestricted agent (no Tools constraint). The unrestricted agent
        // should see MORE than just view+task — it should see all session tools.
        // This proves subagent delegation respects each agent's own Tools scope.

        var restrictedOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "view", "task" };
        var extraTools = reportedTools.Where(t => !restrictedOnly.Contains(t)).ToList();

        if (extraTools.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Unrestricted agent sees {extraTools.Count} tools beyond restricted's scope:");
            foreach (var t in extraTools)
                Console.WriteLine($"  + {t}");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Subagent delegation works: unrestricted agent is NOT limited to restricted's Tools.");
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Unrestricted agent only sees restricted agent's tools — delegation did NOT switch scope.");
            Console.ResetColor();
            return 1;
        }
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
        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(120)));
        if (completed != done.Task) throw new TimeoutException("Timed out waiting for response");
        await done.Task;
        return content?.Trim() ?? "";
    }
}
