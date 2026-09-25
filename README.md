# CopilotShell

A cross-platform PowerShell 7+ module that wraps the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) to give you full programmatic control over GitHub Copilot CLI — including **custom system messages**, **streaming**, **session management**, and **timeout/turn limits**.

*Related:* [Copilot Dash](https://github.com/KnicKnic/copilot-dash) a visualizer with browser extension that can embed runs & results along side websites.

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 8.0+ |
| PowerShell | 7.4+ (runs on .NET 8) |
| Copilot CLI | Installed and on PATH (or pass `-CliPath`) |

> **Windows PowerShell 5.x is not supported.** Use `pwsh` (PowerShell 7.4+).

## MCP compatibility

The historical MCP environment-variable issue [github/copilot-sdk#163](https://github.com/github/copilot-sdk/issues/163) is **closed as completed upstream**. CopilotShell retains `mcp-wrapper` for explicit env/cwd forwarding and persistent "zombie" daemon connections for eligible servers; use `-NoMcpWrapper` to disable it. See [MCP-WRAPPER.md](MCP-WRAPPER.md) and the [tracked issue status](#tracked-upstream-sdk-issues).

## Quick Install

```powershell
Install-Module CopilotShell
```

The Copilot CLI binary is **automatically downloaded** on first use — no manual setup needed. It's cached in your user profile so subsequent runs start instantly.

> **Corporate npm registries:** the download honours your npm configuration, so a mirrored registry works without extra setup. The registry is resolved from `npm_config_registry`/`NPM_CONFIG_REGISTRY`, then your user `.npmrc`, then the global `.npmrc` — falling back to `https://registry.npmjs.org`. A scoped `@github:registry` entry takes precedence over a plain `registry` entry, as in npm. To override explicitly:
> ```powershell
> $env:COPILOTSHELL_NPM_REGISTRY = 'https://your-mirror.example.com/npm'
> ```
> A project-local `.npmrc` is intentionally ignored at runtime, since the registry decides where an executable is downloaded from. Builds from source resolve the same settings (plus the repository's own `.npmrc`) via [`Directory.Build.props`](Directory.Build.props).

> **First-run setup:** Authenticate with GitHub before first use:
> ```powershell
> Connect-Copilot
> ```
> This downloads the correct CLI version (if needed) and runs the OAuth device flow. Follow the prompts to sign in with your GitHub account. Once authenticated, CopilotShell reuses the cached token.
>
> For GitHub Enterprise Cloud with data residency:
> ```powershell
> Connect-Copilot -GitHubHost "https://example.ghe.com"
> ```

> Requires [PowerShell 7.4+](https://github.com/PowerShell/PowerShell/releases). Install with: `winget install Microsoft.PowerShell`

### From source

```powershell
git clone https://github.com/KnicKnic/copilot-pwsh.git
cd copilot-pwsh
./build.ps1 -Clean -Install
```

## Rebuild & Reinstall

```powershell
./build.ps1 -Clean -Install
```

Then reload in any open pwsh session:

```powershell
Remove-Module CopilotShell -ErrorAction SilentlyContinue
Import-Module CopilotShell
```

## Cmdlets

### Client Management

| Cmdlet | Description |
|---|---|
| `New-CopilotClient` | Create and start a CopilotClient |
| `Start-CopilotClient` | Start a client created with `-AutoStart $false` |
| `Stop-CopilotClient` | Gracefully stop a client (`-Force` for immediate) |
| `Remove-CopilotClient` | Dispose a client |
| `Test-CopilotClient` | Ping the server |

### Session Management

| Cmdlet | Description |
|---|---|
| `New-CopilotSession` | Create a session with model, system message, streaming, etc. |
| `Get-CopilotSession` | List all sessions (or filter by `-SessionId`) |
| `Resume-CopilotSession` | Resume a previous session by ID |
| `Remove-CopilotSession` | Delete a session |
| `Get-CopilotSessionMessages` | Get all messages/events from a session |
| `Stop-CopilotSession` | Abort the current operation |
| `Disconnect-CopilotSession` | Dispose local session resources without deleting from server (resumable) |
| `Wait-CopilotSession` | Wait for a session to become idle |

### Messaging

| Cmdlet | Description |
|---|---|
| `Send-CopilotMessage` | Send a prompt; returns final text or streams events with `-Stream` |
| `Format-CopilotEvent` | Format streaming events with colors and icons (filter function) |

### Authentication

| Cmdlet | Description |
|---|---|
| `Connect-Copilot` | Authenticate with GitHub Copilot via OAuth device flow (`-GitHubHost` for GHE) |

### Convenience

| Cmdlet | Description |
|---|---|
| `Invoke-Copilot` | One-shot: prompt → response (handles client/session lifecycle) |

## Examples

### One-liner

```powershell
Invoke-Copilot "What is the capital of France?"
```

### Custom system message

```powershell
Invoke-Copilot "Tell me a joke" `
    -SystemMessage "You are a comedian who only tells programming jokes." `
    -SystemMessageMode Replace
```

### Streaming output

```powershell
# Simple colored console output
$session = New-CopilotSession $client -Stream
Send-CopilotMessage $session "Write a short story" -Stream | Format-CopilotEvent

# Console + colored log file
Send-CopilotMessage $session "test" -Stream | Format-CopilotEvent -LogFile "session.log"
```

The `Format-CopilotEvent` function automatically formats all streaming events with colors and icons, and can save an ANSI-colored log file. See [STREAMING.md](STREAMING.md) for details.

### Full client/session workflow

```powershell
# Create client
$client = New-CopilotClient

# Create session with custom system prompt
$session = New-CopilotSession $client `
    -Model gpt-5 `
    -ReasoningEffort high `
    -SystemMessage "You are a security auditor." `
    -SystemMessageMode Replace `
    -Stream

# Send messages
Send-CopilotMessage $session "Review this code for vulnerabilities" -Stream

# Send another in the same session
Send-CopilotMessage $session "Now suggest fixes" -Stream

# List all sessions
Get-CopilotSession $client

# Get message history
Get-CopilotSessionMessages $session

# Clean up
Remove-CopilotSession -Client $client -SessionId $session.SessionId
Stop-CopilotClient $client
Remove-CopilotClient $client
```

### Timeout and turn limits

```powershell
Invoke-Copilot "Refactor the entire codebase" `
    -TimeoutSeconds 120 `
    -MaxTurns 5
```

### Attach files

```powershell
Invoke-Copilot "Explain this code" -Attachment ./main.cs, ./utils.cs
```

### Tool filtering

Custom agent files are discovered automatically from the same local locations Copilot CLI uses:
the repository `.github/agents` directory first, then `~/.copilot/agents`. Use `-Agent`
(`-AgentNames`/`-Agents`) to load named agents without selecting one; each name is resolved
as `<agent-folder>/<agent-name>.agent.md`. Use `-AgentFolders` to override the ordered
discovery folders, and `-DefaultAgent` to select the session's starting agent.
`-McpConfigFile` accepts one or more files in Copilot CLI `mcpServers` JSON or
VS Code `.vscode/mcp.json` `servers` JSON; files are merged in order, and duplicate
server names keep the first definition.

`-AvailableTools` restricts which tools a session can use. Selectors are passed to the
Copilot runtime verbatim. SDK 1.0.11 / runtime 1.0.79 supports:

- **Source-qualified selectors:** `builtin:view`, `builtin:*`, `mcp:ado-wiki_get_page`,
  `mcp:*`, `custom:my_tool`, and `custom:*`. These are preferred when the source boundary
  matters because a tool from another source cannot match by name collision.
- **MCP server selectors:** `ado` or `ado/*` selects every tool from that server;
  `ado/wiki_get_page` selects one namespaced tool. Exact dashed wire names such as
  `ado-wiki_get_page` also work.
- **Legacy dash globs are literals:** `ado-*` is not a wildcard. Use `ado/*` for one
  server or `mcp:*` for all MCP tools.
- **Agent level** (`CustomAgentConfig.Tools` / `.agent.md` `tools:`): this uses a separate
  alias grammar. Exact built-in or dashed wire names, bare MCP server names, `ado/*`, and
  `ado/wiki_get_page` work. Source-qualified forms such as `mcp:*` are session-only.
  Prefer the slash forms because they preserve the MCP namespace.

A session-level restriction is a **hard cap**: it cascades to every agent (and any subagent
spawned via `task`). An agent can only ever *narrow* the session's tool set, never widen it.

```powershell
# Restrict a session to specific MCP tools + a builtin
Invoke-Copilot "Search the wiki" `
    -AvailableTools @('ado/wiki_get_page', 'ado/wiki_search', 'builtin:view') `
    -McpConfigFile ./mcp-config.json

# Allow every MCP tool, but no built-in or custom tools
Invoke-Copilot "Use the connected services" `
    -AvailableTools @('mcp:*') `
    -McpConfigFile ./mcp-config.json

# Scope via an agent instead (slash form, wildcard supported)
New-CopilotSession `
    -CustomAgents ([GitHub.Copilot.CustomAgentConfig]@{ Name = 'ado-agent'; Tools = @('ado/*', 'task') }) `
    -DefaultAgent ado-agent `
    -McpConfigFile ./mcp-config.json
```

#### Isolated default agent

When no agent is specified, `-IsolatedDefaultAgent` restricts the session — and therefore the
built-in default agent it caps — to an *isolated* builtin tool set:
`GitHub.Copilot.BuiltInTools.Isolated` minus `exit_plan_mode` and `ask_user` (the interactive
planning/prompting tools). `send_inbox` and `context_board` are **kept** so the agent can still
participate in the inbox / dynamic-context-board machinery. The result is an
orchestration-focused default (`task`, `read_agent`, `write_agent`, `list_agents`,
`task_complete`, `send_inbox`, `context_board`, `skill`) with **no** file/shell/repo/MCP tools.
CopilotShell emits these as `builtin:`-qualified selectors so an MCP or custom tool with the same
name cannot cross the isolation boundary.

```powershell
# Default agent restricted to the isolated builtin tools
Invoke-Copilot "Plan and delegate this work" -IsolatedDefaultAgent
```

The switch is **ignored when an agent is specified** (via `-DefaultAgent` or a prompt file) — in that
case the agent's own `Tools` govern its scope. Because the restriction is applied at the session
level, it is a hard cap that cascades to any subagents spawned via `task`.

### Resume a session

```powershell
$client = New-CopilotClient
$sessions = Get-CopilotSession $client
$session = Resume-CopilotSession -Client $client -SessionId $sessions[0].SessionId
Send-CopilotMessage $session "Continue where we left off"
```

### Disconnect and resume later

```powershell
# Free local resources without deleting the session
$id = $session.SessionId
Disconnect-CopilotSession $session

# Later, reconnect to the same session
$session = Resume-CopilotSession $client $id
Send-CopilotMessage $session "Pick up where we left off"
```

### MCP wrapper (env var fix + zombie daemon)

All local MCP servers are automatically launched through `mcp-wrapper` — a transparent proxy that handles env var propagation and, for eligible servers, manages persistent "zombie" daemon connections that survive session boundaries. This means no startup delay and preserved auth tokens across sessions.

```powershell
# Wrapper is applied automatically to all local MCP servers
New-CopilotSession $client -McpConfigFile ./mcp-config.json

# Disable wrapping if you don't need it
New-CopilotSession $client -McpConfigFile ./mcp-config.json -NoMcpWrapper

# Use -Verbose to see which servers are wrapped
New-CopilotSession $client -McpConfigFile ./mcp-config.json -Verbose
# VERBOSE: MCP servers wrapped via: C:\...\mcp-wrapper.exe

# Stop the zombie daemon (kills all persistent MCP servers)
# Useful when auth tokens become stale
Reset-CopilotMcpDaemon
Reset-CopilotMcpDaemon -Force  # if graceful shutdown fails
```

Servers matching certain patterns (Azure DevOps, Grafana, EV2, Fabric RTI) are automatically routed through the zombie daemon. See [MCP-WRAPPER.md](MCP-WRAPPER.md) for full details.

## Project Structure

```
copilot-sdk/
├── build.ps1                  # Build & install script
├── install.ps1                # Elevated install to Program Files
├── reimport.ps1               # Quick dev reload
├── Format-CopilotEvent.ps1    # Streaming event formatter with color logging
├── goal.md                    # Project goals / spec
├── README.md                  # This file
├── STREAMING.md               # Detailed streaming guide
├── TOOLS.md                   # Tool pattern reference
├── .gitignore
├── MCP-WRAPPER.md             # Detailed mcp-wrapper documentation
├── mcp-wrapper/               # MCP proxy with zombie daemon support
│   ├─ mcp-wrapper.csproj     # .NET 8 console app
│   ├── Program.cs             # Entry point: arg parsing, direct/zombie modes
│   ├── Daemon.cs              # Zombie daemon: socket listener, child lifecycle
│   ├── ManagedChild.cs        # Child process: JSON-RPC multiplexing, ID remapping
│   └── ZombieEligibility.cs   # Regex-based zombie eligibility checker
└── src/
    ├─ CopilotShell.csproj    # .NET 8 project
    ├── CopilotShell.psd1      # PowerShell module manifest
    ├── AsyncPSCmdlet.cs       # Base class for async cmdlets
    ├── ClientCmdlets.cs       # New/Start/Stop/Remove/Test-CopilotClient
    ├── SessionCmdlets.cs      # New/Get/Resume/Remove/Stop/Disconnect-CopilotSession + Get-CopilotSessionMessages
    ├── MessageCmdlets.cs      # Send-CopilotMessage, Wait-CopilotSession
    ├── InvokeCopilotCommand.cs # Invoke-Copilot (one-shot convenience)
    ├── LoginCmdlet.cs         # Connect-Copilot (authentication)
    ├── CliPathResolver.cs     # Auto-detect bundled copilot.exe
    ├── McpConfigLoader.cs     # Parse MCP JSON configs
    ├── McpWrapperHelper.cs    # Wraps MCP configs to use mcp-wrapper
    ├── SessionSetupHelper.cs  # Wire agents, MCP servers & tool filters onto a session
    └── Format-CopilotEvent.ps1 # Streaming event formatter (exported function)
```

## Cross-Platform

This module runs on Windows, macOS, and Linux wherever .NET 8+ and PowerShell 7.4+ are available. No platform-specific dependencies are used.

## References

### GitHub Copilot SDK
- [GitHub Copilot SDK repository](https://github.com/github/copilot-sdk)
- [Copilot SDK .NET README](https://github.com/github/copilot-sdk/blob/main/dotnet/README.md)
- [GitHub.Copilot.SDK on NuGet](https://www.nuget.org/packages/GitHub.Copilot.SDK)

### Project documentation
- [MCP-WRAPPER.md](MCP-WRAPPER.md) — env var fix + zombie daemon details
- [STREAMING.md](STREAMING.md) — detailed streaming guide
- [TOOLS.md](TOOLS.md) — tool pattern reference
- [goal.md](goal.md) — project goals / spec

### Tooling
- [PowerShell 7.4+ releases](https://github.com/PowerShell/PowerShell/releases)
- [Copilot Dash](https://github.com/KnicKnic/copilot-dash) — run/result visualizer with browser extension
- [copilot-cleaner](https://github.com/KnicKnic/copilot-cleaner) — local cross-platform C# Avalonia app for inspecting and cleaning GitHub Copilot session-state folders

### Tracked upstream SDK issues

All five tracked issues are **closed as completed upstream**, checked on **2026-09-14**. This records the original reports' resolution, not a guarantee for every SDK integration or transport.

| Issue | Resolution | Closed (UTC) |
|---|---|---|
| [github/copilot-sdk#163](https://github.com/github/copilot-sdk/issues/163) | MCP environment-variable propagation fixed upstream; the wrapper remains useful for env/cwd forwarding and persistent servers. | 2026-02-17 |
| [github/copilot-sdk#859](https://github.com/github/copilot-sdk/issues/859) | Agent preselection through `SessionConfig.Agent` enforces `CustomAgentConfig.Tools`. | 2026-05-16 |
| [github/copilot-sdk#860](https://github.com/github/copilot-sdk/issues/860) | Agent MCP selectors resolve bare server names, slash wildcards, namespaced names, and exact wire names on the pinned release. | 2026-08-29 |
| [github/copilot-sdk#861](https://github.com/github/copilot-sdk/issues/861) | Session MCP selectors resolve supported forms; a dash glob such as `test-mcp-*` remains an unsupported literal. | 2026-08-29 |
| [github/copilot-sdk#1019](https://github.com/github/copilot-sdk/issues/1019) | Upstream supports `DefaultAgentConfig.ExcludedTools` for default-agent-only exclusions. CopilotShell's existing custom coordinator pattern uses `-DefaultAgent` with agent `Tools`. | 2026-09-08 |

See the [regression suite's completion record](tests/tracked-copilot-sdk-bugs/README.md#tracked-issues) for local results and coverage limits. `-IsolatedDefaultAgent` still applies a **session-wide** cap; it is not the SDK's default-agent-only exclusion feature.

## License

MIT
