<#
.SYNOPSIS
    Fetch recent failed CI runs for a GitHub repository.

.DESCRIPTION
    Lists failed GitHub Actions workflow runs from the last N days,
    showing run ID, workflow name, branch, commit, and timestamp.

.PARAMETER Repo
    GitHub repository in owner/repo format.

.PARAMETER Days
    How many days back to search (default: 7).

.PARAMETER Workflow
    Optional workflow name/filename filter.

.PARAMETER Limit
    Maximum number of runs to return (default: 100).

.EXAMPLE
    .\tools\fetch-ci-failures.ps1 -Repo KnicKnic/copilot-pwsh -Days 5
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$Repo,

    [Parameter(Mandatory=$false)]
    [int]$Days = 7,

    [Parameter(Mandatory=$false)]
    [string]$Workflow = "",

    [Parameter(Mandatory=$false)]
    [int]$Limit = 100
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$getRunsArgs = @{Repo=$Repo; Days=$Days; Limit=$Limit}
if ($Workflow) { $getRunsArgs['Workflow'] = $Workflow }

$runs = & "$scriptDir\get-failed-runs.ps1" @getRunsArgs

if (-not $runs) {
    Write-Host "No failed runs found for '$Repo' in the past $Days day(s)." -ForegroundColor Yellow
}
