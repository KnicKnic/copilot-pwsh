# McpToolAgentScopedExplicitSession

Self-contained reproduction of a **GitHub Copilot SDK** bug where MCP tools are
not exposed even when explicit, fully-prefixed MCP tool names are listed in
**both** the agent's `Tools` **and** `SessionConfig.AvailableTools`.

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

The same three prefixed names are supplied in two places — the agent's `Tools`
and the session's `AvailableTools` — and the agent is selected after session
creation:

```csharp
var agent = new CustomAgentConfig
{
    Name  = "mcp-agent-explicit-session",
    Tools = new List<string> { "test-mcp-alpha", "test-mcp-beta", "test-mcp-gamma" },
    ...
};

var sessionConfig = new SessionConfig
{
    McpServers     = new() { ["test-mcp"] = mcpServer },
    CustomAgents   = new() { agent },
    AvailableTools = new List<string> { "test-mcp-alpha", "test-mcp-beta", "test-mcp-gamma" },
    ...
};
...
await session.Rpc.Agent.SelectAsync("mcp-agent-explicit-session");
```

The model is then asked to list every tool it can see.

## Expected vs actual

- **Expected:** the model sees exactly `test-mcp-alpha`, `test-mcp-beta`,
  `test-mcp-gamma`.
- **Actual:** only built-in tools are reported — even with the names supplied
  in both the agent scope and the session `AvailableTools`, no MCP tools are
  exposed.

The test exits `1` when any expected MCP tool is missing.

> Related to tracked issues
> [github/copilot-sdk#860](https://github.com/github/copilot-sdk/issues/860) /
> [github/copilot-sdk#1019](https://github.com/github/copilot-sdk/issues/1019):
> explicit MCP tool names in agent scope still do not expose MCP tools.

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
