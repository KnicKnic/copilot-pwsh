function Measure-CopilotEvent {
    <#
    .SYNOPSIS
        Summarizes Copilot session events — counts, tokens, cost, tool results.

    .DESCRIPTION
        Accepts Copilot session events from the pipeline (via Send-CopilotMessage -Stream
        or Get-CopilotSessionMessages) and produces an aggregate summary object.

    .PARAMETER InputObject
        Copilot session event objects from the pipeline.

    .EXAMPLE
        # Summarize a streaming session
        Send-CopilotMessage $session "hello" -Stream | Measure-CopilotEvent

    .EXAMPLE
        # Combine with Format-CopilotEvent using -PassThru
        Send-CopilotMessage $session "hello" -Stream | Format-CopilotEvent -PassThru | Measure-CopilotEvent

    .EXAMPLE
        # Summarize history
        Get-CopilotSessionMessages $session | Measure-CopilotEvent
    #>
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline = $true)]
        $InputObject
    )

    begin {
        $summary = [ordered]@{
            Events           = 0
            UserMessages     = 0
            AssistantMessages = 0
            ToolCalls        = 0
            ToolSuccesses    = 0
            ToolFailures     = 0
            ToolErrors       = [System.Collections.Generic.List[object]]::new()
            ToolNames        = [System.Collections.Generic.List[string]]::new()
            InputTokens      = 0
            OutputTokens     = 0
            CacheReadTokens  = 0
            CacheWriteTokens = 0
            TotalCost        = [double]0
            Models           = [System.Collections.Generic.List[string]]::new()
            SessionTokens    = $null
            SessionTokenLimit = $null
            SessionMessages  = $null
            Duration         = [double]0
        }
    }

    process {
        if ($null -eq $InputObject) { return }
        $summary.Events++
        $type = $InputObject.GetType().Name

        switch -Wildcard ($type) {
            "*UserMessage*" {
                $summary.UserMessages++
            }
            "*AssistantMessage*" {
                if ($type -notmatch 'Delta') {
                    $summary.AssistantMessages++
                }
            }
            "*ToolExecutionStart*" {
                $summary.ToolCalls++
                $name = if ($InputObject.Data.McpServerName) {
                    "$($InputObject.Data.McpServerName)/$($InputObject.Data.McpToolName)"
                } else {
                    $InputObject.Data.ToolName
                }
                if ($name -and -not $summary.ToolNames.Contains($name)) {
                    $summary.ToolNames.Add($name)
                }
            }
            "*ToolExecutionComplete*" {
                if ($InputObject.Data.Success) {
                    $summary.ToolSuccesses++
                } else {
                    $summary.ToolFailures++
                    if ($InputObject.Data.Error) {
                        $summary.ToolErrors.Add([PSCustomObject]@{
                            ToolCallId = $InputObject.Data.ToolCallId
                            Code       = $InputObject.Data.Error.Code
                            Message    = $InputObject.Data.Error.Message
                        })
                    }
                }
            }
            "*AssistantUsage*" {
                $d = $InputObject.Data
                if ($d.InputTokens)      { $summary.InputTokens      += $d.InputTokens }
                if ($d.OutputTokens)     { $summary.OutputTokens     += $d.OutputTokens }
                if ($d.CacheReadTokens)  { $summary.CacheReadTokens  += $d.CacheReadTokens }
                if ($d.CacheWriteTokens) { $summary.CacheWriteTokens += $d.CacheWriteTokens }
                if ($d.Cost)             { $summary.TotalCost        += $d.Cost }
                if ($d.Duration)         { $summary.Duration         += $d.Duration }
                if ($d.Model -and -not $summary.Models.Contains($d.Model)) {
                    $summary.Models.Add($d.Model)
                }
            }
            "*SessionUsageInfo*" {
                $d = $InputObject.Data
                $summary.SessionTokens    = $d.CurrentTokens
                $summary.SessionTokenLimit = $d.TokenLimit
                $summary.SessionMessages  = $d.MessagesLength
            }
        }
    }

    end {
        # Convert lists to arrays for clean output
        $summary.ToolNames  = @($summary.ToolNames)
        $summary.ToolErrors = @($summary.ToolErrors)
        $summary.Models     = @($summary.Models)

        [PSCustomObject]$summary
    }
}

