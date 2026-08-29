# Tracked Copilot SDK Bugs — Test Suite

Standalone .NET 8 console app that reproduces and tracks bugs in the [GitHub Copilot SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK). Each bug is a self-contained class implementing `IBugRepro`. Tests use the SDK directly (no CopilotShell dependency) so they can be reported upstream.

## Prerequisites

- .NET 8 SDK

## Quick Start

```bash
# Download the correct CLI version (matched to the pinned SDK)
dotnet run -- --download

# Run all known-failing tests (currently none on the pinned release)
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
| **AgentToolScopingDefaultSubagent** | No | Restricted agent preselected through `SessionConfig.Agent` delegates to an unrestricted subagent — validates the `-DefaultAgent` orchestration path end to end. |
| **McpToolDiscovery** | No | Attaches an MCP server to a session and checks that its tools become visible to the model. Fixed in `0.2.2-preview.0`. |
| **McpToolExplicit** | No | MCP server with explicit dashed tool names (`test-mcp-alpha`, ...) in session `AvailableTools` — tools correctly exposed. Fixed in `0.2.2-preview.0`. |
| **McpToolExplicitNamespaced** | No | Session `AvailableTools = ["test-mcp/alpha", ...]` resolves namespaced MCP tools. Fixed by CLI/runtime 1.0.79. |
| **McpToolServerName** | No | Session `AvailableTools = ["test-mcp"]` exposes that server's tools. Fixed by CLI/runtime 1.0.79. |
| **McpToolSpecified** | No | Confirms `test-mcp-*` is a literal, not a supported wildcard; use `test-mcp/*` or `mcp:*`. |
| **McpToolWildcard** | No | Session `AvailableTools = ["test-mcp/*"]` exposes every tool from that MCP server. Fixed by CLI/runtime 1.0.79. |
| **McpToolSourceWildcard** | No | SDK `ToolSet().AddMcp("*")` emits `mcp:*`, exposing MCP tools without admitting built-in/custom name collisions. |
| **McpToolAgentScoped** | No | Agent with `Tools = ["test-mcp/*"]` (wildcard) selected via `Rpc.Agent.SelectAsync` — MCP tools correctly exposed through the agent's tool scope. |
| **McpToolAgentScopedExplicit** | No | Agent with explicit namespaced tool names (`test-mcp/alpha`, ...) — MCP tools correctly exposed. |
| **McpToolAgentScopedExplicitSession** | No | Agent with namespaced tool names (`test-mcp/alpha`, ...) + session `AvailableTools` with dashed names — MCP tools correctly exposed. |
| **McpToolAgentScopedSlashVsDash** | No | Agent `Tools` accepts both `test-mcp/alpha` and the exact wire name `test-mcp-alpha`. Fixed by CLI/runtime 1.0.79. |
| **UnrestrictedToolsTwoMcp** | No | Two MCP servers (`mcp1`, `mcp2`), no agent/`AvailableTools` restriction — control test confirming an unrestricted session sees tools from **both** servers. |
| **AgentScopedDefaultMcpTwoMcp** | No | Two MCP servers (`mcp1`, `mcp2`) with a default agent (`SessionConfig.Agent`) scoped to `mcp1/*` (+ `task`) — agent sees `mcp1` tools but **not** `mcp2` tools (other server hidden by scope). |

## Tracked Issues

| Issue | Description | Tests |
|-------|-------------|-------|
| [github/copilot-sdk#859](https://github.com/github/copilot-sdk/issues/859) | Agent pre-selected via `SessionConfig.Agent` did not enforce `CustomAgentConfig.Tools` | AgentToolScopingSessionAgent |
| [github/copilot-sdk#860](https://github.com/github/copilot-sdk/issues/860) | Agent MCP selectors did not consistently resolve server/namespaced/exact forms. Fixed by CLI/runtime 1.0.79. | McpToolAgentScoped, McpToolAgentScopedExplicit, McpToolAgentScopedSlashVsDash |
| [github/copilot-sdk#861](https://github.com/github/copilot-sdk/issues/861) | Session MCP selectors did not expose tools for bare server, namespaced, or server-wildcard forms. Fixed by CLI/runtime 1.0.79; dash globs remain unsupported by design. | McpToolServerName, McpToolExplicitNamespaced, McpToolWildcard, McpToolSpecified |

> **2026-08-28:** Retested on SDK `1.0.11` / required CLI `1.0.79`:
> - Bare server names, namespaced exact names, and slash server wildcards now work at the session level.
> - Exact dashed wire names now work at both session and agent levels.
> - The SDK's `ToolSet` emits source-qualified selectors (`builtin:*`, `mcp:*`, `custom:*`) so filters can enforce provenance rather than relying only on names.
> - `test-mcp-*` is not part of the filter grammar and is treated as a literal. Use `test-mcp/*` for one server or `mcp:*` for all MCP tools.
> - The historical failures below remain as a record of the behavior on older SDK/runtime pairs.

> **2026-06-19:** Adopted the SDK team's namespaced-selector guidance and mapped
> out where each selector form works. MCP tools are matched by their namespaced
> name (`test-mcp/alpha`) or wildcard (`test-mcp/*`), which revealed a
> **selector-format asymmetry** between the agent and session levels:
> - **Agent level** (`CustomAgentConfig.Tools`) — the **slash** form works.
>   Switching `McpToolAgentScoped` to `test-mcp/*` and
>   `McpToolAgentScopedExplicit` / `McpToolAgentScopedExplicitSession` to
>   `test-mcp/alpha` made all three **pass** (#860 effectively resolved).
> - **Session level** (`SessionConfig.AvailableTools`) — only the **dashed
>   explicit** form (`test-mcp-alpha`, ...) works (`McpToolExplicit`). Every
>   other form fails (#861): bare server name `test-mcp` (`McpToolServerName`),
>   namespaced explicit `test-mcp/alpha` (`McpToolExplicitNamespaced`), dash
>   wildcard `test-mcp-*` (`McpToolSpecified`), and slash wildcard `test-mcp/*`
>   (`McpToolWildcard`) — so no bare-name, slash, or wildcard form works there.
>   Each form is now a separate main-suite test so it can flip to **PASS**
>   independently when fixed, while the `self_contained/` folder holds a single
>   combined repro,
>   [`McpSessionAvailableTools`](self_contained/McpSessionAvailableTools), that
>   exercises all forms and exits non-zero while any still fails.
> - The model always reports tools back in the dashed form (`test-mcp-alpha`),
>   so response validation expects dashed names throughout.
> - The SDK team acknowledged the dash-vs-slash inconsistency and is looking at
>   making it easier.

> **2026-06-17:** Tested with SDK `1.0.2` / required CLI `1.0.64-0`:
> - **#859 (SessionConfig.Agent preselect) and agent post-select (`Rpc.Agent.SelectAsync`)** — Still passing. The CLI now injects an extra built-in `sql` tool (per-session SQLite for task tracking); once it is ignored alongside `skill`/`report_intent`, the agent `Tools = ["view"]` allow-list is correctly enforced.
> - **#860 / #861 and agent-scoped MCP variants** — Still reproduce (`McpToolAgentScoped`, `McpToolAgentScopedExplicit`, `McpToolAgentScopedExplicitSession`, `McpToolSpecified`, `McpToolWildcard`).
> - **Still passing:** `AgentToolScopingPostSelect`, `AgentToolScopingSessionAgent`, `AgentToolScopingSubagent`, `McpToolDiscovery`, `McpToolExplicit`.
> - Each non-passing scenario has a standalone reproduction under [`self_contained/`](self_contained/README.md).

> **2026-06-07:** Tested with SDK `1.0.0` / required CLI `1.0.57`:
> - **#859** — Resolved. `SessionConfig.Agent` preselect now enforces `CustomAgentConfig.Tools`; the model only reported `view`.
> - **#860** — Still reproduces. Agent `Tools = ["test-mcp"]` sees no MCP tools.
> - **#861 / server-name and wildcard AvailableTools** — Still reproduces in this local suite. `AvailableTools = ["test-mcp"]` and `AvailableTools = ["test-mcp/*"]` report built-in tools but none of `test-mcp-alpha`, `test-mcp-beta`, or `test-mcp-gamma`.
> - **Explicit MCP tool names without agent scope** — Works. `McpToolExplicit` sees all three expected MCP tools.
> - **Explicit MCP tool names in agent scope** — Still reproduces. Agent-scoped explicit MCP tool names do not surface the MCP tools.

> **2026-04-09:** Tested with SDK `0.2.2-preview.0` / CLI `1.0.20-1`:
> - **#859** — Fully resolved. Agent tool scoping works for both `SessionConfig.Agent` and `Rpc.Agent.SelectAsync`.
> - **#860** — Still reproduces for agent-scoped bare MCP server names.
> - **#861 / server-name AvailableTools** — Partially fixed. Basic MCP tool discovery works, but `AvailableTools` with server name doesn't surface MCP tools.
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
