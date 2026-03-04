# CopilotShell
## Programmable AI Orchestration from PowerShell

---

*A cross-platform PowerShell 7+ module wrapping the GitHub Copilot SDK for .NET*
*Giving you full programmatic control over GitHub Copilot — client, sessions, MCP, agents, and streaming*

---

## The Problem

You want to **orchestrate AI** from scripts, pipelines, and automation — not just chat in a UI.

The GitHub Copilot CLI is great for interactive use, but:

- **No custom system prompts** — can't replace or modify the AI's persona
- **No session management** — can't maintain multi-turn conversations programmatically
- **No MCP tool control** — can't selectively enable/disable tools
- **No composability** — can't pipe, loop, or script AI interactions
- **MCP env vars don't propagate** — SDK bug breaks MCP server configs
- **MCP servers restart every session** — losing auth tokens and incurring startup delay

---

## The Solution: CopilotShell

```
┌──────────────────────────┐
│     Your PowerShell       │   ← Scripts, pipelines, automation
│     Script / Bot          │
└──────────┬───────────────┘
           │ Cmdlets
┌──────────▼───────────────┐
│     CopilotShell          │   ← This module (C# binary module)
│  - Client management      │
│  - Session management     │
│  - Streaming              │
│  - MCP tool filtering     │
│  - Custom agents          │
│  - mcp-wrapper            │
└──────────┬───────────────┘
           │ .NET SDK
┌──────────▼───────────────┐
│  GitHub Copilot SDK       │   ← JSON-RPC over stdio
│  + copilot.exe CLI        │
└──────────┬───────────────┘
           │ API
┌──────────▼───────────────┐
│  GitHub Copilot Backend   │   ← Models: GPT-5, Claude, etc.
└──────────────────────────┘
```

---

## Key Design Principle: Reuse Your VS Code Investment

You've already built `.agent.md` files, MCP configs, and tool patterns for VS Code Copilot.
**CopilotShell uses the same files.** No translation layer, no separate config format.

```
Your existing VS Code files          CopilotShell parameter
──────────────────────────           ─────────────────────────
.github/agents/*.agent.md    ───▶   -CustomAgentFile
.github/prompts/*.prompt.md  ───▶   -PromptFile
.vscode/mcp.json             ───▶   -McpConfigFile
  (or any mcp-config.json)
Agent tool lists:                    Auto-mapped:
  execute/runInTerminal       ───▶     write_powershell, read_powershell, ...
  vscode/vscodeAPI            ───▶     view, create, edit, grep, glob
  read/readFile               ───▶     view
  web/fetch                   ───▶     web_fetch
  search                      ───▶     grep, glob
  ado-*                       ───▶     (82 tools, discovered dynamically)
```

The same `.agent.md` that powers your VS Code agent chat works **identically** in CopilotShell.
The same `.prompt.md` files with frontmatter and prompt text work natively with `-PromptFile`.
The same MCP config that connects your tools in VS Code works here too — with bonus features
(env var fix, persistent zombie servers) that make it work *better*.

---

## Demo 1: One-Liner — Zero Setup

```powershell
# Install once
Install-Module CopilotShell

# That's it. Copilot CLI auto-downloads on first use.
Invoke-Copilot "What is the capital of France?"
# → Paris
```

The `copilot.exe` binary is **automatically downloaded** from npm on first run and cached locally.
No `npm install`, no PATH setup, no manual downloads.

---

## Demo 2: Reuse Your VS Code Agent Files

If you already have `.agent.md` files in your repo for VS Code Copilot agents,
they work directly with CopilotShell — **no changes required:**

```powershell
# Point at the SAME agent file you use in VS Code
Invoke-Copilot "Check the latest deployment status" `
    -CustomAgentFile .github/agents/ado-team.agent.md `
    -McpConfigFile .vscode/mcp.json
```

Here's a typical `.agent.md` — identical to what VS Code Copilot expects:

```markdown
# .github/agents/ado-team.agent.md
---
description: 'ADO team helper agent'
tools:
    [
        'ado-*',
        'execute/runInTerminal',
        'vscode/vscodeAPI',
        'web/fetch'
    ]
---
You are a helpful assistant for the Azure DevOps team.
You have access to ADO work items, wikis, and pipelines.
Always check the current sprint board before answering.
```

CopilotShell parses the YAML frontmatter, **auto-maps VS Code tool names to CLI equivalents**, discovers MCP tools dynamically, and configures everything.

### VS Code Tool Name Translation

