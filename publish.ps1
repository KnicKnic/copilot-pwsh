#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publish CopilotShell to the PowerShell Gallery.

.DESCRIPTION
    Builds a PSGallery-ready package (without the 105 MB copilot.exe native binary)
    and publishes it. Users who install via Install-Module will need `copilot` on PATH,
    or can install the full bundle from GitHub releases.

    Requires a PSGallery API key — get one at https://www.powershellgallery.com/account/apikeys

.PARAMETER ApiKey
    PSGallery API key. If not provided, reads from $env:PSGALLERY_API_KEY.

.PARAMETER WhatIf
    Show what would be published without actually publishing.

.PARAMETER Version
    Override the module version (default: read from CopilotShell.psd1).

.EXAMPLE
    ./publish.ps1 -ApiKey 'oy2m...'
    ./publish.ps1 -WhatIf                     # dry run
    $env:PSGALLERY_API_KEY = 'oy2m...'; ./publish.ps1
#>
[CmdletBinding()]
param(
    [string]$ApiKey,
    [string]$Version,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# Auto-load .env file if present
$envFile = Join-Path $PSScriptRoot '.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]+?)\s*=\s*(.+)$') {
            Set-Item "env:$($Matches[1].Trim())" $Matches[2].Trim()
        }
    }
}

if (-not $ApiKey) { $ApiKey = $env:PSGALLERY_API_KEY }
if (-not $ApiKey -and -not $WhatIf) {
    throw 'No API key provided. Pass -ApiKey or set $env:PSGALLERY_API_KEY. Get a key at https://www.powershellgallery.com/account/apikeys'
}

$projectDir  = Join-Path $PSScriptRoot 'src'
$wrapperDir  = Join-Path $PSScriptRoot 'mcp-wrapper'
$stageDir    = Join-Path $PSScriptRoot 'stage' 'CopilotShell'

# ── Clean staging area ──
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Write-Host '🔨 Building for PSGallery (no native binaries)...' -ForegroundColor Cyan

# Build CopilotShell — framework-dependent, no RID (portable), skip CLI binary download
dotnet publish (Join-Path $projectDir 'CopilotShell.csproj') `
    --configuration Release `
    --output $stageDir `
    --no-self-contained `
    -p:CopilotSkipCliDownload=true
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish CopilotShell failed.' }

