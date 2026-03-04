#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build and optionally install the CopilotShell PowerShell module.

.DESCRIPTION
    Publishes the CopilotShell C# project and copies the module manifest.
    With -Install, elevates to admin and runs install.ps1 to reinstall
    into C:\Program Files\PowerShell\7-preview\Modules.

.PARAMETER Clean
    Remove previous build output before building.

.PARAMETER Install
    Reinstall the module after building (elevates to admin automatically).

.EXAMPLE
    ./build.ps1              # build only
    ./build.ps1 -Clean -Install
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$projectDir       = Join-Path $PSScriptRoot 'src'
$outputDir        = Join-Path $PSScriptRoot 'output' 'CopilotShell'
$projectFile      = Join-Path $projectDir 'CopilotShell.csproj'
$manifestSrc      = Join-Path $projectDir 'CopilotShell.psd1'
$wrapperDir       = Join-Path $PSScriptRoot 'mcp-wrapper'
$wrapperProject   = Join-Path $wrapperDir 'mcp-wrapper.csproj'

# ── Unload module if loaded (prevents file locks) ──
if (Get-Module CopilotShell -ErrorAction SilentlyContinue) {
    Write-Host '🔓 Unloading CopilotShell module...' -ForegroundColor Yellow
    Remove-Module CopilotShell -Force -ErrorAction SilentlyContinue
}

# ── Clean ──
if ($Clean -and (Test-Path $outputDir)) {
    Write-Host '🧹 Cleaning previous output...' -ForegroundColor Yellow
    try {
        Remove-Item $outputDir -Recurse -Force
    }
    catch {
        Write-Warning "Could not fully clean output (files may be locked by another process). Renaming old directory..."
        $stale = "${outputDir}_old_$(Get-Date -Format 'yyyyMMddHHmmss')"
        try {
            Rename-Item $outputDir $stale -Force
            # Best-effort background cleanup
            Start-Job { Remove-Item $using:stale -Recurse -Force -ErrorAction SilentlyContinue } | Out-Null
        }
        catch {
            Write-Warning "Cannot rename locked directory. Build will overwrite in-place."
        }
    }
}

# ── Build CopilotShell ──
Write-Host '🔨 Building CopilotShell...' -ForegroundColor Cyan
dotnet publish $projectFile `
    --configuration Release `
    --output $outputDir `
    --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish CopilotShell failed.' }

# ── Build mcp-wrapper ──
Write-Host '🔨 Building mcp-wrapper...' -ForegroundColor Cyan
dotnet publish $wrapperProject `
    --configuration Release `
    --output $outputDir `
    --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish mcp-wrapper failed.' }

# Copy the module manifest and PowerShell script files into the output directory
Copy-Item $manifestSrc -Destination $outputDir -Force

# Stamp build date into the manifest
$manifestDest = Join-Path $outputDir 'CopilotShell.psd1'
$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
(Get-Content $manifestDest -Raw) -replace "BuildDate\s*=\s*'source'", "BuildDate  = '$buildDate'" |
    Set-Content $manifestDest -NoNewline

$formatCopilotEventSrc = Join-Path $projectDir 'Format-CopilotEvent.ps1'
Copy-Item $formatCopilotEventSrc -Destination $outputDir -Force

Write-Host "✅ Build output: $outputDir" -ForegroundColor Green

# ── Install ──
if ($Install) {
    $installScript = Join-Path $PSScriptRoot 'install.ps1'
    $installLog = Join-Path $PSScriptRoot 'install.log'
    Write-Host '⚡ Elevating to reinstall...' -ForegroundColor Yellow
    $proc = Start-Process (Get-Process -Id $PID).Path `
        -ArgumentList '-NoProfile', '-Command', "try { & '$installScript' *> '$installLog' } catch { `$_ >> '$installLog'; exit 1 }" `
        -Verb RunAs -Wait -PassThru
    if (Test-Path $installLog) {
        Get-Content $installLog
    }
    if ($proc.ExitCode -ne 0) { throw 'install.ps1 failed.' }
}