Agent files often reference VS Code-specific tool names. CopilotShell maps them automatically:

| VS Code tool name | CopilotShell equivalent |
|---|---|
| `execute/runInTerminal` | `write_powershell`, `read_powershell`, `stop_powershell`, `list_powershell` |
| `vscode/vscodeAPI` | `view`, `create`, `edit`, `grep`, `glob` |
| `read/readFile` | `view` |
| `web/fetch` | `web_fetch` |
| `search` | `grep`, `glob` |
| `edit` | `edit`, `create` |
| `execute/runTask` | *(dropped — VS Code-only, no CLI equivalent)* |

Both `/` and `-` separators work — `execute/runInTerminal` and `execute-runInTerminal` are identical.

---

## Demo 3: Reuse Your VS Code MCP Config

Your MCP config is the same JSON format VS Code uses. Point at the same file:

```powershell
# Use the same MCP config as VS Code
Invoke-Copilot "List my open work items" `
    -McpConfigFile .vscode/mcp.json `
    -AvailableTools @('ado')

# Or a standalone config file — same format
Invoke-Copilot "Check the Grafana dashboard" `
    -McpConfigFile ./mcp-config.json `
    -AvailableTools @('ado-wiki_*', 'grafana-mcp', 'powershell')
```

The config format:

```json
{
  "ado": {
    "command": "npx",
    "args": ["-y", "@azure-devops/mcp-server"],
    "env": { "ADO_ORG": "myorg", "ADO_PAT": "..." }
  },
  "grafana-mcp": {
    "command": "uvx",
    "args": ["grafana-mcp-server"],
    "env": { "GRAFANA_URL": "https://..." }
  },
  "kusto-mcp": {
    "command": "npx",
    "args": ["-y", "kusto-mcp-server"]
  }
}
```

**Bonus:** CopilotShell actually makes MCP work *better* than VS Code in two ways:
1. **Env var fix** — the SDK has a bug where env vars don't propagate; CopilotShell transparently fixes this
2. **Zombie daemon** — MCP servers stay alive across sessions (instant reconnect, auth tokens persist)

---

## Demo 4: Custom System Prompts

```powershell
# Replace the system message entirely — make it your own bot
Invoke-Copilot "Review this PR" `
    -SystemMessage "You are a senior security auditor. 
                    Flag any SQL injection, XSS, or auth bypass risks." `
    -SystemMessageMode Replace

# Or append to the default system message
Invoke-Copilot "Help me with this code" `
    -SystemMessage "Always respond in bullet points." `
    -SystemMessageMode Append
```

This is the **#1 reason CopilotShell was built** — the stock CLI doesn't let you customize the system prompt.

**Tip — load prompts from files:**
```powershell
# Load a system prompt from a markdown file (e.g., your copilot-instructions)
$prompt = Get-Content .github/copilot-instructions.md -Raw

Invoke-Copilot "Review this code" `
    -SystemMessage $prompt `
    -SystemMessageMode Replace

# Combine with an agent file for full config
$session = New-CopilotSession $client `
    -SystemMessage (Get-Content .github/copilot-instructions.md -Raw) `
    -SystemMessageMode Replace `
    -CustomAgentFile .github/agents/reviewer.agent.md `
    -McpConfigFile .vscode/mcp.json
```

Since `-SystemMessage` takes a string, you can `Get-Content` any markdown file — including your existing `copilot-instructions.md`, `.prompt.md` files, or any text file. PowerShell makes file-to-string trivial.

### Native `.prompt.md` Support

For VS Code-compatible `.prompt.md` files, use the dedicated `-PromptFile` parameter:

```powershell
# Use a .prompt.md file directly — prompt text + optional agent in frontmatter
Invoke-Copilot -PromptFile .github/prompts/get-work-items.prompt.md
```

Example `.prompt.md` file (same format as VS Code):

```markdown
# .github/prompts/get-work-items.prompt.md
---
agent: 'ado-team'
description: 'Your goal is to help with Azure DevOps work items.'
---
Get the list of work items assigned to me.
```

The frontmatter `agent:` field auto-selects the named agent. You can override it:

```powershell
# Override the prompt file's agent with an explicit -Agent
Invoke-Copilot -PromptFile .github/prompts/get-work-items.prompt.md \
    -Agent different-agent

# Override the prompt text too
Invoke-Copilot "Custom prompt text" \
    -PromptFile .github/prompts/get-work-items.prompt.md
