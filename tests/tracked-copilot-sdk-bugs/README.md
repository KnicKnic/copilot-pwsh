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
| **AgentToolScopingPostSelect** | No | Agent selected post-creation via `Rpc.Agent.SelectAsync()` — `CustomAgentConfig.Tools` correctly restricts tool visibility. |
| **AgentToolScopingSessionAgent** | No | Agent pre-selected via `SessionConfig.Agent` — `CustomAgentConfig.Tools` correctly restricts tool visibility. Fixed in `0.2.2-preview.0`. |
| **AgentToolScopingSubagent** | No | Restricted agent delegates to unrestricted agent via the `task` tool — validates that subagent delegation correctly switches tool scope. |
| **McpToolDiscovery** | No | Attaches an MCP server to a session and checks that its tools become visible to the model. Fixed in `0.2.2-preview.0`. |
| **McpToolExplicit** | No | MCP server with explicit tool names in `AvailableTools` — tools correctly exposed. Fixed in `0.2.2-preview.0`. |
| **McpToolSpecified** | Yes | Same as McpToolDiscovery but sets `SessionConfig.AvailableTools = ["test-mcp"]` (server name) — MCP tools are NOT exposed. |
| **McpToolAgentScoped** | Yes | Agent with `Tools = ["test-mcp"]` selected via `Rpc.Agent.SelectAsync` — MCP tools should be exposed through the agent's tool scope. |
| **McpToolAgentScopedExplicit** | Yes | Agent with explicit MCP tool names selected via `Rpc.Agent.SelectAsync` — MCP tools not exposed. |
| **McpToolAgentScopedExplicitSession** | Yes | Agent with explicit MCP tool names + session `AvailableTools` — MCP tools not exposed, wrong tools visible. |

## Tracked Issues

| Issue | Description | Tests |
|-------|-------------|-------|
| [github/copilot-sdk#861](https://github.com/github/copilot-sdk/issues/861) | Agent tool scoping (`CustomAgentConfig.Tools`) not enforced | AgentToolScopingSessionAgent, AgentToolScopingPostSelect |
| [github/copilot-sdk#860](https://github.com/github/copilot-sdk/issues/860) | MCP server tools not discoverable by the model | McpToolDiscovery, McpToolSpecified |
| [github/copilot-sdk#859](https://github.com/github/copilot-sdk/issues/859) | MCP tools not exposed through agent tool scope | McpToolAgentScoped |

> **2026-04-09:** Tested with SDK `0.2.2-preview.0` / CLI `1.0.20-1`:
> - **#861** — Fully resolved. Agent tool scoping works for both `SessionConfig.Agent` and `Rpc.Agent.SelectAsync`.
> - **#860** — Partially fixed. Basic MCP tool discovery works, but `AvailableTools` with server name doesn't surface MCP tools.
> - **#859** — Still reproduces. MCP tools not exposed through agent tool scope.
>
> **2026-04-01:** All three issues still reproduce with SDK `0.2.1-preview.1` / CLI `1.0.12-0`.

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
