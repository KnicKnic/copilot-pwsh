# MCP Wrapper

A transparent MCP server proxy with an integrated zombie daemon for persistent server connections. Built as a single .NET 8 console application that operates in two modes: **direct proxy** for simple env/cwd forwarding, and **zombie mode** for keeping MCP servers alive across Copilot sessions.

## Problem

The GitHub Copilot SDK doesn't propagate environment variables or working directories to MCP server processes ([copilot-sdk#163](https://github.com/github/copilot-sdk/issues/163)). Additionally, MCP servers are started fresh for every Copilot session — losing authentication tokens and incurring startup latency each time.

## Solution

`mcp-wrapper` sits between the Copilot SDK and MCP server processes:

```
┌─────────────┐        ┌──────────────┐        ┌────────────┐
│  Copilot SDK │──────▶│  mcp-wrapper  │──────▶│  MCP Server │
│  (parent)    │ stdio  │  (proxy)      │ stdio  │  (child)    │
└─────────────┘        └──────────────┘        └────────────┘
```

- **Direct mode**: Sets env vars and cwd, then proxies stdin/stdout/stderr transparently.
- **Zombie mode**: Routes traffic through a background daemon that keeps MCP servers alive indefinitely.

## Architecture

```
                          ┌──────────────────────────────────┐
                          │         Zombie Daemon             │
                          │    (background zombie process)    │
Session 1:                │                                  │
┌──────────┐  socket      │  ┌──────────────────────────┐   │
│mcp-wrapper├─────────────┤  │ ManagedChild (ADO)        │   │
└──────────┘              │  │  • JSON-RPC multiplexing  │   │
                          │  │  • ID remapping           │   │
Session 2:                │  │  • stdin serialization    │   │
┌──────────┐  socket      │  └──────────────────────────┘   │
│mcp-wrapper├─────────────┤                                  │
└──────────┘              │  ┌──────────────────────────┐   │
                          │  │ ManagedChild (Grafana)    │   │
Session 3:                │  │  • Reuses if same config  │   │
┌──────────┐  socket      │  │  • Cached init response   │   │
│mcp-wrapper├─────────────┤  └──────────────────────────┘   │
└──────────┘              │                                  │
                          └──────────────────────────────────┘

Direct mode (non-eligible servers):
┌──────────┐       ┌────────────┐
│mcp-wrapper├─────▶│  MCP Server │   (transparent proxy, no daemon)
└──────────┘ stdio └────────────┘
```

### Components

| File | Purpose |
|---|---|
| `Program.cs` | Entry point — argument parsing, mode selection, direct proxy, zombie client |
| `Daemon.cs` | Zombie daemon — socket listener, client handshake, child lifecycle |
| `ManagedChild.cs` | Single MCP server — process management, JSON-RPC multiplexing, ID remapping |
| `ZombieEligibility.cs` | Regex-based eligibility checker for zombie mode |

## Modes

### Direct Proxy Mode

```
mcp-wrapper [--env KEY=VALUE]... [--cwd DIR] [--no-zombie] -- <command> [args...]
```

Transparent stdin/stdout/stderr pass-through. Sets environment variables and working directory before launching the MCP server process, then bidirectionally pipes all I/O. Used when:

- The server doesn't match any zombie eligibility pattern
- `--no-zombie` is explicitly specified
- The zombie daemon fails to start (automatic fallback)

On Windows, commands without file extensions (e.g., `npx`, `uvx`) are automatically routed through `cmd.exe /c` since they're typically `.cmd` batch files.

### Zombie Mode

```
mcp-wrapper [--env KEY=VALUE]... [--cwd DIR] -- <command> [args...]
```

When the command and arguments match zombie eligibility patterns, `mcp-wrapper` connects to a background daemon process via Unix domain socket. The daemon manages the actual MCP server, keeping it alive across sessions.

**Connection flow:**

1. Check if daemon is running (try connecting to `ctrl.sock`)
2. If not, spawn the daemon as a detached zombie process
3. Wait up to 10 seconds for daemon to become available
4. Send a JSON handshake identifying the desired MCP server
5. Daemon finds or creates the child MCP server process
6. Bidirectionally proxy stdin/stdout through the socket connection
7. On disconnect, the MCP server stays alive for future clients

**Fallback**: If the daemon can't be reached after 10 seconds, automatically falls back to direct mode.

### Daemon Mode (Internal)

```
mcp-wrapper --daemon
```

Runs the zombie daemon process. Not intended for direct invocation — `mcp-wrapper` spawns this automatically when a zombie-eligible server is requested and no daemon is running.

The daemon:
- Listens on a Unix domain socket for client connections
- Manages a pool of long-lived MCP server child processes
- Each child is keyed by a SHA256 hash of `(command + args + env + cwd)`
- Multiplexes JSON-RPC messages between multiple clients and shared servers
- Caches `initialize` responses for instant reconnection
- Logs to `daemon.log` in the runtime directory

### Stop Mode