```

**Override priority:** explicit `-Agent` > prompt file `agent:` > auto-select single custom agent.

---

## Demo 5: Multi-Turn Conversations & Session Introspection

```powershell
# Create a persistent client + session with MCP tools
$client = New-CopilotClient
$session = New-CopilotSession $client `
    -Model gpt-5 `
    -CustomAgentFile .github/agents/incident-responder.agent.md `
    -McpConfigFile .vscode/mcp.json `
    -Stream

# First message — kicks off tool calls
Send-CopilotMessage $session "Check the error rate for payments-api" -Stream | 
    Format-CopilotEvent

# Follow-up — builds on context from previous tool results
Send-CopilotMessage $session "Now check if there was a recent deployment" -Stream | 
    Format-CopilotEvent

# Third turn — correlate findings
Send-CopilotMessage $session "Summarize the root cause" -Stream | 
    Format-CopilotEvent
```

### Introspecting the Session with Follow-Up Questions

After the AI does work (runs tools, writes files, investigates), you can send a **simple follow-up** 
to verify outcomes — and branch on the plain-text answer:

```powershell
# Step 1: Ask the AI to investigate and write a log
Send-CopilotMessage $session @"
Investigate the payments-api error spike.
Use Grafana to check error rates, ADO for recent deployments, 
and Kusto for exception logs.
Write your findings to ./investigation.log
"@ -Stream | Format-CopilotEvent

# Step 2: Ask a yes/no verification question (non-streaming → returns plain text)
$result = Send-CopilotMessage $session `
    "Did you successfully run all the debugging tools and write the investigation log to ./investigation.log? Answer only 'Yes' or 'No'."

# Step 3: Branch on the answer
if ($result.Trim() -like "Yes*") {
    Write-Host "Investigation complete — log written" -ForegroundColor Green
    Get-Content ./investigation.log | Select-Object -First 5
} else {
    Write-Host "Investigation incomplete: $result" -ForegroundColor Red
    # Ask what went wrong
    $reason = Send-CopilotMessage $session "What failed? One sentence."
    Write-Error "Investigation failed: $reason"
}
```

This works because `Send-CopilotMessage` **without `-Stream`** returns plain text — perfect for yes/no gates.

You can chain multiple verification checks:

```powershell
# Run a multi-step task
Send-CopilotMessage $session "Deploy the hotfix to staging" -Stream | Format-CopilotEvent

# Verify each step
$deployed = Send-CopilotMessage $session "Did the deployment to staging succeed? Yes or No."
$healthy  = Send-CopilotMessage $session "Is the staging health check passing? Yes or No."
$logged   = Send-CopilotMessage $session "Did you write the deployment report to ./deploy.log? Yes or No."

if ($deployed.Trim() -like "Yes*" -and $healthy.Trim() -like "Yes*" -and $logged.Trim() -like "Yes*") {
    Write-Host "All checks passed — ready for production" -ForegroundColor Green
} else {
    Write-Host "Pre-prod checks failed:" -ForegroundColor Red
    Write-Host "  Deployed: $($deployed.Trim())"
    Write-Host "  Healthy:  $($healthy.Trim())"
    Write-Host "  Logged:   $($logged.Trim())"
    exit 1
}
```

### Session Persistence

Sessions persist on the server — you can disconnect and resume later:

```powershell
$id = $session.SessionId
Disconnect-CopilotSession $session

# ... hours later, even in a new terminal ...
$session = Resume-CopilotSession $client $id
Send-CopilotMessage $session "Where were we?"
```

# Clean up
Remove-CopilotSession -Client $client -SessionId $session.SessionId
Stop-CopilotClient $client

---

## Demo 6: Streaming Output

```powershell
# Stream events to the pipeline in real time
$session = New-CopilotSession $client -Stream
Send-CopilotMessage $session "Write a long story" -Stream | Format-CopilotEvent
```

Events flow through the PowerShell pipeline as typed objects:

| Event Type | Description |
|---|---|
| `AssistantMessageEvent` | The AI's response text |
| `ToolExecutionEvent` | Tool being called (and its result) |
| `SessionIdleEvent` | Session finished processing |
| `SessionErrorEvent` | Something went wrong |

`Format-CopilotEvent` renders them with colors and icons — or pipe them to your own handler.

---

## Demo 7: Bot / Automation Patterns

