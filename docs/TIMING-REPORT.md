# CopilotShell Timing Report

**Date:** March 4, 2026  
**Module version:** 0.2.0  
**Runtime:** pwsh 7.4+ / .NET 8  
**Machine:** Windows (OneDrive user-scope module path)

---

## 1. Invoke-Copilot (One-Shot) — 8.2s total

Single cold-boot invocation: `Invoke-Copilot -Prompt "What is 2+2?"`

### Module Loading (cold boot) — 188ms (2.3%)

| Phase | Duration | Notes |
|---|---:|---|
| ALC creation | 8ms | AssemblyLoadContext for dependency isolation |
| Preload dependencies | 95ms | 23 DLLs loaded into ALC |
| Register resolver | 5ms | ALC resolving handler |
| **StartupCheck.ps1 total** | **108ms** | ScriptsToProcess — runs before binary module |
| Binary module load (GetTypes) | 80ms | Import-Module minus StartupCheck |
| **Import-Module total** | **188ms** | |

### Invoke-Copilot Execution — 7,868ms C# / 7,903ms wall (96.4%)

| Phase | Duration | @Offset | % of cmdlet |
|---|---:|---:|---:|
| CLI resolve | 2ms | @125ms | 0.0% |
| Client create + start | 1,717ms | @1844ms | 21.8% |
| MCP config + wrapper | 0ms | @1845ms | — |
| MCP tool discovery | 0ms | @1845ms | — |
| Session config (tools + agents) | 1ms | @1848ms | 0.0% |
| Session create | 1,262ms | @3110ms | 16.0% |
| Agent select | 0ms | @3110ms | — |
| Message send (enqueue) | 53ms | @3164ms | 0.7% |
| **Wait for response** | **4,826ms** | @7990ms | **61.3%** |

### Where the 8.2s goes

```
Import-Module     ██                                         188ms   (2.3%)
Client start      █████████████████                        1,717ms  (20.9%)
Session create    ████████████                             1,262ms  (15.4%)
LLM response      ████████████████████████████████████████ 4,826ms  (58.8%)
Everything else   ██                                         208ms   (2.5%)
─────────────────────────────────────────────────────────
TOTAL                                                      8,201ms
```

---

## 2. Session Multi-Message Workflow — 11.5s total

Workflow: `New-CopilotClient` → `New-CopilotSession` → 3x `Send-CopilotMessage` → cleanup

### Setup (one-time costs) — 2,530ms (22.1%)

| Phase | Duration | Notes |
|---|---:|---|
| Import-Module (cold boot) | 170ms | Same ALC + preload chain |
| New-CopilotClient | 1,280ms | copilot.exe launch + handshake |
| New-CopilotSession | 1,080ms | Session create (1,066ms is SDK call) |
| **Total setup** | **2,530ms** | |

#### New-CopilotClient breakdown

| Phase | Duration | @Offset |
|---|---:|---:|
| CLI resolve + options | 2ms | @81ms |
| Client constructor | 1ms | @83ms |
| Client start | 1,263ms | @1347ms |

#### New-CopilotSession breakdown

| Phase | Duration | @Offset |
|---|---:|---:|
| MCP config + wrapper | 0ms | @1367ms |
| MCP tool discovery | 0ms | @1367ms |
| Session config (tools + agents) | 1ms | @1369ms |
| Session create | 1,066ms | @2435ms |
| Agent select | 0ms | @2436ms |

### Per-Message Timing

| # | Prompt | Reply | Duration |
|---|---|---|---:|
| 1 | What is 2+2? | 4 | 4,000ms |
| 2 | Capital of France? | Paris | 2,498ms |
| 3 | What is 7*8? | 56 | 2,289ms |

- **Message #1:** 4,000ms — includes server-side warm-up / token allocation overhead
- **Messages #2+ avg:** 2,394ms — **40% faster** once the session is warm
- Message setup overhead: **0ms** — all time is in `SendAndWaitAsync`

### Where the 11.5s goes

```
Import-Module     ██                                         170ms   (1.5%)
Client start      █████████████                            1,280ms  (11.2%)
Session create    ███████████                              1,080ms   (9.4%)
Message #1        ████████████████████████████████████████ 4,000ms  (34.9%)
Message #2        █████████████████████████                2,498ms  (21.8%)
Message #3        ███████████████████████                  2,289ms  (20.0%)
Cleanup           █                                           27ms   (0.2%)
─────────────────────────────────────────────────────────
TOTAL                                                     11,468ms
```

---

## 3. Analysis: Where is the Code Spending Time?

### Module load (170-188ms) — fast, no action needed
- Dominated by preloading 23 dependency DLLs into the AssemblyLoadContext
- Runs once per process via `StartupCheck.ps1` (ScriptsToProcess)

### Client start (1,263-1,717ms) — copilot.exe process launch
- `CopilotClient.StartAsync()` starts the copilot CLI as a child process
- Includes process spawn, CLI initialization, JSON-RPC handshake
- This is the SDK launching `copilot.exe` — not under our control

### Session create (1,066-1,262ms) — server-side allocation
- `CopilotClient.CreateSessionAsync()` — SDK → copilot.exe → GitHub backend
- Allocates a session on the Copilot backend
- Nothing to optimize on our side

### Message send/wait (2,289-4,000ms) — LLM response time
- **The dominant cost** — 59-77% of total wall time
- `Session.SendAndWaitAsync()` is an opaque SDK call
- Sends message via JSON-RPC to copilot.exe, which calls the GitHub backend
- All time is network round-trip + LLM inference
- First message is ~40% slower than subsequent (server-side warm-up)

### What's NOT taking time
- CLI resolve: 2ms
- MCP config/wrapper: 0ms (no MCP servers configured in test)
- Session config: 1ms
- Agent select: 0ms
- Message setup: 0ms
- Cleanup: 27ms

---

## 4. Key Findings

1. **Module itself is fast** — 170ms cold boot is excellent for a binary module with 23 isolated dependencies
2. **Setup tax is 2.5s** — client start + session create, paid once per session
3. **Reusing sessions saves ~40% per message** — subsequent messages average 2.4s vs 4.0s for the first
4. **LLM wait dominates** — 59-77% of wall time is waiting for the Copilot backend
5. **No optimization opportunity in our code** — the bottlenecks are all inside the SDK or server-side
6. **MCP overhead is 0ms** when no MCP servers are configured; with MCP servers the `MCP tool discovery` phase would add startup time proportional to the number of servers

### Possible optimizations (if needed)
- **Warm session pool** — pre-create client+session to eliminate the 2.5s setup cost for interactive use
- **Streaming mode** — use `-Stream` to get time-to-first-token visibility and start showing output earlier
- **Parallel MCP discovery** — already implemented; would matter with many MCP servers

---

## 5. How to Reproduce

```powershell
# One-shot timing (cold boot + Invoke-Copilot)
pwsh -NoProfile -File timing-invoke.ps1

# Multi-message session timing
pwsh -NoProfile -File timing-session.ps1

# Custom message count
pwsh -NoProfile -File timing-session.ps1 -MessageCount 5

# With specific model
pwsh -NoProfile -File timing-session.ps1 -Model gpt-4.1-mini
```

All timing data is captured via `System.Diagnostics.Stopwatch` in C# with `[Timing @Xms]` verbose output (absolute offset from module load + phase duration), plus `$global:CopilotShellTimings` from `StartupCheck.ps1`.
