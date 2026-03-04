# CopilotShell

A cross-platform PowerShell 7+ module that wraps the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) to give you full programmatic control over GitHub Copilot CLI — including **custom system messages**, **streaming**, **session management**, and **timeout/turn limits**.

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 10.0+ (RC/preview — required by GitHub.Copilot.SDK) |
| PowerShell | 7.6 preview (runs on .NET 10) |
| Copilot CLI | Installed and on PATH (or pass `-CliPath`) |

> **Windows PowerShell 5.x is not supported.** Use `pwsh` (PowerShell 7.6+ preview).
>
> The GitHub.Copilot.SDK NuGet package requires `System.Text.Json 10.x`, which
> means the host process must run on .NET 10. PowerShell 7.6 preview is the
> first pwsh release on .NET 10.

## Known limitations
1. ~~mcp env args dont work~~ — **Workaround implemented:** all local MCP servers are automatically launched through `mcp-wrapper`, which handles env var propagation and persistent "zombie" daemon connections for eligible servers. Use `-NoMcpWrapper` to disable. See [github/copilot-sdk#163](https://github.com/github/copilot-sdk/issues/163) and [MCP-WRAPPER.md](MCP-WRAPPER.md).
1. Have a hack to load MCPs to get their name for filtering
1. I dont know how to start with an agent -- need to spend more time here.

## Quick Install

```powershell
# 1. Install .NET 10 SDK
winget install Microsoft.DotNet.SDK.Preview

# 2. Install PowerShell 7.6 preview (runs on .NET 10)
winget install Microsoft.PowerShell.Preview

# 3. Clone and build (run in pwsh-preview)
git clone <repo-url> copilot-sdk
cd copilot-sdk
./build.ps1 -Install

# 4. Import
Import-Module CopilotShell
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

## VS Code Tip

To make pwsh-preview the default terminal in VS Code (so it appears when you click the `+` button):

1. Open Settings (JSON): `Ctrl+Shift+P` → "Preferences: Open User Settings (JSON)"
2. Add:

```json
"terminal.integrated.profiles.windows": {
    "PowerShell Preview": {
        "path": "C:\\Program Files\\PowerShell\\7-preview\\pwsh.exe",
        "icon": "beaker"
    }
},
"terminal.integrated.defaultProfile.windows": "PowerShell Preview"
```

After saving, new terminals will use pwsh-preview automatically.

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

### Tool filtering with dynamic MCP discovery

The Copilot CLI doesn't support wildcards in `-AvailableTools` — it needs exact tool names like `ado-wiki_get_page`. CopilotShell works around this by **dynamically querying MCP servers** for their tool lists using the MCP `tools/list` protocol.

When you pass a bare server name or wildcard pattern alongside `-McpConfigFile`, CopilotShell will:
1. Start each referenced MCP server process temporarily
2. Perform the MCP handshake (`initialize` → `initialized` → `tools/list`)
3. Collect the exact tool names (e.g., `grafana-mcp-search_dashboards`)
4. Expand your patterns against the discovered tools
5. Kill the discovery process (the Copilot CLI starts its own instance for the actual session)

**Forward slashes are always normalized to dashes** — use whichever you prefer:

```powershell
# Enable all ADO tools — discovered dynamically from the MCP server
Invoke-Copilot "List my work items" `
    -AvailableTools @('ado') `
    -McpConfigFile ./mcp-config.json

# Wildcard patterns work too
Invoke-Copilot "Search the wiki" `
    -AvailableTools @('ado-wiki_*', 'powershell', 'view') `
    -McpConfigFile ./mcp-config.json

# Multiple servers
Invoke-Copilot "Query the database and check dashboards" `
    -AvailableTools @('kusto-mcp', 'grafana-mcp', 'powershell') `
    -McpConfigFile ./mcp-config.json

# Use -Verbose to see discovery results
Invoke-Copilot "hello" `
    -AvailableTools @('ado', 'kusto-mcp', 'grafana-mcp') `
    -McpConfigFile ./mcp-config.json `
    -Verbose
# VERBOSE: Discovering tools from MCP servers: grafana-mcp, ado, kusto-mcp
# VERBOSE:   grafana-mcp: 24 tools discovered
# VERBOSE:   ado: 82 tools discovered
# VERBOSE:   kusto-mcp: 17 tools discovered
```

**Supported patterns:** (both `-` and `/` separators work)
- `ado` or `ado-*` — bare server name or wildcard: all tools from that MCP server
- `ado-wiki_*` — sub-wildcard: only tools matching the prefix
- `kusto-mcp-kusto_query` — exact tool name: passed through as-is
- `view`, `edit`, `grep` — built-in CLI tools: passed through as-is
- `powershell` — shorthand: expands to `write_powershell`, `read_powershell`, `stop_powershell`, `list_powershell`

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
│   ├── mcp-wrapper.csproj     # .NET 10 console app
│   ├── Program.cs             # Entry point: arg parsing, direct/zombie modes
│   ├── Daemon.cs              # Zombie daemon: socket listener, child lifecycle
│   ├── ManagedChild.cs        # Child process: JSON-RPC multiplexing, ID remapping
│   └── ZombieEligibility.cs   # Regex-based zombie eligibility checker
└── src/
    ├── CopilotShell.csproj    # .NET 10 project
    ├── CopilotShell.psd1      # PowerShell module manifest
    ├── AsyncPSCmdlet.cs       # Base class for async cmdlets
    ├── ClientCmdlets.cs       # New/Start/Stop/Remove/Test-CopilotClient
    ├── SessionCmdlets.cs      # New/Get/Resume/Remove/Stop/Disconnect-CopilotSession + Get-CopilotSessionMessages
    ├── MessageCmdlets.cs      # Send-CopilotMessage, Wait-CopilotSession
    ├── InvokeCopilotCommand.cs # Invoke-Copilot (one-shot convenience)
    ├── CliPathResolver.cs     # Auto-detect bundled copilot.exe
    ├── McpConfigLoader.cs     # Parse MCP JSON configs
    ├── McpToolDiscovery.cs    # Dynamic MCP tool discovery via tools/list protocol
    ├── McpWrapperHelper.cs    # Wraps MCP configs to use mcp-wrapper
    ├── ToolFilterHelper.cs    # Tool filtering with wildcard expansion
    └── Format-CopilotEvent.ps1 # Streaming event formatter (exported function)
```

## Cross-Platform

This module runs on Windows, macOS, and Linux wherever .NET 10 and PowerShell 7.5+ are available. No platform-specific dependencies are used.

## License

MIT