```powershell
# Automated code review bot
$client = New-CopilotClient
$session = New-CopilotSession $client `
    -SystemMessage "You are a code reviewer. Be concise. Rate severity 1-5." `
    -SystemMessageMode Replace

$files = git diff --name-only HEAD~1
foreach ($file in $files) {
    $diff = git diff HEAD~1 -- $file
    $review = Send-CopilotMessage $session "Review this diff for $file:`n$diff"
    "$file : $review" | Out-File reviews.txt -Append
}

Stop-CopilotClient $client
```

```powershell
# Incident response bot — query multiple MCP tools
Invoke-Copilot @"
An alert fired for service 'payments-api'. 
1. Check the Grafana dashboard for error rates
2. Look up the latest deployment in ADO
3. Query Kusto for exceptions in the last hour
4. Summarize findings and suggest next steps
"@ `
    -McpConfigFile ./mcp-config.json `
    -AvailableTools @('grafana-mcp', 'ado', 'kusto-mcp') `
    -TimeoutSeconds 120 `
    -MaxTurns 10
```

---

# Architecture Deep Dive

---

## How It All Fits Together

```
PowerShell Process (pwsh 7.4+ / .NET 8)
┌───────────────────────────────────────────────────────────────────┐
│  Default ALC                        CopilotShell ALC (isolated)  │
│  ┌─────────────────────┐           ┌──────────────────────────┐  │
│  │ CopilotShell.dll     │◄─resolve─│ GitHub.Copilot.SDK.dll   │  │
│  │ (binary module)      │          │ StreamJsonRpc.dll        │  │
│  │                      │          │ System.Text.Json v10     │  │
│  │ AsyncPSCmdlet        │          │ + 20 other dependencies  │  │
│  │ InvokeCopilotCommand │          └──────────────────────────┘  │
│  │ SessionCmdlets       │                                        │
│  │ McpToolDiscovery     │          ┌──────────────────────────┐  │
│  │ ToolFilterHelper     │          │ copilot.exe              │  │
│  │ McpWrapperHelper     │──stdio──▶│ (child process)          │  │
│  │ AgentFileParser      │          │ JSON-RPC 2.0             │  │
│  └─────────────────────┘          └──────────┬───────────────┘  │
│                                               │                  │
│                                    ┌──────────▼───────────────┐  │
│                                    │ mcp-wrapper              │  │
│                                    │ (proxy / zombie daemon)  │  │
│                                    └──────────┬───────────────┘  │
│                                               │                  │
│                                    ┌──────────▼───────────────┐  │
│                                    │ MCP Servers              │  │
│                                    │ (ADO, Grafana, Kusto...) │  │
│                                    └──────────────────────────┘  │
└───────────────────────────────────────────────────────────────────┘
```

---

## Technical Challenge #1: Assembly Load Context Isolation

### The Problem

PowerShell 7.4 runs on .NET 8 and loads `System.Text.Json v8.x`.
The Copilot SDK needs `System.Text.Json v10.x`.
Loading both in the same context → **MissingMethodException at runtime**.

### The Workaround

CopilotShell uses a **custom AssemblyLoadContext** with a two-phase boot:

```
Phase 1: StartupCheck.ps1 (runs via ScriptsToProcess BEFORE binary module loads)
  ┌──────────────────────────────────────────────────────────┐
  │ 1. Create isolated ALC named "CopilotShell"              │  8ms
  │ 2. Pre-load ALL 23 dependency DLLs into that ALC         │  95ms
  │ 3. Register Resolving handler on Default ALC              │  5ms
  │    → When Default ALC can't find a dependency,            │
  │      the handler finds it in the CopilotShell ALC         │
  └──────────────────────────────────────────────────────────┘

Phase 2: PowerShell loads CopilotShell.dll (Import-Module)
  ┌──────────────────────────────────────────────────────────┐
  │ 4. PowerShell calls GetTypes() on CopilotShell.dll       │
  │ 5. This triggers resolution of SDK types                  │
  │ 6. Resolving handler (already registered!) returns        │
  │    pre-loaded assemblies from the isolated ALC            │
  │ 7. Everything works — no version conflicts                │
  └──────────────────────────────────────────────────────────┘
```

**Why `ScriptsToProcess`?** The `IModuleAssemblyInitializer.OnImport()` runs *after* `GetTypes()` starts — too late! The resolver must be in place **before** the binary module loads.

**Result:** 188ms cold boot with full dependency isolation. Zero conflicts.

---

## Technical Challenge #2: Async/Await in PowerShell Cmdlets

### The Problem

