# Self-Contained SDK Bug Repros

Each subfolder is an **independent, runnable** reproduction of a single
GitHub Copilot SDK scenario that **does not pass** with `GitHub.Copilot.SDK`
`1.0.2` (required CLI `1.0.64-0`). They were generated from the parent
[tracked-copilot-sdk-bugs](../README.md) suite so they can be handed to the SDK
team without any CopilotShell or suite dependency.

Each folder contains its own `.csproj`, `Program.cs`, a `README.md` describing
the scenario, and — for MCP scenarios — a bundled `test-mcp-server/` project.
Every repro auto-downloads the matching Copilot CLI on first run (or reuses one
found in a parent folder), so a bare `dotnet run -c Release` is enough.

## Scenarios

| Scenario | Kind | What it shows |
|----------|------|---------------|
| [McpToolAgentScoped](McpToolAgentScoped) | Known bug | Agent `Tools = ["test-mcp"]` (bare server name) not expanded to MCP tools (#860). |
| [McpToolAgentScopedExplicit](McpToolAgentScopedExplicit) | Known bug | Agent `Tools` with explicit prefixed MCP tool names not surfaced (#860 / #1019). |
| [McpToolAgentScopedExplicitSession](McpToolAgentScopedExplicitSession) | Known bug | Explicit MCP tool names in agent scope **and** session `AvailableTools` still not surfaced. |
| [McpToolSpecified](McpToolSpecified) | Known bug | `AvailableTools = ["test-mcp"]` (server name) does not expose MCP tools (#861). |
| [McpToolWildcard](McpToolWildcard) | Known bug | `AvailableTools = ["test-mcp/*"]` (wildcard) does not expose MCP tools (#861). |

**Known bug** = continues to reproduce as already tracked.

> The agent tool-scoping scenarios (`#859`) were removed: once the CLI-injected
> `sql` built-in is ignored, agent `Tools` scoping is correctly enforced, so
> those cases pass and are no longer bugs.

All scenarios exit `1` when the bug reproduces, `0` if the behavior is fixed,
and `2` on a setup error.

## Run one

```bash
cd <ScenarioName>
dotnet run -c Release
```

## Run all

```powershell
Get-ChildItem -Directory | ForEach-Object {
    Write-Host "=== $($_.Name) ==="
    dotnet run --project $_.FullName -c Release
}
```
