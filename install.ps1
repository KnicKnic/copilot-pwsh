#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reinstall CopilotShell module to the shared PowerShell Modules directory.
    Requires elevation (Run as Administrator).

.DESCRIPTION
    Removes any existing CopilotShell install, then copies the build output to
    C:\Program Files\PowerShell\Modules\CopilotShell.
    Always does a full clean reinstall.

    Run build.ps1 first, then run this script elevated, or use build.ps1 -Install
    which elevates automatically.

.EXAMPLE
    ./install.ps1                        # from admin prompt
    ./build.ps1 -Clean -Install          # build + auto-elevate install
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Self-elevate if not running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '⚡ Elevating to admin...' -ForegroundColor Yellow
    $logFile = Join-Path $PSScriptRoot 'install.log'
    $scriptPath = $PSCommandPath
    $proc = Start-Process (Get-Process -Id $PID).Path `
        -ArgumentList '-NoProfile', '-Command', "try { & '$scriptPath' *> '$logFile' } catch { $_ >> '$logFile'; exit 1 }" `
        -Verb RunAs -Wait -PassThru
    if (Test-Path $logFile) { Get-Content $logFile }
    exit $proc.ExitCode
}

$outputDir  = Join-Path $PSScriptRoot 'output' 'CopilotShell'
$installDir = 'C:\Program Files\PowerShell\Modules\CopilotShell'

if (-not (Test-Path $outputDir)) {
    throw "Build output not found at $outputDir. Run build.ps1 first."
}

# Unload module if loaded
if (Get-Module CopilotShell -ErrorAction SilentlyContinue) {
    Write-Host '🔓 Unloading CopilotShell module...' -ForegroundColor Yellow
    Remove-Module CopilotShell -Force -ErrorAction SilentlyContinue
}

# Use robocopy to mirror files — it handles locked files much better than Copy-Item.
# Locked DLLs (e.g. loaded by VS Code terminals) will be skipped with a warning,
# but all other files get updated correctly. /MIR mirrors the directory tree.
Write-Host "📦 Installing to $installDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $installDir -Force | Out-Null

# First, rename any locked files out of the way so robocopy can place new ones.
# Windows allows renaming a locked file — the old handle keeps working on the
# renamed file, while the new file takes the original name.

# Also clean up any stale .old_ files from previous installs
Get-ChildItem $installDir -Filter '*.old_*' -File -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        Remove-Item $_.FullName -Force -ErrorAction Stop
        Write-Host "  Cleaned up stale file: $($_.Name)" -ForegroundColor DarkGray
    }
    catch { }
}

$staleFiles = @()
foreach ($srcFile in Get-ChildItem $outputDir -File -Recurse) {
    $relPath = $srcFile.FullName.Substring($outputDir.Length)
    $destFile = Join-Path $installDir $relPath
    if (Test-Path $destFile) {
        # Check if file differs (skip if identical)
        $srcHash = (Get-FileHash $srcFile.FullName -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash $destFile -Algorithm SHA256).Hash
        if ($srcHash -ne $dstHash) {
            # Try to rename the locked file out of the way
            $staleName = "$destFile.old_$([guid]::NewGuid().ToString('N').Substring(0,8))"
            try {
                Rename-Item $destFile $staleName -Force -ErrorAction Stop
                $staleFiles += $staleName
                Write-Host "  Renamed locked file: $(Split-Path $destFile -Leaf) → $(Split-Path $staleName -Leaf)" -ForegroundColor Yellow
            }
            catch {
                # File isn't locked or rename failed — robocopy will handle it normally
            }
        }
    }
}

$roboOutput = & robocopy $outputDir $installDir /MIR /R:1 /W:1 /NJH /NJS /NDL /NP 2>&1
$roboExit = $LASTEXITCODE

# robocopy exit codes: 0=no change, 1=files copied, 2=extra files deleted, 3=both — all OK
# Codes >= 8 indicate errors
if ($roboExit -ge 8) {
    Write-Warning "robocopy reported errors (exit $roboExit). Some files may be locked by VS Code terminals."
    $roboOutput | ForEach-Object { Write-Host "  $_" }
}
elseif ($roboExit -ge 4) {
    # Code 4 = some mismatched files/dirs; code 5/6/7 = partial
    Write-Warning "robocopy: some files could not be copied (exit $roboExit) — likely locked by VS Code."
    $roboOutput | Where-Object { $_ -match 'FAILED|ERROR' } | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}

# Clean up stale renamed files (best-effort — may fail if still locked)
foreach ($stale in $staleFiles) {
    try {
        Remove-Item $stale -Force -ErrorAction SilentlyContinue
    }
    catch {
        Write-Host "  Stale file will be cleaned up on next install: $(Split-Path $stale -Leaf)" -ForegroundColor DarkGray
    }
}

# Verify critical files were installed
$criticalFiles = @('CopilotShell.dll', 'CopilotShell.psd1', 'mcp-wrapper.exe', 'dependencies\GitHub.Copilot.SDK.dll')
$missing = $criticalFiles | Where-Object { -not (Test-Path (Join-Path $installDir $_)) }
if ($missing) {
    Write-Warning "These files could not be installed (locked?): $($missing -join ', ')"
    Write-Warning "Close all pwsh / VS Code terminals and run: ./build.ps1 -Clean -Install"
}

# Check if DLL is outdated (locked copy from a previous build)
$sourceDll = Join-Path $outputDir 'CopilotShell.dll'
$installedDll = Join-Path $installDir 'CopilotShell.dll'
if ((Test-Path $sourceDll) -and (Test-Path $installedDll)) {
    $srcSize = (Get-Item $sourceDll).Length
    $dstSize = (Get-Item $installedDll).Length
    if ($srcSize -ne $dstSize) {
        Write-Warning "CopilotShell.dll is locked (old: $dstSize bytes, new: $srcSize bytes)."
        Write-Warning "Close all pwsh / VS Code terminals and rerun: ./build.ps1 -Clean -Install"
    }
}

Write-Host '✅ Installed! Run:  Import-Module CopilotShell' -ForegroundColor Green

# Warn if a user-scope module copy shadows the system install.
# User Documents/PowerShell/Modules is checked BEFORE Program Files in PSModulePath.
$userModuleDirs = @(
    [IO.Path]::Combine([Environment]::GetFolderPath('MyDocuments'), 'PowerShell', 'Modules', 'CopilotShell')
    if ($env:OneDrive) { [IO.Path]::Combine($env:OneDrive, 'Documents', 'PowerShell', 'Modules', 'CopilotShell') }
)
foreach ($userDir in $userModuleDirs | Select-Object -Unique) {
    if ((Test-Path $userDir) -and $userDir -ne $installDir) {
        Write-Warning "A user-scope module copy exists at: $userDir"
        Write-Warning "This SHADOWS the system install at $installDir."
        Write-Warning "Run: Remove-Item '$userDir' -Recurse -Force  (or update it manually)"
    }
}

# Ensure the install directory is on PSModulePath for this session
$modulesRoot = Split-Path $installDir
if ($env:PSModulePath -notlike "*$modulesRoot*") {
    $env:PSModulePath += ";$modulesRoot"
}