PowerShell cmdlets run on a **single pipeline thread**. You can't call `WriteObject()` from a background thread — it throws. But the Copilot SDK is fully async (`await foreach`, `Task<T>`, etc.).

### The Workaround: `AsyncPSCmdlet` — A Custom Message Pump

```
Pipeline Thread                    Background (async work)
────────────────                   ──────────────────────
ProcessRecord()                    
  │                                
  ├─ Create BlockingCollection     
  ├─ Set SynchronizationContext    
  │    (routes Post() → queue)     
  │                                
  ├─ Start async work ──────────▶  async Task ProcessRecordAsync()
  │                                  │
  │  ┌── Message Pump ──┐           │  await sdk.CreateSessionAsync()
  │  │ while (!done)     │           │  
  │  │   TryTake(50ms)   │ ◀─Post── │  WriteObject(session) 
  │  │   Execute callback │           │    → Post to queue
  │  │   (WriteObject!)  │           │
  │  │                   │           │  await foreach (event)
  │  │                   │ ◀─Post── │    WriteObject(event)
  │  │                   │           │      → Post to queue
  │  └──────────────────┘           │
  │                                  │  return
  ├─ Drain remaining callbacks      
  └─ task.GetAwaiter().GetResult()  
```

Every cmdlet inherits `AsyncPSCmdlet` and overrides `ProcessRecordAsync()`. The pump runs on the pipeline thread, executing marshaled callbacks — so `WriteObject` is always called from the correct thread.

---

## Technical Challenge #3: MCP Environment Variable Bug

### The Problem

The GitHub Copilot SDK **doesn't propagate environment variables** or working directories to MCP server processes it spawns. [copilot-sdk#163](https://github.com/github/copilot-sdk/issues/163)

Your `mcp-config.json` says `"env": { "ADO_PAT": "..." }` — but the MCP server never sees it.

### The Workaround: `mcp-wrapper`

CopilotShell **transparently rewrites every MCP config** to route through `mcp-wrapper`:

**Before (what you write):**
```json
{ "command": "npx", "args": ["-y", "@azure-devops/mcp-server"],
  "env": { "ADO_ORG": "myorg" } }
```

**After (what the SDK actually sees):**
```json
{ "command": "mcp-wrapper.exe", 
  "args": ["--", "npx", "-y", "@azure-devops/mcp-server"],
  "env": { "ADO_ORG": "myorg" } }
```

The wrapper proxies stdin/stdout/stderr transparently. The SDK sets env vars on the wrapper process, and the child MCP server inherits them. You never know it's there.

---

## Technical Challenge #4: MCP Server Startup Latency

### The Problem

The Copilot CLI starts fresh MCP servers for **every session**. A server like ADO takes 5-10 seconds to initialize (`npm install`, auth handshake, etc.). If you create 3 sessions, you wait 3 times.

### The Workaround: Zombie Daemon

`mcp-wrapper` includes a **persistent background daemon** that keeps MCP servers alive across sessions:

```
Session 1 creates ADO server:     mcp-wrapper → daemon → starts ADO  (5s)
Session 2 reuses ADO server:      mcp-wrapper → daemon → reuse!      (0s)
Session 3 reuses ADO server:      mcp-wrapper → daemon → reuse!      (0s)
Session 1 ends:                   ADO server stays alive
Session 2 ends:                   ADO server stays alive
                                  ... hours later ...
Session 4 reuses ADO server:      mcp-wrapper → daemon → reuse!      (0s)
```

**How it works:**

1. Each MCP server is keyed by SHA256 of `(command + args + env + cwd)`
2. First request spawns the server; subsequent requests get the **cached connection**
3. The daemon multiplexes JSON-RPC between multiple clients sharing one server
4. JSON-RPC 2.0 request IDs are **remapped** to prevent collisions:

```
Client A sends: {"id":1, "method":"tools/list"}
  → Daemon remaps to: {"id":1001, "method":"tools/list"}

Client B sends: {"id":1, "method":"tools/call"}
  → Daemon remaps to: {"id":1002, "method":"tools/call"}

Server responds: {"id":1001, "result":{...}}
  → Daemon routes to Client A, restores: {"id":1, "result":{...}}
```

5. `initialize` responses are **cached** — instant reconnection, no handshake
6. Auth tokens persist — no re-authentication between sessions

**Eligible servers** (auto-detected by regex):
- `@azure-devops/*`, `grafana*`, `ev2*`, `microsoft-fabric-rti-mcp*`