function Format-CopilotEvent {
    <#
    .SYNOPSIS
        Formats and logs Copilot session events with colors to console and optionally to file.
    
    .DESCRIPTION
        A filter function that processes Copilot session events in the pipeline,
        displaying them with colors in the console and optionally saving plain
        text to a log file.
    
    .PARAMETER LogFile
        Optional path to save the log as plain text (no color codes).
    
    .PARAMETER PassThru
        If specified, passes the original event objects through the pipeline
        for further processing.
    
    .PARAMETER Append
        If specified with -LogFile, appends to an existing log file instead of
        overwriting it.
    
    .EXAMPLE
        # Console output only
        Send-CopilotMessage $session -Prompt "test" -Stream | Format-CopilotEvent
    
    .EXAMPLE
        # Console + log file (overwrites existing)
        Send-CopilotMessage $session -Prompt "test" -Stream | Format-CopilotEvent -LogFile "session.log"
    
    .EXAMPLE
        # Append to existing log file
        Send-CopilotMessage $session -Prompt "test" -Stream | Format-CopilotEvent -LogFile "session.log" -Append
    
    .EXAMPLE
        # Save to file and capture events
        $events = Send-CopilotMessage $session -Prompt "test" -Stream | Format-CopilotEvent -LogFile "log.txt" -PassThru
    #>
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline = $true)]
        $InputObject,
        
        [string]$LogFile,
        
        [switch]$PassThru,
        
        [switch]$Append
    )
    
    begin {
        # Track toolCallId -> display name for correlating COMPLETE with START
        $toolNames = @{}

        function Write-ColorLog {
            param(
                [string]$Message,
                [ConsoleColor]$ForegroundColor = 'White',
                [switch]$NoNewline
            )
            
            # Write to console with color
            Write-Host $Message -ForegroundColor $ForegroundColor -NoNewline:$NoNewline
            
            # Write to log file as plain text if LogFile is specified
            if ($LogFile) {
                if ($NoNewline) {
                    [System.IO.File]::AppendAllText($LogFile, $Message)
                } else {
                    [System.IO.File]::AppendAllText($LogFile, $Message + "`n")
                }
            }
        }
        
        # Initialize log file if specified
        if ($LogFile) {
            # Ensure parent directory exists
            $logDir = Split-Path $LogFile -Parent
            if ($logDir -and -not (Test-Path $logDir)) {
                New-Item -ItemType Directory -Path $logDir -Force | Out-Null
            }
            if (-not $Append -and (Test-Path $LogFile)) {
                Remove-Item $LogFile -Force
            }
        }
    }
    
    process {
        if ($null -eq $InputObject) { return }
        
        $type = $InputObject.GetType().Name
        
        switch -Wildcard ($type) {
            "*UserMessage*" {
                Write-ColorLog "`n[📝 USER MESSAGE]" -ForegroundColor Blue
            }
            "*AssistantMessageDelta*" {
                # Stream assistant text as it arrives
                Write-ColorLog $InputObject.Data.Content -ForegroundColor Green -NoNewline
            }
            "*AssistantMessage*" {
                # Final complete message
                if ($InputObject.Data.Content) {
                    Write-ColorLog "`n[✓ ASSISTANT COMPLETE]" -ForegroundColor DarkGreen
                    Write-ColorLog $InputObject.Data.Content -ForegroundColor White
                }
            }
            "*ToolExecutionStart*" {
                $toolName = $InputObject.Data.ToolName
                $mcpServer = $InputObject.Data.McpServerName
                $mcpTool = $InputObject.Data.McpToolName
                $toolCallId = $InputObject.Data.ToolCallId

                $displayName = if ($mcpServer) { "$mcpServer/$mcpTool" } else { $toolName }
                if ($toolCallId) { $toolNames[$toolCallId] = $displayName }

                Write-ColorLog "`n[🔧 TOOL START] " -ForegroundColor Yellow -NoNewline
                Write-ColorLog $displayName -ForegroundColor Cyan
                if ($toolCallId) {
                    Write-ColorLog "  ID: $toolCallId" -ForegroundColor DarkGray
                }

                if ($InputObject.Data.Arguments) {
                    $argsRaw = $InputObject.Data.Arguments
                    $argsJson = if ($argsRaw -is [System.Text.Json.JsonElement]) {
                        $argsRaw.GetRawText()
                    } else {
                        $argsRaw | ConvertTo-Json -Compress -Depth 3
                    }
                    if ($argsJson.Length -gt 300) {
                        Write-ColorLog "  Args: $($argsJson.Substring(0, 300))..." -ForegroundColor DarkGray
                    } else {
                        Write-ColorLog "  Args: $argsJson" -ForegroundColor DarkGray
                    }
                }
            }
            "*ToolExecutionComplete*" {
                $success = $InputObject.Data.Success
                $icon = if ($success) { "✓" } else { "✗" }
                $color = if ($success) { "Green" } else { "Red" }
                $toolCallId = $InputObject.Data.ToolCallId
                $resolvedName = if ($toolCallId -and $toolNames.ContainsKey($toolCallId)) { $toolNames[$toolCallId] } else { $null }

                Write-ColorLog "`n[$icon TOOL COMPLETE" -ForegroundColor $color -NoNewline
                if ($resolvedName) {
                    Write-ColorLog " $resolvedName" -ForegroundColor Cyan -NoNewline
                }
                if ($toolCallId) {
                    Write-ColorLog " ($toolCallId)" -ForegroundColor DarkGray -NoNewline
                }
                Write-ColorLog "] " -ForegroundColor $color -NoNewline
                
                if ($InputObject.Data.Result) {
                    $content = $InputObject.Data.Result.Content
                    $detailed = $InputObject.Data.Result.DetailedContent
                    
                    if ($content) {
                        if ($content.Length -gt 500) {
                            Write-ColorLog "$($content.Substring(0, 500))..." -ForegroundColor Gray
                        } else {
                            Write-ColorLog $content -ForegroundColor Gray
                        }
                    }
                    
                    if ($detailed -and $detailed -ne $content) {
                        if ($detailed.Length -gt 1000) {
                            Write-ColorLog "  Detail: $($detailed.Substring(0, 1000))..." -ForegroundColor DarkGray
                        } else {
                            Write-ColorLog "  Detail: $detailed" -ForegroundColor DarkGray
                        }
                    }
                }
                
                if ($InputObject.Data.Error) {
                    $errMsg = $InputObject.Data.Error.Message
                    $errCode = $InputObject.Data.Error.Code
                    if ($errCode) {
                        Write-ColorLog "  ERROR [$errCode]: $errMsg" -ForegroundColor Red
                    } else {
                        Write-ColorLog "  ERROR: $errMsg" -ForegroundColor Red
                    }
                }
            }
            "*AssistantReasoning*" {
                if ($InputObject.Data.Content) {
                    Write-ColorLog "`n[💭 REASONING] " -ForegroundColor Magenta -NoNewline
                    Write-ColorLog $InputObject.Data.Content -ForegroundColor DarkGray
                }
            }
            "*SessionIdle*" {
                Write-ColorLog "`n[✓ SESSION IDLE]" -ForegroundColor DarkGreen
            }
            "*SessionError*" {
                Write-ColorLog "`n[⚠ ERROR] $($InputObject.Data.Message)" -ForegroundColor Red
            }
            "*AssistantUsage*" {
                $d = $InputObject.Data
                $parts = @()
                if ($d.Model)        { $parts += $d.Model }
                if ($d.InputTokens)  { $parts += "in:$($d.InputTokens)" }
                if ($d.OutputTokens) { $parts += "out:$($d.OutputTokens)" }
                if ($d.CacheReadTokens)  { $parts += "cache-read:$($d.CacheReadTokens)" }
                if ($d.CacheWriteTokens) { $parts += "cache-write:$($d.CacheWriteTokens)" }
                if ($d.Cost)     { $parts += "cost:$($d.Cost)" }
                if ($d.Duration) { $parts += "$($d.Duration)ms" }
                Write-ColorLog "[📊 USAGE] $($parts -join ' | ')" -ForegroundColor DarkCyan
            }
            "*SessionUsageInfo*" {
                $d = $InputObject.Data
                Write-ColorLog "[📊 SESSION] Tokens: $($d.CurrentTokens)/$($d.TokenLimit) | Messages: $($d.MessagesLength)" -ForegroundColor DarkCyan
            }
            "*PendingMessages*" {
                # Suppress these verbose events
            }
            "*TurnStart*" {
                # Suppress these verbose events
            }
            "*TurnEnd*" {
                # Suppress these verbose events
            }
            default {
                # Uncomment to see all event types:
                # Write-ColorLog "[DEBUG: $type]" -ForegroundColor DarkGray
            }
        }
        
        # Pass through the original object if requested
        if ($PassThru) {
            Write-Output $InputObject
        }
    }
    
    end {
    }
}
