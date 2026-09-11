#region Parameters
param(
    [Parameter(Mandatory=$false, Position=0)]
    [string]$PrependPrompt = "",

    [Parameter(Mandatory=$false, Position=1)]
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        Get-ChildItem -Path ".github/prompts" -Filter "*.prompt.md" -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName -replace [regex]::Escape((Get-Location).Path + '\'), '' -replace '\\', '/' } |
            Where-Object { $_ -like "*$wordToComplete*" }
    })]
    [string]$PromptFile = "",

    [Parameter(Mandatory=$false)]
    [string]$Name = "",

    [Parameter(Mandatory=$false)]
    [switch]$RunOnce,

    [Parameter(Mandatory=$false)]
    [string]$Version = "0",

    [Parameter(Mandatory=$false)]
    [int]$MaxTurns = 0,

    [Parameter(Mandatory=$false)]
    [Alias("McpConfigFiles", "McpConfigSource")]
    [string[]]$McpConfigFile = @(".mcp.json", "~/.copilot/mcp-config.json"),

    [Parameter(Mandatory=$false)]
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        Get-ChildItem -Path ".github/agents" -Filter "*.agent.md" -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName -replace [regex]::Escape((Get-Location).Path + '\'), '' -replace '\\', '/' } |
            Where-Object { $_ -like "*$wordToComplete*" }
    })]
    [string[]]$AgentFile = @(),

    [Parameter(Mandatory=$false)]
    [Alias("AgentNames", "Agents")]
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        [CopilotShell.CopilotAgentNameCompleter]::new().CompleteArgument(
            $commandName,
            $parameterName,
            $wordToComplete,
            $commandAst,
            $fakeBoundParameters)
    })]
    [string[]]$Agent = @(),

    [Parameter(Mandatory=$false)]
    [Alias("AgentFolder", "AgentFileFolder", "AgentFileFolders", "AgentPath", "AgentPaths")]
    [string[]]$AgentFolders = @(),

    [Parameter(Mandatory=$false)]
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        [CopilotShell.CopilotAgentNameCompleter]::new().CompleteArgument(
            $commandName,
            $parameterName,
            $wordToComplete,
            $commandAst,
            $fakeBoundParameters)
    })]
    [string]$DefaultAgent = "",

    [Parameter(Mandatory=$false)]
    [string]$Model = "claude-opus-4.6",

    [Parameter(Mandatory=$false)]
    [string[]]$DisplayFiles = @(),

    [Parameter(Mandatory=$false)]
    [string]$UrlRegexp = $null,

    [Parameter(Mandatory=$false)]
    [switch]$AllowCustomInstructions,

    [Parameter(Mandatory=$false)]
    [string]$SystemMessage = "",

    [Parameter(Mandatory=$false)]
    [string]$SystemMessageSuffix = "",

    [Parameter(Mandatory=$false)]
    [string[]]$AdditionalArgs = @(),

    [Parameter(Mandatory=$false)]
    [string] $promptSuccessYesNoQuestion = "",

    [Parameter(Mandatory=$false)]
    [string[]]$AdditionalPrompts = @(),

    [Parameter(Mandatory=$false)]
    [switch]$Check
)
#endregion

#region Sync process directory with PowerShell working directory
if ([System.IO.Directory]::GetCurrentDirectory() -ne (Get-Location).Path) {
    Write-Host "Warning: .NET process directory '$([System.IO.Directory]::GetCurrentDirectory())' differs from PowerShell directory '$((Get-Location).Path)'. Syncing." -ForegroundColor Yellow
    [System.IO.Directory]::SetCurrentDirectory((Get-Location).Path)
}
#endregion

#region MCP Config Resolution
$workspaceRoot = (Get-Location).Path
$mcpConfigFileWasSpecified = $PSBoundParameters.ContainsKey('McpConfigFile')
$mcpConfigPaths = @()
foreach ($configFile in $McpConfigFile) {
    $resolvedMcpConfigFile = if ([System.IO.Path]::IsPathRooted($configFile)) { $configFile } else { Join-Path $workspaceRoot $configFile }
    if (Test-Path -LiteralPath $resolvedMcpConfigFile) {
        $mcpConfigPaths += (Get-Item -LiteralPath $resolvedMcpConfigFile).FullName
    } elseif ($mcpConfigFileWasSpecified) {
        Write-Host "Warning: MCP config '$resolvedMcpConfigFile' not found; continuing without it." -ForegroundColor Yellow
    }
}
#endregion

#region Prompt Validation
$promptName = if ($PromptFile) { [System.IO.Path]::GetFileName($PromptFile) -replace '\.prompt\.md$', '' } else { "" }

if (-not $PromptFile -and -not $PrependPrompt) {
    throw "No prompt provided. Specify either -PrependPrompt or -PromptFile."
}
#endregion

#region Run Details Setup
# Build run details directory path from name parameter (or generate from timestamp_pid)
if (-not $Name) {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $Name = "empty/${timestamp}_$PID"
}

$runDetailsDir = ".copilot_runs/$Name"
$runDetailsPath = "$runDetailsDir/run_details.json"

