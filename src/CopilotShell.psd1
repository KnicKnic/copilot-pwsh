@{
    RootModule        = 'CopilotShell.dll'
    ModuleVersion     = '0.2.1'
    GUID              = 'a3b7c9d1-4e5f-6a7b-8c9d-0e1f2a3b4c5d'
    Author            = 'CopilotShell Contributors'
    CompanyName       = 'Community'
    Copyright         = '(c) 2026. MIT License.'
    Description       = 'PowerShell 7+ module wrapping the GitHub Copilot SDK. Provides cmdlets for managing Copilot clients, sessions, and messages with full system-message customization and streaming support.'

    # Minimum PowerShell version — 7.4+ (PowerShell 7.4 LTS runs on .NET 8)
    PowerShellVersion = '7.4'
    # .NET 8+ (minimum supported by GitHub.Copilot.SDK)
    DotNetFrameworkVersion = '8.0'
    CompatiblePSEditions = @('Core')

    # StartupCheck.ps1 registers the assembly dependency resolver BEFORE the
    # binary module loads. This is critical: PowerShell calls GetTypes() on
    # CopilotShell.dll during import, which triggers resolution of SDK assemblies.
    # The resolver must be in place before that happens.
    ScriptsToProcess  = @('StartupCheck.ps1')

    # Load PowerShell functions as nested modules
    NestedModules = @('Format-CopilotEvent.ps1')

    CmdletsToExport   = @(
        # Client management
        'New-CopilotClient'
        'Start-CopilotClient'
        'Stop-CopilotClient'
        'Remove-CopilotClient'
        'Test-CopilotClient'

        # Session management
        'New-CopilotSession'
        'Get-CopilotSession'
        'Resume-CopilotSession'
        'Remove-CopilotSession'
        'Get-CopilotSessionMessages'
        'Stop-CopilotSession'
        'Disconnect-CopilotSession'

        # Messaging
        'Send-CopilotMessage'
        'Wait-CopilotSession'

        # Convenience
        'Invoke-Copilot'

        # MCP daemon management
        'Reset-CopilotMcpDaemon'
    )

    FunctionsToExport = @(
        'Format-CopilotEvent'
        'Measure-CopilotEvent'
    )
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('Copilot', 'GitHub', 'AI', 'Agent', 'LLM', 'SDK')
            ProjectUri = 'https://github.com/KnicKnic/copilot-pwsh'
            LicenseUri = 'https://opensource.org/licenses/MIT'
            BuildDate  = 'source'
        }
    }
}