**Manage the daemon:**
```powershell
Reset-CopilotMcpDaemon          # Recycle all servers (e.g., stale auth tokens)
Reset-CopilotMcpDaemon -Force   # Force-kill if needed
```

---

## Technical Challenge #5: Dynamic MCP Tool Discovery

### The Problem

The Copilot CLI requires **exact tool names** in `-AvailableTools` — no wildcards. MCP tools are named like `grafana-mcp-search_dashboards`. A server like ADO has **82 tools**. Nobody wants to type all 82.

### The Workaround: Runtime `tools/list` Protocol

When you pass a wildcard or bare server name, CopilotShell **temporarily starts each MCP server** and queries it:

```
1. Start process:  npx -y @azure-devops/mcp-server
2. Send:           {"method":"initialize", "params":{...}}
3. Receive:        {"result":{"capabilities":{...}}}
4. Send:           {"method":"notifications/initialized"}
5. Send:           {"method":"tools/list"}
6. Receive:        {"result":{"tools":[{"name":"wiki_get_page"}, ...]}}
7. Kill process
```

Then patterns are expanded:

| You write | Expands to |
|---|---|
| `'ado'` | All 82 ADO tools |
| `'ado-wiki_*'` | `ado-wiki_get_page`, `ado-wiki_search`, ... |
| `'grafana-mcp'` | All 24 Grafana tools |
| `'powershell'` | `write_powershell`, `read_powershell`, `stop_powershell`, `list_powershell` |

Forward slashes are normalized to dashes (`ado/wiki_get_page` → `ado-wiki_get_page`).

16 core CLI tools (`view`, `edit`, `grep`, `create`, etc.) are **always** included automatically.

---

## Technical Challenge #6: Auto-Download of Copilot CLI

### The Problem

The module needs `copilot.exe` — but users shouldn't have to install it manually or know about npm.

### The Workaround

`CliDownloader` fetches the correct platform-specific binary from the npm registry:

```
1. Read required version from assembly metadata (stamped at build time)
2. Map .NET RID → npm platform: win-x64 → @github/copilot-win32-x64
3. Download .tgz from https://registry.npmjs.org/@github/copilot-{platform}
4. Extract copilot.exe from the tarball
5. Cache to: %LOCALAPPDATA%\CopilotShell\cli\{version}\{rid}\
6. Next run: instant (cached)
```

Cross-platform: works on Windows, macOS (x64/arm64), and Linux.

---

## Performance: Where Does Time Go?

Cold-start `Invoke-Copilot "What is 2+2?"` — **8.2 seconds total**:

```
Import-Module     ██                                         188ms   (2.3%)
Client start      █████████████████                        1,717ms  (20.9%)
Session create    ████████████                             1,262ms  (15.4%)
LLM response      ████████████████████████████████████████ 4,826ms  (58.8%)
Everything else   ██                                         208ms   (2.5%)
                                                           ────────
                                                           8,201ms
```

### Key Insights

| Metric | Value | Notes |
|---|---|---|
| Module cold boot | **188ms** | Excellent for 23 isolated dependencies |
| Setup tax (once) | **2.5s** | Client + session — paid once per session |
| First message | **4.0s** | Includes server-side warm-up |
| Subsequent messages | **2.4s** | **40% faster** — session is warm |
| MCP config/wrapper | **0ms** | Negligible overhead |
| Tool discovery | per-server | Only when wildcards are used |

**Optimization: reuse sessions** — the 2.5s setup is paid once. After that, it's just LLM response time.

---

## The Cmdlet Surface

### 18 cmdlets — PowerShell-idiomatic, composable

**Client lifecycle:**
```
New-CopilotClient → Start-CopilotClient → Test-CopilotClient → Stop-CopilotClient → Remove-CopilotClient
```

**Session lifecycle:**
```
New-CopilotSession → Send-CopilotMessage → Get-CopilotSessionMessages → Disconnect-CopilotSession
                  ↘ Resume-CopilotSession ↗                             → Remove-CopilotSession
```

**One-shot:**
```
Invoke-Copilot ───▶ (creates client, session, sends message, cleans up)
```

**MCP management:**
```
Reset-CopilotMcpDaemon  ← stop/restart the zombie daemon
```

All cmdlets follow `Verb-CopilotNoun` naming and support standard PowerShell patterns (`-Verbose`, `-ErrorAction`, pipeline input, etc.).

---

## Reusing Your VS Code Ecosystem — Full Compatibility Map

CopilotShell is designed so you **don't rewrite anything** you've already built for VS Code Copilot:

