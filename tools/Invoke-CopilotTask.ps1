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
    [switch]$SkipMcpConfig,

    [Parameter(Mandatory=$false)]
    [switch]$RunOnce,

    [Parameter(Mandatory=$false)]
    [string]$Version = "0",

    [Parameter(Mandatory=$false)]
    [string]$McpConfigSource = ".vscode/mcp.json",

    [Parameter(Mandatory=$false)]
    [ArgumentCompleter({
        param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
        Get-ChildItem -Path ".github/agents" -Filter "*.agent.md" -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName -replace [regex]::Escape((Get-Location).Path + '\'), '' -replace '\\', '/' } |
            Where-Object { $_ -like "*$wordToComplete*" }
    })]
    [string[]]$AgentFile = @(),

    [Parameter(Mandatory=$false)]
    [string]$Agent = "",

    [Parameter(Mandatory=$false)]
    [ValidateSet("claude-sonnet-4.5","claude-sonnet-4.6", "claude-haiku-4.5", "claude-opus-4.5", "claude-opus-4.6", "claude-sonnet-4", 
                 "gemini-3-pro-preview", "gpt-5.2-codex", "gpt-5.2", "gpt-5.1-codex-max", 
                 "gpt-5.1-codex", "gpt-5.1", "gpt-5", "gpt-5.1-codex-mini", "gpt-5-mini", "gpt-4.1")]
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

#region MCP Config Generation
$workspaceRoot = (Get-Location).Path
$resolvedMcpConfigSource = if ([System.IO.Path]::IsPathRooted($McpConfigSource)) { $McpConfigSource } else { Join-Path $workspaceRoot $McpConfigSource }
if (-not $SkipMcpConfig) {
    $mcpConfigDest = Join-Path $workspaceRoot "mcp-config.json"
    
    if (Test-Path $resolvedMcpConfigSource) {
        try {
            $sourceConfig = Get-Content $resolvedMcpConfigSource -Raw | ConvertFrom-Json
            
            # Get source file hash first
            $sourceFileHash = (Get-FileHash -Path $resolvedMcpConfigSource -Algorithm SHA256).Hash
            
            # Check if we need to update by reading the existing file's metadata
            $needsUpdate = $true
            if (Test-Path $mcpConfigDest) {
                try {
                    $existingConfig = Get-Content $mcpConfigDest -Raw | ConvertFrom-Json
                    if ($existingConfig._sourceHash -eq $sourceFileHash) {
                        $needsUpdate = $false
                    }
                }
                catch {
                    # If we can't read/parse existing file, regenerate it
                    $needsUpdate = $true
                }
            }
            
            if ($needsUpdate) {
                # Convert from .vscode/mcp.json format (servers) to copilot cli format (mcpServers)
                $cliConfig = [ordered]@{
                    _sourceHash = $sourceFileHash
                    mcpServers = @{}
                }
                
                $servers = if ($sourceConfig.servers) { $sourceConfig.servers } else { $sourceConfig }
                
                foreach ($prop in $servers.PSObject.Properties) {
                    $serverName = $prop.Name
                    $serverConfig = $prop.Value
                    
                    # Skip disabled servers
                    if ($serverConfig.disabled -eq $true) { continue }
                    
                    # Add tools: ["*"] to enable all tools
                    $serverConfig | Add-Member -NotePropertyName "tools" -NotePropertyValue @("*") -Force
                    
                    $cliConfig.mcpServers[$serverName] = $serverConfig
                }
                
                # Generate config with source hash embedded
                $newConfigJson = $cliConfig | ConvertTo-Json -Depth 10
                
                # Write to temp file and atomically swap
                $tempFile = "$mcpConfigDest.tmp.$PID"
                try {
                    $newConfigJson | Set-Content -Path $tempFile -NoNewline
                    Move-Item -Path $tempFile -Destination $mcpConfigDest -Force
                    Write-Host "Generated $mcpConfigDest from $resolvedMcpConfigSource" -ForegroundColor Cyan
                }
                catch {
                    # Clean up temp file if move failed
                    if (Test-Path $tempFile) {
                        Remove-Item -Path $tempFile -Force -ErrorAction SilentlyContinue
                    }
                    throw
                }
            }
        }
        catch {
            throw "Failed to convert MCP config: $_"
        }
    }
    elseif (-not (Test-Path $mcpConfigDest)) {
        # Source doesn't exist and mcp-config.json doesn't exist - warn user
        Write-Host "Warning: MCP config source '$resolvedMcpConfigSource' not found and no existing $mcpConfigDest" -ForegroundColor Yellow
    }
    # else: source doesn't exist but mcp-config.json does - silently continue
}
#endregion

#region Parse Prompt File
# Initialize variables
$promptName = ""
$prompt = ""
$agentArgs = @()
$tools = $null

if ($PromptFile) {
    # Read and parse the prompt file
    $promptContent = Get-Content -Path $PromptFile -Raw

    # Extract prompt name from file path (e.g., "dri" from ".github/prompts/dri.prompt.md")
    $promptName = [System.IO.Path]::GetFileName($PromptFile) -replace '\.prompt\.md$', ''

    # Extract frontmatter (between --- markers)
    if ($promptContent -match '(?s)^---\s*\n(.*?)\n---\s*\n(.*)$') {
        $frontmatter = $matches[1]
        $prompt = $matches[2].Trim()
        
        # Parse agent from frontmatter (unless overridden by parameter)
        if (-not $Agent -and $frontmatter -match "agent:\s*'([^']+)'") {
            $agentName = $matches[1]
            $agentArgs = @("-agent", $agentName)
        }
    } else {
        # No frontmatter, use entire content as prompt
        $prompt = $promptContent.Trim()
    }
}

# Use Agent parameter if provided (overrides frontmatter)
if ($Agent) {
    $agentArgs = @("-agent", $Agent)
}
# Prepend string to prompt if provided (or use as entire prompt if no PromptFile)
if ($PrependPrompt) {
    if ($prompt) {
        $prompt = "$PrependPrompt`n" + $prompt
    } else {
        $prompt = $PrependPrompt
    }
}
# Resolve agent files: from -AgentFiles parameter + frontmatter name-based lookup
$resolvedAgentFiles = @()
foreach ($af in $AgentFile) {
    if (Test-Path $af) {
        $resolvedAgentFiles += (Get-Item $af).FullName
    } else {
        Write-Host "Warning: Agent file '$af' not found" -ForegroundColor Yellow
    }
}
# From agent name: always add the convention-based agent file if it exists and isn't already resolved
if ($agentArgs.Count -gt 1) {
    $agentFileTest = ".github/agents/$($agentArgs[1]).agent.md"
    if (Test-Path $agentFileTest) {
        $resolvedPath = (Get-Item $agentFileTest).FullName
        if ($resolvedAgentFiles -notcontains $resolvedPath) {
            $resolvedAgentFiles += $resolvedPath
        }
    } else {
        Write-Host "Warning: Agent file '$agentFileTest' not found for agent '$($agentArgs[1])'" -ForegroundColor Yellow
    }
}

# Validate that agent selection has a corresponding agent file
if ($agentArgs.Count -gt 1 -and $resolvedAgentFiles.Count -eq 0) {
    throw "Agent '$($agentArgs[1])' was specified but no agent file could be resolved. Provide -AgentFile or ensure .github/agents/$($agentArgs[1]).agent.md exists."
}

# Validate we have a prompt
if (-not $prompt) {
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
# Save prompt to run_details folder
$prompt | Set-Content -LiteralPath "$runDetailsDir/prompt.txt"

# Copy mcp-config.json to run_details folder if it exists
if (Test-Path $mcpConfigDest) {
    Copy-Item -Path $mcpConfigDest -Destination "$runDetailsDir/mcp-config.json" -Force
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
    agent = if ($agentArgs.Count -gt 1) { $agentArgs[1] } else { $null }
    agentFiles = $resolvedAgentFiles
    sessionId = $sessionIdPlaceholder
    name = if ($Name) { $Name } else { $null }
    model = $Model
    version = $Version
    systemMessage = $SystemMessage
    workingDirectory = $workingDirectory
    mcpConfigPath = $mcpConfigDest
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
        $customAgentsArg = if ($resolvedAgentFiles.Count -gt 0) { @{"-CustomAgentFile" = $resolvedAgentFiles} } else { @{} }
        $agentName = if ($agentArgs.Count -gt 1) { $agentArgs[1] } else { $null }
        try {
            $session = New-CopilotSession $client `
                -SystemMessage $SystemMessage `
                -SystemMessageMode Replace `
                -McpConfigFile $mcpConfigDest `
                -InfiniteSessions -Model $Model -stream @customAgentsArg
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
    $agentArg = if ($agentArgs.Count -gt 1) { @{"-Agent" = $agentArgs[1]} } else { @{} }
    try {
        Send-CopilotMessage $session @agentArg -prompt $prompt -timeout $(30*60) -stream | Format-CopilotEvent -LogFile "$runDetailsDir/pwsh_capture.md" | Out-Null
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
                if($yesNo.trim() -ilike "yes"){
                    $success = $true
                } else {
                    $success = $false
                    Write-Host "Prompt success indicator $promptSuccessYesNoQuestion returned '$yesNo' and I interpreted it as failure." -ForegroundColor Yellow
                }
            }
            else {
                Write-Host "No prompt success indicator specified, skipping success check." -ForegroundColor Yellow
            }
        }
        finally {
            Disconnect-CopilotSession $session | Out-Null
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
    agent = if ($agentArgs.Count -gt 1) { $agentArgs[1] } else { $null }
    agentFiles = $resolvedAgentFiles
    sessionId = if ($sessionId) { $sessionId } else { $sessionIdPlaceholder }
    name = if ($Name) { $Name } else { $null }
    model = $Model
    version = $Version
    systemMessage = $SystemMessage
    workingDirectory = $workingDirectory
    mcpConfigPath = $mcpConfigDest
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
