# McpToolAgentScopedExplicit

Self-contained reproduction of a **GitHub Copilot SDK** bug where a custom
agent that lists **explicit, fully-prefixed MCP tool names** in its `Tools`
still does not surface those MCP tools to the model.

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

A custom agent lists all three MCP tools by their full prefixed names and is
selected after session creation:

```csharp
var agent = new CustomAgentConfig
{
    Name  = "mcp-agent-explicit",
    Tools = new List<string> { "test-mcp-alpha", "test-mcp-beta", "test-mcp-gamma" },
    ...
};
...
await session.Rpc.Agent.SelectAsync("mcp-agent-explicit");
```

The model is then asked to list every tool it can see.

## Expected vs actual

- **Expected:** the model sees exactly `test-mcp-alpha`, `test-mcp-beta`,
  `test-mcp-gamma`.
- **Actual:** only built-in tools are reported — the agent-scoped explicit MCP
  tool names do not surface the MCP tools.

The test exits `1` when any expected MCP tool is missing.

> Related to tracked issues
> [github/copilot-sdk#860](https://github.com/github/copilot-sdk/issues/860) /
> [github/copilot-sdk#1019](https://github.com/github/copilot-sdk/issues/1019):
> explicit MCP tool names in agent scope still do not expose MCP tools.
>
> Note: the same explicit names **without** agent scope (a plain session with
> `AvailableTools` listing the prefixed names) works — see the suite's
> `McpToolExplicit` test, which passes.

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