### What's directly reusable

| VS Code Artifact | CopilotShell Parameter | Conversion Needed? |
|---|---|---|
| `.github/agents/*.agent.md` | `-CustomAgentFile` | **None** — used as-is |
| `.github/prompts/*.prompt.md` | `-PromptFile` | **None** — used as-is (prompt text + optional agent from frontmatter) |
| `.vscode/mcp.json` / MCP configs | `-McpConfigFile` | **None** — same JSON format |
| Agent `tools:` lists with VS Code names | (auto-mapped) | **Automatic** — e.g. `execute/runInTerminal` → PowerShell tools |
| MCP tool wildcards (`ado-*`) | `-AvailableTools` | **None** — expanded via dynamic discovery |
| `copilot-instructions.md` | `-SystemMessage (Get-Content ... -Raw)` | **One line** — `Get-Content` loads the file |
| Inline system prompts | `-SystemMessage` + `-SystemMessageMode` | Direct — just a string parameter |

### Agent file format (identical to VS Code)

```markdown
# .github/agents/incident-responder.agent.md
---
description: 'Automated incident response agent'
tools:
    [
        'grafana-mcp',
        'ado',
        'kusto-mcp',
        'execute/runInTerminal',
        'vscode/vscodeAPI'
    ]
---
You are an incident response agent for the payments team.

When investigating an incident:
1. Check Grafana for error rate spikes and latency
2. Query Kusto for exception logs in the last hour
3. Look up recent deployments in ADO
4. Correlate findings and provide a root cause analysis
5. Suggest remediation steps
```

Both the VS Code tool names (`execute/runInTerminal`, `vscode/vscodeAPI`) and the MCP wildcards (`ado`, `grafana-mcp`) are handled automatically. No edits needed.

### Tool pattern support in `-AvailableTools`

| Pattern | Example | Effect |
|---|---|---|
| Bare server name | `'ado'` | All tools from that MCP server (discovered dynamically) |
| Wildcard | `'ado-wiki_*'` | Tools matching the prefix |
| Exact tool | `'ado-wiki_get_page'` | Only that specific tool |
| Shorthand | `'powershell'` | Expands to 4 PowerShell tools |
| Core tool | `'view'`, `'edit'`, `'grep'` | Always included automatically |
| VS Code name | `'execute/runInTerminal'` | Mapped to CLI equivalents |

**Slash normalization:** `ado/wiki_*` → `ado-wiki_*` (both separators work everywhere)

### Loading prompt files

```powershell
# Native .prompt.md support (with frontmatter agent selection)
Invoke-Copilot -PromptFile .github/prompts/get-work-items.prompt.md `
    -McpConfigFile .vscode/mcp.json

# Or load copilot-instructions.md as a system prompt
Invoke-Copilot "Review this" `
    -SystemMessage (Get-Content .github/copilot-instructions.md -Raw) `
    -SystemMessageMode Replace

# Combine: instructions file + agent file + MCP config
New-CopilotSession $client `
    -SystemMessage (Get-Content .github/copilot-instructions.md -Raw) `
    -SystemMessageMode Append `
    -CustomAgentFile .github/agents/reviewer.agent.md `
    -McpConfigFile .vscode/mcp.json
```

### The result: one repo, two runtimes

```
.github/
├── agents/
│   ├── ado-team.agent.md          ← VS Code uses this interactively
│   ├── incident-responder.agent.md ← CopilotShell uses this in automation
│   └── reviewer.agent.md          ← Same file, both runtimes
├── prompts/
│   └── get-work-items.prompt.md   ← VS Code uses this; CopilotShell via -PromptFile
├── copilot-instructions.md        ← VS Code reads natively; CopilotShell via Get-Content
.vscode/
└── mcp.json                       ← VS Code reads natively; CopilotShell via -McpConfigFile
```

No duplication. No format conversion. One source of truth.

---

## Why This Matters for AI Orchestration

### Scripts that think

```powershell
# Nightly security scan
$repos = gh repo list myorg --json name -q '.[].name'
foreach ($repo in $repos) {
    $code = gh api repos/myorg/$repo/contents/src --jq '.[] | .name'
    Invoke-Copilot "Scan $repo for security issues: $code" `
        -SystemMessage "You are a security auditor. Output JSON." `
        -SystemMessageMode Replace | 
        ConvertFrom-Json | 
        Where-Object { $_.severity -ge 3 } |
        Export-Csv "security-findings.csv" -Append
}
```

### Pipelines that reason

```powershell
# CI/CD gate: AI-powered review
$diff = git diff main...HEAD
$review = Invoke-Copilot "Review this PR diff for breaking changes:`n$diff" `
    -SystemMessage "Output PASS or FAIL with one-line reason." `
    -SystemMessageMode Replace
    
