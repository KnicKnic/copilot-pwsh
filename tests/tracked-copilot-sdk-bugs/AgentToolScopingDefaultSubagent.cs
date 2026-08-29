// ============================================================================
// Compatibility: preselected restricted agent delegates to a broader subagent
// ============================================================================
//
// Creates an unrestricted session with SessionConfig.Agent set to a restricted
// coordinator (Tools = ["view", "task"]). The coordinator delegates through
// task to an unrestricted agent, which must see tools beyond the coordinator's
// scope. This matches CopilotShell's -DefaultAgent orchestration path.
//
// Run: dotnet run -- AgentToolScopingDefaultSubagent
// ============================================================================

public class AgentToolScopingDefaultSubagent : IBugRepro
{
    public bool ExpectsFail => false;
    public string Description =>
        "Preselected restricted default agent delegates to an unrestricted subagent with broader tools";

    public Task<int> RunAsync(string cliPath) =>
        AgentToolScopingSubagent.RunScenarioAsync(cliPath, preselectAtCreation: true);
}