```
mcp-wrapper --stop
```

Sends a shutdown signal to the running daemon. All managed MCP servers are terminated and runtime files are cleaned up.

## Zombie Eligibility

Not all MCP servers benefit from persistent connections. Eligibility is controlled by regex patterns matched against the command string and each argument:

| Pattern | Matches |
|---|---|
| `.*@azure-devops.*` | Azure DevOps MCP server (`npx -y @azure-devops/mcp-server`) |
| `.*microsoft-fabric-rti-mcp.*` | Microsoft Fabric RTI MCP |
| `.*ev2.*` | EV2 deployment MCP |
| `.*grafana.*` | Grafana MCP server |

Matching is case-insensitive. A server is eligible if **any** of its command or arguments match **any** pattern.

**Examples:**

| Command | Args | Eligible? | Why |
|---|---|---|---|
| `npx` | `-y @azure-devops/mcp-server` | Yes | Arg matches `.*@azure-devops.*` |
| `npx` | `-y @anthropic/claude-mcp` | No | No pattern matches |
| `uvx` | `grafana-mcp-server` | Yes | Arg matches `.*grafana.*` |
| `python` | `-m ev2_mcp` | Yes | Arg matches `.*ev2.*` |
| `node` | `server.js` | No | No pattern matches |

To force direct mode for an eligible server, use `--no-zombie`.

## JSON-RPC Multiplexing

The daemon supports multiple clients sharing the same MCP server simultaneously. This works through JSON-RPC 2.0 ID remapping:

```
Client A sends: {"jsonrpc":"2.0","id":1,"method":"tools/list"}
  → Daemon remaps: {"jsonrpc":"2.0","id":1001,"method":"tools/list"}

Client B sends: {"jsonrpc":"2.0","id":1,"method":"tools/call",...}
  → Daemon remaps: {"jsonrpc":"2.0","id":1002,"method":"tools/call",...}

MCP server responds: {"jsonrpc":"2.0","id":1001,"result":{...}}
  → Daemon routes to Client A, restores ID: {"jsonrpc":"2.0","id":1,"result":{...}}

MCP server responds: {"jsonrpc":"2.0","id":1002,"result":{...}}
  → Daemon routes to Client B, restores ID: {"jsonrpc":"2.0","id":1,"result":{...}}
```

- **Requests** (have `id`): IDs are remapped to unique global IDs, forwarded to the child, and the original ID is restored when the response arrives
- **Notifications** (no `id`): Forwarded as-is in both directions; server→client notifications are broadcast to all connected clients
- **Initialize**: The first client's `initialize` request is forwarded; the response is cached. Subsequent clients receive the cached response immediately without hitting the MCP server
- **Stdin serialization**: Writes to the child's stdin are serialized via `SemaphoreSlim(1,1)` to prevent byte interleaving on the single stdio pipe

## Runtime Files

The daemon stores runtime files in a platform-specific directory:

| Platform | Directory |
|---|---|
| Windows | `%LOCALAPPDATA%\mcp-host\` |
| Linux/macOS | `/tmp/mcp-host-$USER/` |

| File | Purpose |
|---|---|
| `ctrl.sock` | Unix domain socket for client↔daemon communication |
| `daemon.pid` | Daemon process ID |
| `daemon.log` | Daemon log output (timestamped, append-only) |

Stale socket files from crashed daemons are automatically cleaned up on startup.

## Integration with CopilotShell

`McpWrapperHelper` in the CopilotShell module automatically wraps **all** local MCP server configurations to use `mcp-wrapper`. The wrapping transforms:

**Before** (original MCP config):
```json
{
  "ado": {
    "command": "npx",
    "args": ["-y", "@azure-devops/mcp-server"],
    "env": { "ADO_ORG": "myorg" }
  }
}
```

**After** (wrapped for SDK):
```json
{
  "ado": {
    "command": "/path/to/mcp-wrapper",
    "args": ["--env", "ADO_ORG=myorg", "--", "npx", "-y", "@azure-devops/mcp-server"]
  }
}
```

The wrapper then internally decides whether to use zombie mode (because `@azure-devops` matches the eligibility pattern) or direct proxy mode.

### Disabling the wrapper

```powershell
# Disable wrapping entirely (env vars won't be set, no zombie mode)
New-CopilotSession $client -McpConfigFile ./config.json -NoMcpWrapper
```

## CLI Reference

```
mcp-wrapper — MCP server proxy with zombie daemon support

Usage:
  mcp-wrapper [OPTIONS] -- <command> [args...]
  mcp-wrapper --daemon
  mcp-wrapper --stop

Options:
  --env KEY=VALUE   Set an environment variable for the MCP server (repeatable)
  --cwd DIR         Set the working directory for the MCP server
  --no-zombie       Force direct proxy mode even for eligible servers
  --                Separator between wrapper options and MCP server command

Daemon management:
  --daemon          Run the zombie daemon (internal, auto-spawned)
  --stop            Send shutdown signal to a running daemon

