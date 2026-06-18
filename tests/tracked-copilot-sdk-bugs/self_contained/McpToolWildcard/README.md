# McpToolWildcard

Self-contained reproduction of a **GitHub Copilot SDK** bug where MCP server
tools are **not exposed** to the model when `SessionConfig.AvailableTools` uses
a **server wildcard** selector.

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

The session sets `AvailableTools` to a server wildcard:

```csharp
var sessionConfig = new SessionConfig
{
    McpServers   = new() { ["test-mcp"] = mcpServer },
    AvailableTools = new List<string> { "test-mcp/*" },   // wildcard selector
    ...
};
```

The model is then asked to list every tool it can see.

## Expected vs actual

- **Expected:** `test-mcp-alpha`, `test-mcp-beta`, `test-mcp-gamma` are exposed.
- **Actual:** only built-in tools (`git`, `curl`, `gh`, `grep`, `glob`,
  `view`, `edit`, `create`, `powershell`) are reported — none of the MCP tools.

The test exits `1` when any expected MCP tool is missing.

> Related to tracked issue
> [github/copilot-sdk#861](https://github.com/github/copilot-sdk/issues/861):
> MCP server tools are not exposed via `AvailableTools` wildcard selectors.

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

Exit codes: `0` = MCP tools exposed (bug fixed), `1` = bug reproduces, `2` = setup error.
