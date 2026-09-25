# McpSessionAvailableTools

Self-contained compatibility matrix for **GitHub Copilot SDK**
`SessionConfig.AvailableTools` MCP selector forms.

## Scenario

| | |
|---|---|
| **SDK** | `GitHub.Copilot.SDK` 1.0.11 |
| **CLI** | 1.0.79 (auto-downloaded) |
| **Model** | `claude-haiku-4.5` |
| **Status** | 6/6 selector forms matched expectations on 2026-09-14 |

The latest run used the SDK-bundled CLI artifact with Windows file version
`1.0.79`; its `--version` banner reports `1.0.83`. See the parent suite's
[completion record](../../README.md#latest-completion-check-2026-09-14) for
the full version details and coverage limits.

A local stdio MCP server is bundled in [`test-mcp-server/`](test-mcp-server)
and registered under the name `test-mcp` (tools `alpha`/`beta`/`gamma`, exposed
by the CLI as `test-mcp-alpha`, `test-mcp-beta`, `test-mcp-gamma`).

The program creates one session per `AvailableTools` selector form and reads
the runtime's resolved tool metadata:

| # | `AvailableTools` form | Example | Result |
|---|-----------------------|---------|--------|
| 0 | explicit dashed names (baseline) | `["test-mcp-alpha", ...]` | ✅ exposed |
| 1 | bare server name | `["test-mcp"]` | ✅ exposed |
| 2 | explicit namespaced / slash names | `["test-mcp/alpha", ...]` | ✅ exposed |
| 3 | legacy dash glob (literal) | `["test-mcp-*"]` | ❌ not exposed |
| 4 | slash server wildcard | `["test-mcp/*"]` | ✅ exposed |
| 5 | source wildcard | `["mcp:*"]` | ✅ exposed |

The runtime resolves source-qualified, server, namespaced, and exact-name
selectors against tool metadata. A dash glob is not part of the grammar.

The program exits `0` when all six forms match the table, `1` on a behavior
mismatch, and `2` on a setup error.

> Related to tracked issue
> [github/copilot-sdk#861](https://github.com/github/copilot-sdk/issues/861):
> The original bug was that namespaced and server-wildcard selectors did not
> expose MCP tools. Those forms pass with the required CLI artifact 1.0.79.
> The issue was closed as completed upstream on 2026-08-29.

## Bundled MCP server

[`test-mcp-server/`](test-mcp-server) is a tiny .NET stdio server implementing
just enough of the MCP JSON-RPC protocol (`initialize`, `tools/list`,
`tools/call`) to expose the three echo tools. The repro launches it with
`dotnet run --project test-mcp-server -c Release`.

## Run

```bash
# auto-downloads the matching CLI (or reuses one found in a parent folder)
dotnet run -c Release

# or point it at an existing CLI binary
dotnet run -c Release -- C:\path\to\copilot.exe
```
