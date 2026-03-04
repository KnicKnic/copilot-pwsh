<#
.SYNOPSIS
    Pre-loads the assembly dependency resolver before CopilotShell.dll loads.

.DESCRIPTION
    This script runs BEFORE the binary module (CopilotShell.dll) is loaded by
    PowerShell, via the ScriptsToProcess manifest entry.

    It registers a Resolving event handler on the Default AssemblyLoadContext
    that redirects dependency resolution to the dependencies/ subdirectory
    and pre-loads all dependencies into an isolated ALC.

    This ensures:
    1. The handler is in place BEFORE PowerShell calls GetTypes() on
       CopilotShell.dll (which triggers resolution of SDK types).
    2. All dependencies share a single isolated ALC with consistent type
       identity — no cross-ALC type conflicts.
    3. On .NET 9 (pwsh 7.5), System.Text.Json v10 is loaded from the module
       instead of falling back to the runtime's v9 — preventing the
       StreamJsonRpc MissingMethodException.
#>

$depsDir = [System.IO.Path]::Combine($PSScriptRoot, 'dependencies')

if (-not [System.IO.Directory]::Exists($depsDir)) {
    # Fallback: dependencies might be in the module root (dev/debug layout)
    return
}

# Create the isolated AssemblyLoadContext for module dependencies.
# All dependencies are loaded here to ensure consistent type identity.
$script:CopilotShellALC = [System.Runtime.Loader.AssemblyLoadContext]::new('CopilotShell', $false)

# Pre-load ALL dependency DLLs into the isolated ALC.
# This ensures that when the SDK and its transitive dependencies need assemblies,
# they find them already in the CopilotShell ALC — preventing fallback to the
# Default ALC which may have older/incompatible versions (e.g. STJ v9 on .NET 9).
foreach ($dll in [System.IO.Directory]::GetFiles($depsDir, '*.dll')) {
    try {
        $script:CopilotShellALC.LoadFromAssemblyPath($dll) | Out-Null
    } catch {
        # Skip DLLs that can't be loaded (e.g. satellite resource assemblies
        # with missing dependencies — they'll be loaded on demand later)
    }
}

# Register the Resolving handler on Default ALC.
# This handler fires when the Default ALC can't resolve a dependency referenced
# by CopilotShell.dll (e.g. GitHub.Copilot.SDK, System.Text.Json v10 on .NET 9).
# It returns the pre-loaded assembly from our isolated ALC.
$script:CopilotShellDepsDir = $depsDir
$script:CopilotShellResolvingHandler = [Func[System.Runtime.Loader.AssemblyLoadContext, System.Reflection.AssemblyName, System.Reflection.Assembly]]{
    param(
        [System.Runtime.Loader.AssemblyLoadContext]$context,
        [System.Reflection.AssemblyName]$assemblyName
    )
    # First check if already loaded in our ALC
    foreach ($loaded in $script:CopilotShellALC.Assemblies) {
        if ($loaded.GetName().Name -eq $assemblyName.Name) {
            return $loaded
        }
    }
    # Try loading from dependencies directory
    $candidatePath = [System.IO.Path]::Combine($script:CopilotShellDepsDir, "$($assemblyName.Name).dll")
    if ([System.IO.File]::Exists($candidatePath)) {
        return $script:CopilotShellALC.LoadFromAssemblyPath($candidatePath)
    }
    return $null
}

[System.Runtime.Loader.AssemblyLoadContext]::Default.add_Resolving($script:CopilotShellResolvingHandler)
