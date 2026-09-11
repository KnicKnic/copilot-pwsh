---
name: invoke-copilot-task
description: 'Run autonomous Copilot tasks via Invoke-CopilotTask.ps1. Use when executing prompt files, running headless agent workflows, automating multi-step Copilot sessions, orchestrating CI-style Copilot tasks with MCP tools, or adding Invoke-CopilotTask to a new repository.'
---

# Invoke-CopilotTask

Runs autonomous Copilot sessions from the command line. Handles session lifecycle, run tracking, MCP config forwarding, custom agent discovery through CopilotShell bindings, and success validation.

## When to Use

- Execute a `.prompt.md` file as a headless Copilot task
- Run autonomous agent workflows with MCP tools
- Orchestrate repeatable, idempotent Copilot tasks (CI/automation)
- Add Invoke-CopilotTask tooling to a new repository

## Prerequisites

- **pwsh 7.4+** on PATH
- **CopilotShell** module installed (`Import-Module CopilotShell`)
- **Authenticated** via `Connect-Copilot`

## Adding to a Repository

To adopt Invoke-CopilotTask in any repo, copy the core script and build a thin wrapper for your use case. The pattern is:

### 1. Copy the core script

Copy [Invoke-CopilotTask.ps1](./assets/Invoke-CopilotTask.ps1) into `tools/` in your target repo. This is the only required file.

### 2. Create a prompt file

Write a `.prompt.md` describing the task. This is the instruction set Copilot follows. Place it alongside your script or in `.github/prompts/`.

### 3. Create a wrapper script

The wrapper is a thin PowerShell script that collects inputs, resolves context, and calls `Invoke-CopilotTask.ps1` with the right arguments. It doesn't contain agent logic — just orchestration.

### 4. Ensure MCP tools are available

If your task needs MCP tools (GitHub CLI, ADO, etc.), make sure `.mcp.json` exists in the target repo or `~/.copilot/mcp-config.json` exists in your home directory, or pass VS Code/Copilot CLI-format configs with `-McpConfigFile`. CopilotShell accepts both formats directly and merges configs in order.

## Agent and Prompt Resolution

### How the agent is resolved

The agent is resolved in priority order:

1. **`-DefaultAgent` parameter** — always wins if provided
2. **Prompt file frontmatter** — if `-PromptFile` has `agent: 'my-agent'` in its YAML frontmatter
3. **No agent** — if neither is set, the session runs without a named agent

Agent files are discovered by CopilotShell, not by this script. `Invoke-CopilotTask.ps1` uses the default search paths: the repository `.github/agents` directory first, then `~/.copilot/agents`, with earlier paths winning on name conflicts. Use `-Agent` (`-AgentNames`/`-Agents`) to load named agents without selecting one, and `-AgentFolders` to override the ordered discovery folders.

You can also pass explicit agent file paths with `-AgentFile`.

### When `-DefaultAgent` is necessary

- **No prompt file** — if you only use `-PrependPrompt`, there's no frontmatter to read the agent from. You must pass `-DefaultAgent` explicitly (or omit it to run without one).
- **Multiple agents** — if your repo has several `.agent.md` files and you want to pick one that differs from what the prompt file specifies, use `-DefaultAgent` to override.
- **Reusing a prompt with different agents** — the same prompt file can be run with different agent configurations by passing `-DefaultAgent` at the wrapper level.

### When `-DefaultAgent` is not needed

- **Prompt file specifies the agent** — if your `.prompt.md` has `agent: 'my-agent'` in frontmatter, the script picks it up automatically.
- **No agent needed** — simple tasks that don't need agent-specific instructions or tool restrictions can run without one.

## Example: triage-ci-failures

This repo includes a working example of the pattern. It triages failed GitHub Actions runs by fetching logs and writing diagnosis reports.

### Files involved

| File | Role |
|------|------|
| [Invoke-CopilotTask.ps1](./assets/Invoke-CopilotTask.ps1) | Core engine — manages the full Copilot session lifecycle |
| [ci-failure.prompt.md](./assets/ci-failure.prompt.md) | Prompt file — instructions for diagnosing a CI failure |
| [get-failed-runs.ps1](./assets/get-failed-runs.ps1) | Helper — queries GitHub API for failed workflow runs |
| [triage-ci-failures.ps1](./assets/triage-ci-failures.ps1) | Wrapper — iterates failed runs and calls Invoke-CopilotTask for each |