Exit codes:
  0                 Success (or child process exit code in direct mode)
  1                 Error (no command specified, daemon failure, etc.)
```

## Daemon Protocol

Communication between the wrapper client and daemon uses a simple JSON-over-newline protocol on the Unix domain socket:

### Connect Handshake

**Client → Daemon:**
```json
{"action":"connect","command":"npx","args":["-y","@azure-devops/mcp-server"],"env":{"ADO_ORG":"myorg"},"cwd":""}
```

**Daemon → Client (new server):**
```json
{"status":"ok","reused":false}
```

**Daemon → Client (existing server):**
```json
{"status":"ok","reused":true}
```

**Daemon → Client (error):**
```json
{"status":"error","error":"reason"}
```

After a successful handshake, the socket becomes a bidirectional JSON-RPC proxy — lines sent by the client are forwarded to the MCP server (with ID remapping), and responses are routed back.

### Shutdown

**Client → Daemon:**
```json
{"action":"shutdown"}
```

The daemon terminates all child processes and exits.

## Process Lifecycle

### Daemon Spawning

| Platform | Method |
|---|---|
| Windows | `Process.Start` with `CreateNoWindow = true`, no shell execute |
| Unix | `/bin/sh -c "nohup setsid <path> --daemon >/dev/null 2>&1 &"` |

On both platforms, the daemon is a detached process that survives the parent's death — a true "zombie" process that owns the MCP servers.

### Child Process Identity

Each MCP server is uniquely identified by a SHA256 hash of its full configuration:

```
key = SHA256(command + \0 + arg1 + \0 + arg2 + \x01 + ENV_K1=V1 + \0 + ENV_K2=V2 + \x01 + cwd)[..16]
```

Two requests for the same MCP server (same command, args, env, cwd) will reuse the existing child process. Different env vars or a different cwd will spawn a new child.

### Child Process Recovery

If a child process has exited (crashed or was killed), the daemon automatically restarts it on the next client connection request. The cached `initialize` response is invalidated, and a fresh MCP handshake occurs.

## Troubleshooting

### Check if daemon is running

```powershell
# Check for daemon process
Get-Process mcp-wrapper -ErrorAction SilentlyContinue | Where-Object {
    $_.CommandLine -match '--daemon'
}

# Check PID file
$pidFile = Join-Path $env:LOCALAPPDATA 'mcp-host\daemon.pid'
if (Test-Path $pidFile) { Get-Content $pidFile }
```

### View daemon logs

```powershell
Get-Content (Join-Path $env:LOCALAPPDATA 'mcp-host\daemon.log') -Tail 50
```

### Stop the daemon

```powershell
# Easiest way — use the CopilotShell cmdlet
Reset-CopilotMcpDaemon

# Force kill if graceful shutdown fails
Reset-CopilotMcpDaemon -Force

# See what happened
Reset-CopilotMcpDaemon -Verbose

# Or use the CLI directly
mcp-wrapper --stop
```
```

### Clean up stale files

If the daemon crashed without cleanup, remove the runtime files:

```powershell
Remove-Item (Join-Path $env:LOCALAPPDATA 'mcp-host\ctrl.sock') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'mcp-host\daemon.pid') -ErrorAction SilentlyContinue
```

The daemon also self-cleans stale socket files on startup.

## Adding Zombie Eligibility Patterns

To add a new MCP server to the zombie eligibility list, add a regex pattern to `ZombieEligibility.cs`:

```csharp
private static readonly Regex[] s_patterns =
[
    new(@".*@azure-devops.*",            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    new(@".*microsoft-fabric-rti-mcp.*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    new(@".*ev2.*",                      RegexOptions.IgnoreCase | RegexOptions.Compiled),
    new(@".*grafana.*",                  RegexOptions.IgnoreCase | RegexOptions.Compiled),
    // Add new pattern here:
    new(@".*my-new-mcp.*",              RegexOptions.IgnoreCase | RegexOptions.Compiled),
];
```

Patterns are matched against both the command and every argument. Use `.*` anchoring since partial matches are common (e.g., `npx -y @scope/package-name`).

## PowerShell Cmdlet

The `Reset-CopilotMcpDaemon` cmdlet provides a convenient way to stop the daemon and recycle all MCP servers — useful when auth tokens become stale:

```powershell
# Stop daemon and all managed MCP servers (they restart fresh on next use)
Reset-CopilotMcpDaemon

# Force-kill if graceful shutdown doesn't work
Reset-CopilotMcpDaemon -Force

# See daemon log tail and cleanup details
Reset-CopilotMcpDaemon -Verbose
```

Supports `-WhatIf` and `-Confirm` via `SupportsShouldProcess`.

## Building

`mcp-wrapper` is built automatically as part of the CopilotShell build:

```powershell
./build.ps1 -Clean -Install
```

To build it standalone:

```powershell
dotnet publish mcp-wrapper/mcp-wrapper.csproj -c Release -o output/CopilotShell/
```
