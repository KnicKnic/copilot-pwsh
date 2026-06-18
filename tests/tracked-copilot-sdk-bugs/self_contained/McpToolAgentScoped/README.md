# McpToolAgentScoped

Self-contained reproduction of a **GitHub Copilot SDK** bug where a custom
agent's `Tools` entry that uses a **bare MCP server name** is not expanded to
the server's MCP tools.

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

A custom agent lists the **bare server name** in its `Tools` and is selected
after session creation:

```csharp
var agent = new CustomAgentConfig
{
    Name  = "mcp-agent",
    Tools = new List<string> { "test-mcp" },   // bare MCP server name
    ...
};
...
await session.Rpc.Agent.SelectAsync("mcp-agent");
```

The model is then asked to list every tool it can see.

## Expected vs actual

- **Expected:** the agent's tool scope expands `test-mcp` to `test-mcp-alpha`,
  `test-mcp-beta`, `test-mcp-gamma`, and the model sees those three tools.
- **Actual:** only built-in tools are reported — the bare server name is not
  expanded to MCP tools.

The test exits `1` when any expected MCP tool is missing.

> Tracked issue
> [github/copilot-sdk#860](https://github.com/github/copilot-sdk/issues/860):
> agent `Tools` entries using bare MCP server names are not expanded to MCP tools.

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
