# McpSessionAvailableTools

Self-contained compatibility matrix for **GitHub Copilot SDK**
`SessionConfig.AvailableTools` MCP selector forms.

## Scenario

| | |
|---|---|
| **SDK** | `GitHub.Copilot.SDK` 1.0.11 |
| **CLI** | 1.0.79 (auto-downloaded) |
| **Model** | `claude-haiku-4.5` |
| **Status** | Passes |

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
> expose MCP tools. Those forms pass on CLI/runtime 1.0.79.

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
