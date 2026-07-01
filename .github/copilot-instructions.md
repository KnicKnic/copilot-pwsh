# Copilot Instructions — CopilotShell

## Project Overview
PowerShell 7+ binary module (C#) wrapping the GitHub Copilot SDK for .NET. Targets **net8.0** — the SDK requires .NET 8+. Runs on **pwsh 7.4+** (.NET 8 runtime). Windows PowerShell 5.x is not supported.

**pwsh** is available on PATH as `pwsh`. Use `pwsh` when running commands.

## Build & Install
- **Always rebuild and install after code changes** — run `./build.ps1 -Clean -Install`.
- `build.ps1` publishes `CopilotShell` and `mcp-wrapper` to `output/CopilotShell/`. `-Install` auto-elevates and calls `install.ps1`.
- `install.ps1` copies to `C:\Program Files\PowerShell\Modules\CopilotShell` (requires admin).
- `reimport.ps1 -Build` is for quick dev iteration: clean build + reimport into current session.
- After changing source, verify the module loads: `Import-Module CopilotShell; Get-Command -Module CopilotShell`.

## Architecture
- **AsyncPSCmdlet.cs** — Base class for all cmdlets. Uses a `SynchronizationContext` message-pump (`BlockingCollection`) to marshal async continuations back to the pipeline thread. All cmdlets that call async SDK methods must inherit from this. Never call `WriteObject`/`WriteError` from a background thread.
- **ClientCmdlets.cs** — `New/Start/Stop/Remove/Test-CopilotClient`. Uses `CliPathResolver` to auto-detect the bundled `copilot.exe`.
- **SessionCmdlets.cs** — `New/Get/Resume/Remove/Stop/Disconnect-CopilotSession`, `Get-CopilotSessionMessages`. Accepts `-McpConfigFile` as `FileInfo` (enables tab completion). `-NoMcpWrapper` disables the MCP wrapper.
- **MessageCmdlets.cs** — `Send-CopilotMessage`, `Wait-CopilotSession`. Streaming uses `await foreach` over SDK events.
- **InvokeCopilotCommand.cs** — One-shot convenience cmdlet. Manages full client+session lifecycle.
- **LoginCmdlet.cs** — `Connect-Copilot`. Resolves/downloads the correct CLI version and runs `copilot login` interactively for OAuth device flow authentication. Supports `-GitHubHost` for GHE.
- **CliPathResolver.cs** — Resolves `runtimes/<rid>/native/copilot[.exe]` relative to the assembly location.
- **McpConfigLoader.cs** — Parses MCP JSON config files into SDK `McpStdioServerConfig`/`McpHttpServerConfig` objects. Defaults `Tools=["*"]`. Skips `"disabled": true` entries.
- **SessionSetupHelper.cs** — Wires custom agents, MCP servers, and tool filters onto a `SessionConfig`. Intentionally minimal: MCP servers (when a config file is given) are always attached to the session as-is (optionally wrapped), never filtered and never attached to individual agents; the session `AvailableTools` filter is only set when `-AvailableTools` is passed (otherwise the session inherits all tools); agent `Tools` lists are passed through verbatim (e.g. `<server>/*`) with no wildcard expansion or MCP tool discovery; an agent is pre-selected only when explicitly requested (via `-Agent` or a prompt file) — a sole custom agent is never auto-selected.
- **McpWrapperHelper.cs** — Wraps ALL local MCP server configs to use `mcp-wrapper`. The wrapper internally decides zombie vs direct mode via regex matching. Applied by default; disabled with `-NoMcpWrapper`.
- **mcp-wrapper/** — Separate .NET 8 console app. Combined MCP proxy with zombie daemon support:
  - **Direct mode**: Transparent stdin/stdout/stderr proxy that sets env vars and cwd before launching the MCP server. Used for servers that don't match zombie eligibility patterns.
  - **Zombie mode**: For eligible servers (matching regex patterns like `.*@azure-devops.*`, `.*ev2.*`, `.*grafana.*`, `.*microsoft-fabric-rti-mcp.*`), connects to a background daemon via Unix domain socket. The daemon manages long-lived MCP servers keyed by unique `(command+args+env+cwd)` tuples.
  - **Daemon mode** (`--daemon`): Internal. Spawns as zombie process, listens on Unix domain socket, manages child MCP servers. Multiplexes JSON-RPC between clients with ID remapping. Caches `initialize` responses for instant reconnection. Serializes stdin writes to prevent byte interleaving.
  - **`--no-zombie` flag**: Forces direct proxy mode even for eligible servers.
  - **`--stop` flag**: Sends shutdown signal to running daemon.
  - Benefits: No MCP startup delay after first use, auth tokens persist, multiple sessions share servers.
  - Runtime files: `%LOCALAPPDATA%\mcp-host\` (Windows) or `/tmp/mcp-host-$USER/` (Unix). Logs to `daemon.log`.

## Tool Filtering
- **Session level** (`-AvailableTools`): only applied when explicitly passed; otherwise the session inherits all tools. The values are passed to the SDK verbatim (no expansion, no MCP discovery, no normalization). `-IsolatedDefaultAgent` (only when no agent is specified) restricts the session to the isolated builtin set (`GitHub.Copilot.BuiltInTools.Isolated`) **minus** `exit_plan_mode`/`ask_user` (keeping `send_inbox`/`context_board`), capping the built-in default agent to an orchestration-only tool set. A session-level restriction is a hard cap: it **cascades** to every agent/subagent (an agent can only narrow the session set, never widen it).
- **Agent level** (`CustomAgentConfig.Tools` / `.agent.md` `tools:`): passed through verbatim by `SessionSetupHelper`. For `.agent.md` files, `AgentFileParser` first translates VS Code Copilot tool-group names (e.g. `read/readFile` → `view`, `search` → `grep`/`glob`) to their CLI equivalents; programmatic `CustomAgentConfig.Tools` are expected to already use CLI names. Scope an agent to an MCP server with the namespaced slash wildcard `<server>/*` (e.g. `ado/*`) or a single tool `<server>/tool` — the slash form is what matches at the agent level (the dashed `<server>-tool` form does **not**). No wildcards are expanded and no MCP `tools/list` discovery is performed.
- **MCP servers**: when an MCP config file is supplied, every server in it is attached to the session (optionally wrapped via `mcp-wrapper`). Servers are never filtered by the tool list and are never attached to individual agents.

## Conventions
- All cmdlets follow the `Verb-CopilotNoun` naming pattern and are registered in `CopilotShell.psd1`.
- Use `FileInfo` (not `string`) for file path parameters — PowerShell provides tab completion automatically.
- The SDK is a pre-release NuGet package (`GitHub.Copilot.SDK`). Types may change — use reflection to discover API surface when docs are missing.
- The `.csproj` pins specific SDK versions. Use `Version="*"` if you want latest.
- `System.Management.Automation` is a compile-only reference (`PrivateAssets="all"`).

## Testing
- `test.ps1` runs smoke tests: one-shot `Invoke-Copilot` and a full client+session workflow.
- Run tests with pwsh 7.4+: `pwsh -File test.ps1`.
- `tests/` contains targeted bug-repro and validation tests. **Do not run these automatically** — only run when the user explicitly asks. Each test is self-contained with its own documentation header.

## Key Constraints
- .NET 8+ and pwsh 7.4+ are **required**.
- `global.json` pins the .NET SDK version.
- Cross-platform: no Windows-only APIs. `RuntimeIdentifiers`: win-x64, linux-x64, osx-x64, osx-arm64.
- Use `pwsh` command (on PATH) to launch PowerShell 7.4+.