# Check if previous run was successful - return true/false without running (only if Check is specified)
if ($Check) {
    if (Test-Path -LiteralPath $runDetailsPath) {
        $previousRun = Get-Content -LiteralPath $runDetailsPath -Raw | ConvertFrom-Json
        return ($previousRun.success -eq $true -and $previousRun.version -eq $Version)
    }
    return $false
}

# Check if previous run was successful - skip if so (only if RunOnce is specified)
if ($RunOnce -and (Test-Path -LiteralPath $runDetailsPath)) {
    $previousRun = Get-Content -LiteralPath $runDetailsPath -Raw | ConvertFrom-Json
    if ($previousRun.success -eq $true -and $previousRun.version -eq $Version) {
        Write-Host "Skipping - previous run was successful with same version ($Version)" -ForegroundColor Yellow
        Write-Output $true
        exit 0
    }
}

# Create run_details directory if it doesn't exist
if (-not (Test-Path -LiteralPath $runDetailsDir)) {
    [System.IO.Directory]::CreateDirectory($runDetailsDir) | Out-Null
}
#endregion

#region Pre-Run Artifacts
# Save the inline prompt component. Prompt file parsing is delegated to Send-CopilotMessage.
$PrependPrompt | Set-Content -LiteralPath "$runDetailsDir/prompt.txt"
if ($PromptFile -and (Test-Path -LiteralPath $PromptFile)) {
    Copy-Item -LiteralPath $PromptFile -Destination "$runDetailsDir/prompt_file.md" -Force
}

# Copy MCP configs used to the run_details folder if they exist
if ($mcpConfigPaths.Count -gt 0) {
    for ($i = 0; $i -lt $mcpConfigPaths.Count; $i++) {
        $fileName = "mcp-config_{0}.json" -f ($i + 1)
        Copy-Item -LiteralPath $mcpConfigPaths[$i] -Destination (Join-Path $runDetailsDir $fileName) -Force
    }
}

# Get git branch (silently fail if not a git repo)
$gitBranch = $null
try {
    $gitBranch = git rev-parse --abbrev-ref HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { $gitBranch = $null }
} catch { }

# Get git commit hash
$gitCommit = $null
try {
    $gitCommit = git rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { $gitCommit = $null }
} catch { }

# Define system message with file path guidance
$workingDirectory = (Get-Location).Path
if (-not $SystemMessage) {
    $SystemMessage = @"
You are a helpful fully autonomous agent.

CRITICAL - File Path Rules:
- All created files must use workspace-relative paths
- Your current working directory is: $workingDirectory
- NEVER use absolute paths like '/src/', 'C:\', or user home directories
- Temporary/scratch files go in: .copilot_runs/$Name/
- When using create_file, path is relative to workspace root for non temporary files (but absolute path should be passed), like outputs or summaries that should be saved. For example: 'pipelines/${pipelineId}_${buildId}.md'
"@
}

if ($SystemMessageSuffix) {
    $SystemMessage = $SystemMessage + "`n" + $SystemMessageSuffix
}

# Create prerun_details.json with all known information before execution
$sessionIdPlaceholder = [guid]::NewGuid().ToString()
$prerunDetails = @{
    timestamp = (Get-Date).ToString("o")
    promptName = $promptName
    promptFile = $PromptFile
    prependPrompt = $PrependPrompt
    defaultAgent = if ($DefaultAgent) { $DefaultAgent } else { $null }
    agents = $Agent
    agentFolders = $AgentFolders
    agentFiles = $AgentFile
    sessionId = $sessionIdPlaceholder
    name = if ($Name) { $Name } else { $null }
    model = $Model
    version = $Version
    systemMessage = $SystemMessage
    workingDirectory = $workingDirectory
    mcpConfigPaths = $mcpConfigPaths
    gitBranch = $gitBranch
    gitCommit = $gitCommit
    displayFiles = $DisplayFiles
    urlRegexp = $UrlRegexp
}

$prerunDetails | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$runDetailsDir/prerun_details.json"
#endregion

#region Execute Copilot
# Record start time
$startTime = Get-Date

# Build the command arguments
$sessionFile = "$runDetailsDir/session.md"
$promptFilePath = "$runDetailsDir/prompt.txt"

