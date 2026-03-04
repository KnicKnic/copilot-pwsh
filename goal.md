# goal
Your goal is to recreate copilot-cli as a PowerShell module

see commandline reference https://docs.github.com/en/copilot/reference/cli-command-reference

more info at https://github.com/github/copilot-cli?locale=en-US


## Why?
copilot cli doesnt allow you to replace the system prompt and I need this functionality for writting bots

## How?
use the dotnet sdk - get more info https://github.com/github/copilot-sdk/blob/main/dotnet/README.md

## Platform
- .NET 10 (net10.0) — required by GitHub.Copilot.SDK
- PowerShell 7.5+ built on .NET 10 (pwsh preview/RC)
- Cross-platform (Windows, macOS, Linux)

## Exposed capabilities

### Client Management
- `New-CopilotClient` — Create a CopilotClient with options (CliPath, Port, LogLevel, GithubToken, etc.)
- `Start-CopilotClient` — Start a client (if AutoStart is false)
- `Stop-CopilotClient` — Gracefully stop a client
- `Remove-CopilotClient` — Dispose a client
- `Test-CopilotClient` — Ping the server to check connectivity

### Session Management
- `New-CopilotSession` — Create a session with full config (Model, SystemMessage, Streaming, Tools, AvailableTools, ExcludedTools, Provider, InfiniteSessions, MaxTurns/Timeout)
- `Get-CopilotSession` — List all sessions or get a specific session by ID
- `Resume-CopilotSession` — Resume an existing session by ID
- `Remove-CopilotSession` — Delete a session by ID
- `Get-CopilotSessionMessages` — Get all messages/events from a session
- `Stop-CopilotSession` — Abort the currently processing message

### Messaging
- `Send-CopilotMessage` — Send a message to a session, returns message ID; supports -Stream to emit events to pipeline
- `Wait-CopilotSession` — Wait for session to become idle (with optional -Timeout)
- **Ctrl+C Support** — Press Ctrl+C to cancel streaming operations cleanly; sending a new message automatically cancels any in-progress operation on the same session

### High-Level / Convenience
- `Invoke-Copilot` — One-shot: create client+session, send prompt, stream/collect output, dispose; supports -SystemMessage, -Model, -Stream, -Timeout, -MaxTurns

## Output & Streaming
- All event cmdlets output typed objects (AssistantMessage, ToolExecution, SessionIdle, etc.)
- `Send-CopilotMessage -Stream` streams events to pipeline in real time
- `Invoke-Copilot -Stream` streams events; without -Stream returns final assistant message text

## Timeout & Turn Control
- `-Timeout` (TimeSpan or seconds) on `Send-CopilotMessage`, `Wait-CopilotSession`, `Invoke-Copilot`
- `-MaxTurns` on `Invoke-Copilot` to limit round-trips

## System Message Customization
- `-SystemMessage` with `-SystemMessageMode Append|Replace` on `New-CopilotSession` and `Invoke-Copilot`

## Target
- PowerShell 7.5+ (pwsh) on .NET 10 runtime
- .NET 10

## Please provide simple install instructions
and how to quickly rebuild and reinstall the module

## Other
Include good documentation, and an appropriate .gitignore file