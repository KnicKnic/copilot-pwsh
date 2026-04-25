# Parsing .copilot_runs

Each Invoke-CopilotTask run creates a directory under `.copilot_runs/<Name>/` with JSON metadata files. This reference explains the schema and shows how to query, filter, and retry runs.

## run_details.json Schema

```json
{
  "timestamp": "2026-04-24T10:30:00.0000000-07:00",
  "promptName": "ci-failure",
  "promptFile": "tools/ci-failure.prompt.md",
  "agent": "code-review",
  "agentFiles": ["C:\\code\\my-repo\\.github\\agents\\code-review.agent.md"],
  "sessionId": "a1b2c3d4-...",
  "name": "ci/12345",
  "model": "claude-sonnet-4.6",
  "version": "0",
  "systemMessage": "...",
  "workingDirectory": "C:\\code\\my-repo",
  "mcpConfigPath": "C:\\code\\my-repo\\mcp-config.json",
  "gitBranch": "main",
  "gitCommit": "abc1234def5678...",
  "success": true,
  "exitCode": 0,
  "duration": 45.23,
  "displayFiles": ["ci-failures/12345.md"],
  "urlRegexp": "https://github.com/owner/repo/actions/runs/12345"
}
```

### Key Fields

| Field | Type | Description |
|-------|------|-------------|
| `success` | bool | `true` if the run completed successfully |
| `exitCode` | int | 0 = no errors, 1 = execution error |
| `duration` | float | Seconds elapsed |
| `version` | string | Version tag — used by `-RunOnce` for idempotency |
| `name` | string | Run name (matches the directory path) |
| `timestamp` | string | ISO 8601 start time |
| `model` | string | Model used |
| `gitBranch` | string | Branch at time of run (null if not a git repo) |
| `gitCommit` | string | Full commit hash (null if not a git repo) |
| `displayFiles` | string[] | Output files the run was expected to produce |

## Querying Runs

### Load all run details

```pwsh
$runs = Get-ChildItem .copilot_runs -Recurse -Filter run_details.json |
    ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json }
```

### Count successes and failures

```pwsh
$summary = $runs | Group-Object success
$passed = ($summary | Where-Object Name -eq 'True').Count
$failed = ($summary | Where-Object Name -eq 'False').Count
Write-Host "Passed: $passed, Failed: $failed"
```

### List failed runs

```pwsh
$runs | Where-Object { -not $_.success } |
    Select-Object name, exitCode, duration, timestamp |
    Format-Table
```

### Filter by prompt or agent

```pwsh
# All runs using a specific prompt
$runs | Where-Object { $_.promptName -eq 'ci-failure' }

# All runs using a specific agent
$runs | Where-Object { $_.agent -eq 'code-review' }

# All runs using a specific model
$runs | Where-Object { $_.model -eq 'claude-sonnet-4.6' }
```

### Filter by date range

```pwsh
$since = (Get-Date).AddDays(-1)
$runs | Where-Object { [datetime]$_.timestamp -gt $since }
```

### Longest and shortest runs

```pwsh
$runs | Sort-Object duration -Descending | Select-Object -First 5 name, duration, success
```

### Total time spent

```pwsh
$total = ($runs | Measure-Object duration -Sum).Sum
Write-Host "Total: $([math]::Round($total / 60, 1)) minutes across $($runs.Count) runs"
```

## Retrying Failed Runs

### Retry all failed runs

The `-RunOnce` flag checks `run_details.json` for `success == true` with a matching `version`. To retry, either delete the run directory or call without `-RunOnce`:

```pwsh
# Option 1: Delete failed run directories, then re-run your wrapper
$runs | Where-Object { -not $_.success } | ForEach-Object {
    $dir = ".copilot_runs/$($_.name)"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
# Then re-run your wrapper — RunOnce will re-execute since the directories are gone
```

```pwsh
# Option 2: Re-invoke directly without -RunOnce
$runs | Where-Object { -not $_.success } | ForEach-Object {
    & tools/Invoke-CopilotTask.ps1 `
        -PrependPrompt $_.promptName `
        -Name $_.name `
        -Model $_.model
}
```

### Retry with a different model

```pwsh
$runs | Where-Object { -not $_.success } | ForEach-Object {
    $dir = ".copilot_runs/$($_.name)"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }

    & tools/Invoke-CopilotTask.ps1 `
        -PrependPrompt (Get-Content "$dir/../$($_.name)/prompt.txt" -Raw -ErrorAction SilentlyContinue) `
        -Name $_.name `
        -Model "claude-opus-4.6"
}
```

### Bump version to force re-run (keeps history)

Instead of deleting, pass a new `-Version` so `-RunOnce` doesn't skip:

```pwsh
& tools/Invoke-CopilotTask.ps1 `
    -PromptFile tools/my-task.prompt.md `
    -Name "my-task/item1" `
    -RunOnce -Version "2"   # was "1" before
```

## Using -Check for Conditional Logic

```pwsh
# Check if a run already succeeded (no execution)
$done = & tools/Invoke-CopilotTask.ps1 -Name "setup" -Version "1" -Check

if (-not $done) {
    Write-Host "Setup not complete, running..."
    & tools/Invoke-CopilotTask.ps1 -PrependPrompt "Run setup" -Name "setup" -Version "1"
}
```

## prerun_details.json

Contains the same fields as `run_details.json` minus `success`, `exitCode`, and `duration`. Written before execution starts — useful for debugging runs that crashed without producing `run_details.json`.

```pwsh
# Find runs that started but never completed (no run_details.json)
$prerun = Get-ChildItem .copilot_runs -Recurse -Filter prerun_details.json
$completed = Get-ChildItem .copilot_runs -Recurse -Filter run_details.json |
    ForEach-Object { $_.Directory.FullName }

$crashed = $prerun | Where-Object { $_.Directory.FullName -notin $completed }
$crashed | ForEach-Object {
    $details = Get-Content $_.FullName -Raw | ConvertFrom-Json
    Write-Host "Incomplete: $($details.name) started at $($details.timestamp)"
}
```
