# McpSessionAvailableTools

Self-contained reproduction of a **GitHub Copilot SDK** bug where MCP server
tools are **not exposed** to the model for most `SessionConfig.AvailableTools`
selector forms. A single program exercises the same bundled MCP server against
every selector form and reports which ones expose the tools.

## Scenario

| | |
|---|---|
| **SDK** | `GitHub.Copilot.SDK` 1.0.2 |
| **CLI** | 1.0.64-0 (auto-downloaded) |
| **Model** | `claude-haiku-4.5` |
| **Status** | ❌ Fails — bug reproduces (known) |

A local stdio MCP server is bundled in [`test-mcp-server/`](test-mcp-server)
and registered under the name `test-mcp` (tools `alpha`/`beta`/`gamma`, exposed
by the CLI as `test-mcp-alpha`, `test-mcp-beta`, `test-mcp-gamma`).

The program creates one session per `AvailableTools` selector form and asks the
model to list its tools:

| # | `AvailableTools` form | Example | Result |
|---|-----------------------|---------|--------|
| 0 | explicit dashed names (baseline) | `["test-mcp-alpha", ...]` | ✅ exposed |
| 1 | explicit namespaced / slash names | `["test-mcp/alpha", ...]` | ❌ not exposed |
| 2 | dash wildcard | `["test-mcp-*"]` | ❌ not exposed |
| 3 | slash wildcard | `["test-mcp/*"]` | ❌ not exposed |

At the session level the CLI only honors the **dashed explicit** names; every
other form fails to expose the MCP tools. (The slash form works at the **agent**
level, but not here.)

## Expected vs actual

- **Expected (once fixed):** every form exposes `test-mcp-alpha`,
  `test-mcp-beta`, `test-mcp-gamma`.
- **Actual:** only the dashed-explicit baseline exposes them; forms 1–3 report
  only built-in tools.

The program exits `1` while **any** of the bug forms (1–3) still fails to expose
the MCP tools, and `0` only once every form is fixed. This way the SDK team can
run one repro and watch each form flip to "EXPOSED" as fixes land.

> Related to tracked issue
> [github/copilot-sdk#861](https://github.com/github/copilot-sdk/issues/861):
> MCP server tools are not exposed via session `AvailableTools` namespaced or
> wildcard selectors.

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