# Build mcp-wrapper — framework-dependent, no RID (portable)
dotnet publish (Join-Path $wrapperDir 'mcp-wrapper.csproj') `
    --configuration Release `
    --output $stageDir `
    --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish mcp-wrapper failed.' }

# Copy module manifest and script files
Copy-Item (Join-Path $projectDir 'CopilotShell.psd1') -Destination $stageDir -Force
Copy-Item (Join-Path $projectDir 'Format-CopilotEvent.ps1') -Destination $stageDir -Force
Copy-Item (Join-Path $projectDir 'StartupCheck.ps1') -Destination $stageDir -Force

# ── Remove leftover runtime artifacts (just in case) ──
$runtimesDir = Join-Path $stageDir 'runtimes'
if (Test-Path $runtimesDir) {
    Remove-Item $runtimesDir -Recurse -Force
    Write-Host '  Removed runtimes/ folder (CLI binary not needed — auto-downloaded at runtime)' -ForegroundColor Yellow
}

# ── Move dependency DLLs to dependencies/ for ALC isolation ──
# (see build.ps1 and StartupCheck.ps1 for details)
Write-Host '📦 Moving dependencies to isolation directory...' -ForegroundColor Cyan
$depsDir = Join-Path $stageDir 'dependencies'
New-Item -ItemType Directory -Path $depsDir -Force | Out-Null

$keepInRoot = @(
    'CopilotShell.dll'
    'CopilotShell.psd1'
    'CopilotShell.xml'
    'CopilotShell.deps.json'
    'Format-CopilotEvent.ps1'
    'StartupCheck.ps1'
    'mcp-wrapper.dll'
    'mcp-wrapper.exe'
    'mcp-wrapper.deps.json'
    'mcp-wrapper.runtimeconfig.json'
)

Get-ChildItem $stageDir -Filter '*.dll' -File |
    Where-Object { $_.Name -notin $keepInRoot } |
    Move-Item -Destination $depsDir -Force

$languageDirs = @('cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant')
foreach ($lang in $languageDirs) {
    $langDir = Join-Path $stageDir $lang
    if (Test-Path $langDir) {
        Move-Item $langDir -Destination $depsDir -Force
    }
}

# ── Stamp build date ──
$manifestDest = Join-Path $stageDir 'CopilotShell.psd1'
$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
(Get-Content $manifestDest -Raw) -replace "BuildDate\s*=\s*'source'", "BuildDate  = '$buildDate'" |
    Set-Content $manifestDest -NoNewline

# ── Override version if specified ──
if ($Version) {
    Write-Host "  Setting version to $Version" -ForegroundColor Cyan
    (Get-Content $manifestDest -Raw) -replace "ModuleVersion\s*=\s*'[^']+'", "ModuleVersion     = '$Version'" |
        Set-Content $manifestDest -NoNewline
}

# ── Remove files PSGallery doesn't need ──
@('*.pdb', '*.deps.json', '*.runtimeconfig.json') | ForEach-Object {
    # Keep CopilotShell.deps.json (PowerShell needs it for assembly loading)
    # But remove mcp-wrapper deps/runtimeconfig (not needed for module loading)
    Get-ChildItem $stageDir -Filter $_ -File | Where-Object {
        $_.Name -like 'mcp-wrapper*' -or $_.Extension -eq '.pdb'
    } | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "  Removed $($_.Name)" -ForegroundColor DarkGray
    }
}

# ── Show package contents ──
Write-Host ''
Write-Host '📦 Package contents:' -ForegroundColor Cyan
$totalSize = 0
Get-ChildItem $stageDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($stageDir.Length + 1)
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    $totalSize += $_.Length
    Write-Host "  $rel ($sizeMB MB)" -ForegroundColor DarkGray
}
Write-Host "  Total: $([math]::Round($totalSize / 1MB, 1)) MB" -ForegroundColor White
Write-Host ''

# ── Validate manifest ──
Write-Host '🔍 Validating module manifest...' -ForegroundColor Cyan
try {
    $manifest = Test-ModuleManifest -Path $manifestDest -ErrorAction Stop
    Write-Host "  Name: $($manifest.Name)" -ForegroundColor DarkGray
    Write-Host "  Version: $($manifest.Version)" -ForegroundColor DarkGray
    Write-Host "  Cmdlets: $($manifest.ExportedCmdlets.Count)" -ForegroundColor DarkGray
}
catch {
    throw "Module manifest validation failed: $_"
}

# ── Publish ──
if ($WhatIf) {
    Write-Host '🔍 DRY RUN — would publish to PSGallery:' -ForegroundColor Yellow
    Write-Host "  Module: $stageDir" -ForegroundColor DarkGray
    Write-Host "  Version: $($manifest.Version)" -ForegroundColor DarkGray
    Write-Host ''
    Write-Host 'Run without -WhatIf to publish.' -ForegroundColor Yellow
}
else {
    Write-Host '🚀 Publishing to PSGallery...' -ForegroundColor Cyan
    Publish-Module -Path $stageDir -NuGetApiKey $ApiKey -Verbose
    Write-Host ''
    Write-Host '✅ Published! Users can now install with:' -ForegroundColor Green
    Write-Host '  Install-Module CopilotShell' -ForegroundColor White
    Write-Host ''
    Write-Host '  Note: copilot CLI must be on PATH (not bundled in PSGallery package).' -ForegroundColor Yellow
    Write-Host '  For the full bundle with copilot.exe, use GitHub releases or web-install.ps1.' -ForegroundColor Yellow
}
