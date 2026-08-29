# Self-Contained SDK Bug Repros

Each subfolder is an **independent, runnable** compatibility test for a
GitHub Copilot SDK scenario. The tests are pinned to `GitHub.Copilot.SDK`
`1.0.11` (required CLI `1.0.79`) and were generated from the parent
[tracked-copilot-sdk-bugs](../README.md) suite so they can be handed to the SDK
team without any CopilotShell or suite dependency.

Each folder contains its own `.csproj`, `Program.cs`, a `README.md` describing
the scenario, and — for MCP scenarios — a bundled `test-mcp-server/` project.
Every repro auto-downloads the matching Copilot CLI on first run (or reuses one
found in a parent folder), so a bare `dotnet run -c Release` is enough.

## Scenarios

| Scenario | Kind | What it shows |
|----------|------|---------------|
| [McpSessionAvailableTools](McpSessionAvailableTools) | Compatibility matrix | Exercises exact dashed names, bare server names, namespaced names, server wildcards, source wildcards, and the unsupported legacy dash glob. |

> **Current selector behavior.** Exact dashed names (`test-mcp-alpha`), bare
> server names (`test-mcp`), namespaced names (`test-mcp/alpha`), server
> wildcards (`test-mcp/*`), and source wildcards (`mcp:*`) expose MCP tools.
> A dash glob (`test-mcp-*`) is treated as a literal and intentionally matches
> nothing.
>
> The earlier agent tool-scoping scenarios (`#859`) were likewise removed: once
> the CLI-injected `sql` built-in is ignored, agent `Tools` scoping is correctly
> enforced.

The combined test exits `0` when every selector behaves as documented, `1` on a
behavior mismatch, and `2` on a setup error.

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
