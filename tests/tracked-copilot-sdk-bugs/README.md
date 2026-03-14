# Tracked Copilot SDK Bugs — Test Suite

Standalone .NET 8 console app that reproduces and tracks bugs in the [GitHub Copilot SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK). Each bug is a self-contained class implementing `IBugRepro`. Tests use the SDK directly (no CopilotShell dependency) so they can be reported upstream.

## Prerequisites

- .NET 8 SDK

## Quick Start

```bash
# Download the correct CLI version (matched to the pinned SDK)
dotnet run -- --download

# Run all known-failing tests (default)
dotnet run

# Run all tests (passing + failing)
dotnet run -- --all

# Run only passing tests
dotnet run -- --passing

# Run a specific test by name
dotnet run -- AgentToolScopingPostSelect

# List all tests
dotnet run -- --list
```

## Tests

| Test | ExpectsFail | Description |
|------|:-----------:|-------------|
| **AgentToolScopingPostSelect** | No | Agent selected post-creation via `Rpc.Agent.SelectAsync()` — `CustomAgentConfig.Tools` should restrict tool visibility but doesn't. |
| **AgentToolScopingSessionAgent** | Yes | Agent pre-selected via `SessionConfig.Agent` — `CustomAgentConfig.Tools` should restrict tool visibility but doesn't. |
| **AgentToolScopingSubagent** | No | Restricted agent delegates to unrestricted agent via the `task` tool — validates that subagent delegation correctly switches tool scope. |
| **McpToolDiscovery** | Yes | Attaches an MCP server (HTTP) to a session and checks that its tools become visible to the model. |
| **McpToolSpecified** | Yes | Same as McpToolDiscovery but also sets `SessionConfig.AvailableTools = ["github-mcp-server"]` to explicitly request the MCP server's tools. |
| **McpToolAgentScoped** | Yes | Agent with `Tools = ["github-mcp-server"]` selected via `Rpc.Agent.SelectAsync` — MCP tools should be exposed through the agent's tool scope. |

## How It Works

1. The runner resolves the SDK version and the required CLI version from assembly metadata.
2. If `--download` is passed, it fetches the matching CLI binary from npm.
3. Each `IBugRepro` implementation creates a `CopilotClient`, starts a session, and asks the model to list its visible tools.
4. The test validates the reported tools against expectations and returns exit code 0 (pass) or 1 (fail).

Tests marked `ExpectsFail = true` are known SDK/CLI bugs — the runner treats their failure as expected. Tests marked `ExpectsFail = false` are either fixed or test behavior that should already work.

## Adding a New Test

1. Create a new `.cs` file implementing `IBugRepro`:
   ```csharp
   public class MyNewBug : IBugRepro
   {
       public bool ExpectsFail => true;
       public string Description => "Short description of the bug";

       public async Task<int> RunAsync(string cliPath)
       {
           // Setup client, session, validate behavior
           // Return 0 = pass, 1 = fail
       }
   }
   ```
2. The runner discovers all `IBugRepro` implementations via reflection — no registration needed.
3. Run with: `dotnet run -- MyNewBug`
