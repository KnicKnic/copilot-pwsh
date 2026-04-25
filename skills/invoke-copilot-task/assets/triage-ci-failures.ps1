param(
    [Parameter(Mandatory=$true)]
    [string]$Repo,

    [Parameter(Mandatory=$false)]
    [int]$Days = 7,

    [Parameter(Mandatory=$false)]
    [string]$Workflow = "",

    [Parameter(Mandatory=$false)]
    [int]$Limit = 100,

    [Parameter(Mandatory=$false)]
    [int]$Throttle = 1,

    [Parameter(Mandatory=$false)]
    [switch]$DryRun
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Setup logging
$timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$logDir = ".logs/triage-ci-failures"
$logFile = "$logDir/$timestamp.log"

if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

Write-Host "Logging to: $logFile"
Start-Transcript -Path $logFile -Append

try {
    $getRunsArgs = @{Repo=$Repo; Days=$Days; Limit=$Limit; IdsOnly=$true}
    if ($Workflow) { $getRunsArgs['Workflow'] = $Workflow }

    $runIds = & "$scriptDir\get-failed-runs.ps1" @getRunsArgs

    if (-not $runIds) {
        Write-Host "No failed runs found for '$Repo' in the past $Days day(s)." -ForegroundColor Yellow
        return
    }

    Write-Host "Found $(@($runIds).Count) failed run(s) to process." -ForegroundColor Cyan

    if ($DryRun) {
        Write-Host "Dry run — would process:" -ForegroundColor Yellow
        $runIds | ForEach-Object { Write-Host "  https://github.com/$Repo/actions/runs/$_" }
        return
    }

    $repoEscaped = $Repo -replace '/', '\/'
    $urlBase = "https://github.com/$Repo/actions/runs"

    $runIds | ForEach-Object -Parallel {
        $runId = $_
        $invokeScript = Join-Path $using:scriptDir "Invoke-CopilotTask.ps1"
        $promptFile   = Join-Path $using:scriptDir "ci-failure.prompt.md"

        & $invokeScript `
            "Diagnose the failed GitHub Actions run $runId in repo $using:Repo" `
            -PromptFile $promptFile `
            -Name       "ci/$runId" `
            -RunOnce `
            -UrlRegexp  "$using:urlBase/$runId" `
            -model "claude-sonnet-4.6" `
            -DisplayFiles "ci-failures/$runId.md"
    } -ThrottleLimit $Throttle
}
finally {
    Stop-Transcript
    Write-Host "Log saved to: $logFile"
}
