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
- **McpConfigLoader.cs** — Parses MCP JSON config files into SDK `McpLocalServerConfig`/`McpRemoteServerConfig` objects. Defaults `Tools=["*"]`, `Type="stdio"`. Skips `"disabled": true` entries.
- **ToolFilterHelper.cs** — Manages tool filtering with dynamic MCP tool discovery. When `-AvailableTools` is specified, core CLI tools are always included. Wildcard patterns (`ado-*`) and bare server names (`ado`) are expanded against dynamically discovered MCP tools via `McpToolDiscovery`. MCP servers not referenced in `-AvailableTools` are excluded from the session.
- **McpToolDiscovery.cs** — Discovers MCP server tools at runtime via the `tools/list` JSON-RPC protocol over stdio. Starts the server process, performs the MCP handshake, collects tool names, and kills the process.
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
- When `-AvailableTools` is specified, 16 core Copilot CLI tools are always included automatically
- Wildcard patterns (`ado-*`) and bare server names (`ado`) are expanded via dynamic MCP tool discovery (`tools/list` protocol)
- **Forward slashes are always normalized to dashes**: `ado/*` → `ado-*`, `kusto-mcp/kusto_query` → `kusto-mcp-kusto_query`, etc.
- Only MCP servers referenced by wildcard or bare name in `-AvailableTools` are attached to the session
- The expanded tools and normalized patterns are passed to the SDK

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
