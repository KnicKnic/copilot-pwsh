---
name: invoke-copilot-task
description: 'Run autonomous Copilot tasks via Invoke-CopilotTask.ps1. Use when executing prompt files, running headless agent workflows, automating multi-step Copilot sessions, orchestrating CI-style Copilot tasks with MCP tools, or adding Invoke-CopilotTask to a new repository.'
---

# Invoke-CopilotTask

Runs autonomous Copilot sessions from the command line. Handles MCP config conversion, agent file resolution, session lifecycle, run tracking, and success validation.

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

If your task needs MCP tools (GitHub CLI, ADO, etc.), make sure `.vscode/mcp.json` exists in the target repo. Invoke-CopilotTask auto-converts it to Copilot CLI format.

## Agent and Prompt Resolution

### How the agent is resolved

The agent is resolved in priority order:

1. **`-Agent` parameter** — always wins if provided
2. **Prompt file frontmatter** — if `-PromptFile` has `agent: 'my-agent'` in its YAML frontmatter
3. **No agent** — if neither is set, the session runs without a named agent

Once an agent name is resolved, the script looks for `.github/agents/<name>.agent.md` in the working directory. If the file doesn't exist and no `-AgentFile` was provided, it throws an error.

You can also pass explicit agent file paths with `-AgentFile` — these are used in addition to the convention-based lookup.

### When `-Agent` is necessary

- **No prompt file** — if you only use `-PrependPrompt`, there's no frontmatter to read the agent from. You must pass `-Agent` explicitly (or omit it to run without one).
- **Multiple agents** — if your repo has several `.agent.md` files and you want to pick one that differs from what the prompt file specifies, use `-Agent` to override.
- **Reusing a prompt with different agents** — the same prompt file can be run with different agent configurations by passing `-Agent` at the wrapper level.

### When `-Agent` is not needed

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
| `-Agent` | string | Agent name (resolves `.github/agents/<name>.agent.md`) |
| `-Model` | string | Model to use (default: `claude-opus-4.6`) |
| `-RunOnce` | switch | Skip if previous run succeeded with same `-Version` |
| `-Check` | switch | Return `$true`/`$false` without running |
| `-Version` | string | Version tag for idempotent run tracking |
| `-AdditionalPrompts` | string[] | Follow-up prompts in the same session |
| `-promptSuccessYesNoQuestion` | string | Yes/no question to determine success |
| `-McpConfigSource` | string | MCP config path (default: `.vscode/mcp.json`) |
| `-SkipMcpConfig` | switch | Skip MCP config conversion |

## Run Output

Each run produces a tracked output directory:

```
.copilot_runs/<Name>/
├── prerun_details.json   # Config snapshot before execution
├── prompt.txt            # The resolved prompt
├── mcp-config.json       # MCP config used
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
