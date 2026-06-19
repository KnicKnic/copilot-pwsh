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
| [McpSessionAvailableTools](McpSessionAvailableTools) | Known bug | A single repro that tries every session `AvailableTools` selector form against the same MCP server. Only the dashed-explicit form (`test-mcp-alpha`, ...) exposes the tools; the namespaced (`test-mcp/alpha`), dash-wildcard (`test-mcp-*`), and slash-wildcard (`test-mcp/*`) forms all fail (#861). |

**Known bug** = continues to reproduce as already tracked.

> **Selector-format asymmetry (per SDK team feedback).** MCP tools are matched
> by their namespaced name (`test-mcp/alpha`) or wildcard (`test-mcp/*`) at the
> **agent** level (`CustomAgentConfig.Tools`), but session-level
> `AvailableTools` only matches the **dashed explicit** names
> (`test-mcp-alpha`) — no namespaced or wildcard form (`test-mcp/alpha`,
> `test-mcp-*`, or `test-mcp/*`) works there. Adopting the slash form fixed all
> agent-scoped scenarios (`McpToolAgentScoped`, `McpToolAgentScopedExplicit`,
> `McpToolAgentScopedExplicitSession`), so they were removed from this folder.
> The remaining repro shows that none of those forms work at the session level —
> the inconsistency the SDK team acknowledged. It is consolidated into a single
> program so the team can watch each form flip to "EXPOSED" as fixes land; the
> parent suite keeps a separate test per form (`McpToolExplicitNamespaced`,
> `McpToolSpecified`, `McpToolWildcard`).
>
> The earlier agent tool-scoping scenarios (`#859`) were likewise removed: once
> the CLI-injected `sql` built-in is ignored, agent `Tools` scoping is correctly
> enforced.

The combined repro exits `1` while any session `AvailableTools` form still fails
to expose the MCP tools, `0` once every form is fixed, and `2` on a setup error.

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