if ($review -match "^FAIL") { 
    Write-Error $review
    exit 1 
}
```

### Multi-agent orchestration

```powershell
$client = New-CopilotClient

# Agent 1: Gather data
$gather = New-CopilotSession $client `
    -CustomAgentFile .github/agents/data-gatherer.agent.md `
    -McpConfigFile ./mcp-config.json
$data = Send-CopilotMessage $gather "Get the metrics for last week"

# Agent 2: Analyze
$analyst = New-CopilotSession $client `
    -CustomAgentFile .github/agents/analyst.agent.md
$analysis = Send-CopilotMessage $analyst "Analyze this data: $data"

# Agent 3: Write report
$writer = New-CopilotSession $client `
    -SystemMessage "You are a technical writer. Write a concise report." `
    -SystemMessageMode Replace
Send-CopilotMessage $writer "Write a report based on: $analysis"

Stop-CopilotClient $client
```

---

## Cross-Platform

| Component | Windows | macOS | Linux |
|---|---|---|---|
| CopilotShell module | ✅ | ✅ | ✅ |
| mcp-wrapper + daemon | ✅ | ✅ | ✅ |
| CLI auto-download | ✅ x64/arm64 | ✅ x64/arm64 | ✅ x64/arm64 |
| Unix domain sockets | ✅ (.NET 8) | ✅ | ✅ |

No Windows-only APIs. `RuntimeIdentifiers: win-x64, linux-x64, osx-x64, osx-arm64`.

---

## Getting Started

```powershell
# 1. Install PowerShell 7.4+ (if you don't have it)
winget install Microsoft.PowerShell

# 2. Install the module
Install-Module CopilotShell

# 3. Use it
Invoke-Copilot "Hello, world!"

# 4. With MCP tools
Invoke-Copilot "List my ADO work items" `
    -McpConfigFile ./mcp-config.json `
    -AvailableTools @('ado')

# 5. With a custom agent
Invoke-Copilot "Check the deployment status" `
    -CustomAgentFile .github/agents/my-agent.agent.md `
    -McpConfigFile ./mcp-config.json
```

---

## Summary

| Feature | How |
|---|---|
| **Reuse VS Code agents** | `-CustomAgentFile` — same `.agent.md` files, no changes |
| **Reuse VS Code prompts** | `-PromptFile` — same `.prompt.md` files with frontmatter |
| **Reuse VS Code MCP configs** | `-McpConfigFile` — same JSON format as `.vscode/mcp.json` |
| **Reuse VS Code prompts** | `-PromptFile` — same `.prompt.md` files with frontmatter |
| **Load system prompts** | `-SystemMessage (Get-Content .github/copilot-instructions.md -Raw)` |
| **VS Code tool name mapping** | `execute/runInTerminal` → PowerShell tools, etc. (automatic) |
| **Custom system prompts** | `-SystemMessage` + `-SystemMessageMode Replace` |
| **Multi-turn sessions** | `New-CopilotSession` → `Send-CopilotMessage` (repeatable) |
| **Streaming** | `-Stream` + pipeline with `Format-CopilotEvent` |
| **MCP tool filtering** | `-AvailableTools` with wildcards, dynamic discovery |
| **Persistent MCP servers** | Zombie daemon via `mcp-wrapper` (auto) |
| **MCP env var fix** | Transparent wrapping via `mcp-wrapper` (auto) |
| **Auto CLI download** | First-run download from npm, cached locally |
| **Assembly isolation** | Custom ALC with pre-loaded dependencies |
| **Async in PowerShell** | `AsyncPSCmdlet` message pump pattern |
| **Cross-platform** | Windows, macOS, Linux — .NET 8 + pwsh 7.4+ |

---

## Links

- **GitHub:** [github.com/KnicKnic/copilot-pwsh](https://github.com/KnicKnic/copilot-pwsh)
- **Copilot SDK:** [github.com/github/copilot-sdk](https://github.com/github/copilot-sdk)
- **SDK env bug:** [copilot-sdk#163](https://github.com/github/copilot-sdk/issues/163)
- **License:** MIT

---

*CopilotShell v0.2.0 — PowerShell meets AI orchestration*