### Flow

```
triage-ci-failures.ps1 -Repo owner/repo
  │
  ├─ get-failed-runs.ps1        # Fetch failed run IDs via GitHub API
  │
  └─ ForEach run ID (parallel):
       │
       └─ Invoke-CopilotTask.ps1
            -PrependPrompt "Diagnose run $runId in repo $Repo"
            -PromptFile    ci-failure.prompt.md
            -Name          "ci/$runId"
            -RunOnce                              # idempotent
            -DisplayFiles  "ci-failures/$runId.md"
```

### The wrapper script pattern

The wrapper ([triage-ci-failures.ps1](./assets/triage-ci-failures.ps1)) does three things:

1. **Gathers context** — calls `get-failed-runs.ps1` to get a list of run IDs
2. **Loops with parallelism** — uses `ForEach-Object -Parallel` with `-ThrottleLimit`
3. **Delegates to Invoke-CopilotTask** — each iteration calls the core script with:
   - A `-PrependPrompt` that provides the specific run context
   - A `-PromptFile` with the reusable task instructions
   - A `-Name` for run tracking and idempotency (`-RunOnce`)
   - `-DisplayFiles` pointing to the expected output

### The prompt file pattern

The prompt ([ci-failure.prompt.md](./assets/ci-failure.prompt.md)) contains:

- **Frontmatter** — optional agent selection, description
- **Instructions** — step-by-step procedure for the agent to follow
- **Output format** — expected file path and markdown structure

The prompt is generic and reusable. The wrapper injects run-specific context via `-PrependPrompt`.

## Core Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `-PrependPrompt` | string | Inline prompt (prepended to prompt file content if both given) |
| `-PromptFile` | string | Path to a `.prompt.md` file |
| `-Name` | string | Run name — output goes to `.copilot_runs/<Name>/` |
| `-DefaultAgent` | string | Default agent name to select |
| `-Agent` | string[] | Agent names to load without selecting a default agent (`-AgentNames`/`-Agents` aliases) |
| `-AgentFolders` | string[] | Ordered folders for resolving named agents (defaults to `.github/agents`, then `~/.copilot/agents`) |
| `-AgentFile` | string[] | Explicit `.agent.md` files to load |
| `-Model` | string | Model ID passed unchanged to Copilot (default: `claude-opus-4.6`); no hardcoded allowlist |
| `-RunOnce` | switch | Skip if previous run succeeded with same `-Version` |
| `-Check` | switch | Return `$true`/`$false` without running |
| `-Version` | string | Version tag for idempotent run tracking |
| `-AdditionalPrompts` | string[] | Follow-up prompts in the same session |
| `-promptSuccessYesNoQuestion` | string | Yes/no question to determine success |
| `-McpConfigFile` | string[] | MCP config paths (default: `.mcp.json`, `~/.copilot/mcp-config.json`; `-McpConfigFiles` and `-McpConfigSource` aliases) |

Model availability is determined by the Copilot runtime and your account, not by the script. New model IDs can be supplied without updating the script; unsupported IDs are reported by Copilot.

## Run Output

Each run produces a tracked output directory:

```
.copilot_runs/<Name>/
├── prerun_details.json   # Config snapshot before execution
├── prompt.txt            # The resolved prompt
├── mcp-config_1.json     # First MCP config used (if any)
├── mcp-config_2.json     # Second MCP config used (if any)
├── pwsh_capture.md       # Streaming output log
└── run_details.json      # Final results (success, duration, exit code)
```

## Working with Run Results

For querying, filtering, retrying failed runs, and using `-Check` for conditional logic, see the [parsing copilot runs reference](./references/parsing-copilot-runs.md).

## Building Your Own Wrapper

Follow the same pattern to create any autonomous Copilot workflow:

```pwsh
# my-wrapper.ps1 — minimal example
param([string]$Input)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

& "$scriptDir/Invoke-CopilotTask.ps1" `
    -PrependPrompt "Process: $Input" `
    -PromptFile    "$scriptDir/my-task.prompt.md" `
    -Name          "my-task/$Input" `
    -RunOnce
```

The key design decisions for your wrapper:
- **What context to gather** before calling Invoke-CopilotTask
- **How to parameterize** the prompt (via `-PrependPrompt`)
- **Whether to loop** over multiple items (parallel or sequential)
- **What `-Name` scheme** to use for tracking and idempotency
