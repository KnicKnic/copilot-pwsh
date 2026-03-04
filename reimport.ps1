#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Re-import CopilotShell into the current session.

.DESCRIPTION
    Unloads the module and imports it from the build output.
    Use -Build to run a clean build first.

.PARAMETER Build
    Run a clean build before importing.

.EXAMPLE
    ./reimport.ps1           # reimport existing build output
    ./reimport.ps1 -Build    # clean build + reimport
#>
[CmdletBinding()]
param(
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

# Check .NET runtime version — module requires .NET 10
$fxVersion = [System.Environment]::Version
if ($fxVersion.Major -lt 10) {
    Write-Warning "Current pwsh runs on .NET $fxVersion. CopilotShell requires .NET 10+."
    Write-Warning "Use pwsh 7.6 preview:  pwsh-preview -File $PSCommandPath"
    throw "Incompatible .NET runtime. Need .NET 10+, have .NET $fxVersion."
}

# Unload if loaded
if (Get-Module CopilotShell -ErrorAction SilentlyContinue) {
    Write-Host '🔓 Unloading CopilotShell...' -ForegroundColor Yellow
    Remove-Module CopilotShell -Force
}

# Build + install
if ($Build) {
    & "$PSScriptRoot\build.ps1" -Clean
    & "$PSScriptRoot\install.ps1"
}

# Import from build output
$modulePath = Join-Path $PSScriptRoot 'output' 'CopilotShell' 'CopilotShell.psd1'
if (-not (Test-Path $modulePath)) {
    throw "Build output not found at $modulePath. Run with -Build first."
}

Write-Host '📥 Importing CopilotShell...' -ForegroundColor Cyan
Import-Module $modulePath -Force
Get-Command -Module CopilotShell | Format-Table Name, CommandType -AutoSize
Write-Host '✅ Ready!' -ForegroundColor Green