$sessionId = $null
$success = $null
try{
    $client = New-CopilotClient -cwd $((Get-Location).Path)
    try{
        $customAgentsArg = if ($AgentFile.Count -gt 0) { @{"-CustomAgentFile" = $AgentFile} } else { @{} }
        $agentsArg = if ($Agent.Count -gt 0) { @{"-Agent" = $Agent} } else { @{} }
        $agentFoldersArg = if ($AgentFolders.Count -gt 0) { @{"-AgentFolders" = $AgentFolders} } else { @{} }
        $defaultAgentArg = if ($DefaultAgent) { @{"-DefaultAgent" = $DefaultAgent} } else { @{} }
        $mcpConfigArg = if ($mcpConfigPaths.Count -gt 0) { @{"-McpConfigFile" = $mcpConfigPaths} } else { @{} }
        try {
            $session = New-CopilotSession $client `
                -SystemMessage $SystemMessage `
                -SystemMessageMode Replace `
                -InfiniteSessions -Model $Model -stream @mcpConfigArg @customAgentsArg @agentsArg @agentFoldersArg @defaultAgentArg
        } catch {
            throw "New-CopilotSession failed: $_"
        }
        $sessionId = $session.SessionId

        # Update prerun_details.json with actual session ID
        if ($sessionId) {
            $prerunDetails.sessionId = $sessionId
            $prerunDetails | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "$runDetailsDir/prerun_details.json"
        }

        try{
    try {
        # Pass the prompt file directly to the cmdlet when present (it parses the body);
        # otherwise fall back to the inline prompt. Agent files are discovered by
        # New-CopilotSession from its default/explicit agent file folders.
        $mainPromptArg = @{}
        if ($PromptFile) {
            $mainPromptArg["PromptFile"] = $PromptFile
        }
        # Keep the resolved agent authoritative so a prompt file's frontmatter agent
        # doesn't override an explicit -DefaultAgent selection.
        if ($DefaultAgent) {
            $mainPromptArg["Agent"] = $DefaultAgent
        }
        Send-CopilotMessage $session -MaxTurns $MaxTurns @mainPromptArg -PrependPrompt $PrependPrompt -timeout $(30*60) -stream | Format-CopilotEvent -LogFile "$runDetailsDir/pwsh_capture.md" | Out-Null
    } catch {
        throw "Send-CopilotMessage (main prompt) failed: $_"
    }

            # Execute additional prompts if provided
            if ($AdditionalPrompts.Count -gt 0) {
                Write-Host "Executing $($AdditionalPrompts.Count) additional prompt(s)..." -ForegroundColor Cyan
                foreach ($additionalPrompt in $AdditionalPrompts) {
                    try {
                        Send-CopilotMessage $session -prompt $additionalPrompt -timeout $(30*60) -stream | Format-CopilotEvent  -LogFile "$runDetailsDir/pwsh_capture.md" -Append | Out-Null
                    } catch {
                        throw "Send-CopilotMessage (additional prompt) failed: $_"
                    }
                }
            }

            if($promptSuccessYesNoQuestion -ne ""){
                try {
                    $yesNo = Send-CopilotMessage $session -prompt $promptSuccessYesNoQuestion
                } catch {
                    throw "Send-CopilotMessage (success check) failed: $_"
                }
                if($yesNo.trim() -ilike "yes*"){
                    $success = $true
                } else {
                    $success = $false
                    Write-Host "Prompt success indicator $promptSuccessYesNoQuestion returned '$yesNo' and I interpreted it as failure." -ForegroundColor Yellow
                }
            }
            else {
                Write-Host "No prompt success indicator specified, skipping success check." -ForegroundColor Yellow
            }

            if ($success -eq $null) {
                $success = $true
            }
            # Send-CopilotMessage $session -prompt "what tools are available to you, can you say them?" -timeout $(30*60) -stream | Format-CopilotEvent
        }
        finally {
            if ($success -eq $null -or $success -eq $false) {
                Write-Host "Session did not complete successfully leaving." -ForegroundColor Red
                Disconnect-CopilotSession $session | Out-Null
            } else {
                Write-Host "Session completed successfully, deleting it." -ForegroundColor Green
                $f = Remove-CopilotSession -SessionId $sessionId -Client $client
                write-Host "Delete session id $sessionId result: $f" -ForegroundColor Green
            }
        }
    }
    finally {
        Stop-CopilotClient $client | Out-Null
    }
    $exitCode = 0
}catch {
    Write-Host "Error during Copilot execution: $_" -ForegroundColor Red
    $exitCode = 1
}

#endregion

#region Save Run Details
$endTime = Get-Date
$duration = ($endTime - $startTime).TotalSeconds

$success = if ($success -ne $null) { $success } elseif ($promptSuccessYesNoQuestion -ne "") { $false } else { $exitCode -eq 0 }

$runDetails = @{
    timestamp = $startTime.ToString("o")
    promptName = $promptName
    promptFile = $PromptFile
    prependPrompt = $PrependPrompt
    defaultAgent = if ($DefaultAgent) { $DefaultAgent } else { $null }
    agents = $Agent
    agentFolders = $AgentFolders
    agentFiles = $AgentFile
    sessionId = if ($sessionId) { $sessionId } else { $sessionIdPlaceholder }
    name = if ($Name) { $Name } else { $null }
    model = $Model
    version = $Version
    systemMessage = $SystemMessage
    workingDirectory = $workingDirectory
    mcpConfigPaths = $mcpConfigPaths
    gitBranch = $gitBranch
    gitCommit = $gitCommit
    success = $success
    exitCode = $exitCode
    duration = [math]::Round($duration, 2)
    displayFiles = $DisplayFiles
    urlRegexp = $UrlRegexp
}

$runDetails | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runDetailsPath

if ($success) {
    Write-Host "Completed successfully in $($runDetails.duration)s" -ForegroundColor Green
} else {
    Write-Host "Failed with exit code $exitCode and success flag $success" -ForegroundColor Red
}
#endregion

Write-Output $success
exit $exitCode