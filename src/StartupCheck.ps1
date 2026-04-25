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

    IMPORTANT: References are stored in a global hashtable (not $script: scope)
    because $script: variables are destroyed when a child scope exits (e.g.
    `exit` inside a & call). The .NET Resolving event keeps the delegate alive,
    but its closure over $script: vars would capture nulls after scope teardown.
#>

$depsDir = [System.IO.Path]::Combine($PSScriptRoot, 'dependencies')

if (-not [System.IO.Directory]::Exists($depsDir)) {
    # Fallback: dependencies might be in the module root (dev/debug layout)
    return
}

# Guard: skip if already initialized (e.g. Import-Module -Force reimport).
# The ALC and resolving handler from the first import are still valid and
# non-collectible ALCs cannot be unloaded, so re-running would just leak.
if ($global:__CopilotShellState -ne $null) {
    return
}

# Create the isolated AssemblyLoadContext for module dependencies.
# All dependencies are loaded here to ensure consistent type identity.
$alc = [System.Runtime.Loader.AssemblyLoadContext]::new('CopilotShell', $false)

# Pre-load ALL dependency DLLs into the isolated ALC.
# This ensures that when the SDK and its transitive dependencies need assemblies,
# they find them already in the CopilotShell ALC — preventing fallback to the
# Default ALC which may have older/incompatible versions (e.g. STJ v9 on .NET 9).
foreach ($dll in [System.IO.Directory]::GetFiles($depsDir, '*.dll')) {
    try {
        $alc.LoadFromAssemblyPath($dll) | Out-Null
    } catch {
        # Skip DLLs that can't be loaded (e.g. satellite resource assemblies
        # with missing dependencies — they'll be loaded on demand later)
    }
}

# Store references in a global hashtable that survives scope teardown.
# $script: vars are destroyed when a child scope calls `exit`, but $global:
# persists for the process lifetime — matching the non-collectible ALC.
$global:__CopilotShellState = @{
    ALC = $alc
    DepsDir = $depsDir
}

# Register the Resolving handler on Default ALC.
# This handler fires when the Default ALC can't resolve a dependency referenced
# by CopilotShell.dll (e.g. GitHub.Copilot.SDK, System.Text.Json v10 on .NET 9).
# It returns the pre-loaded assembly from our isolated ALC.
#
# The closure captures $global:__CopilotShellState which survives scope teardown.
$handler = [Func[System.Runtime.Loader.AssemblyLoadContext, System.Reflection.AssemblyName, System.Reflection.Assembly]]{
    param(
        [System.Runtime.Loader.AssemblyLoadContext]$context,
        [System.Reflection.AssemblyName]$assemblyName
    )
    $state = $global:__CopilotShellState
    if ($null -eq $state) { return $null }
    $moduleAlc = $state.ALC
    $moduleDepsDir = $state.DepsDir

    # First check if already loaded in our ALC
    foreach ($loaded in $moduleAlc.Assemblies) {
        if ($loaded.GetName().Name -eq $assemblyName.Name) {
            return $loaded
        }
    }
    # Try loading from dependencies directory
    $candidatePath = [System.IO.Path]::Combine($moduleDepsDir, "$($assemblyName.Name).dll")
    if ([System.IO.File]::Exists($candidatePath)) {
        return $moduleAlc.LoadFromAssemblyPath($candidatePath)
    }
    return $null
}

[System.Runtime.Loader.AssemblyLoadContext]::Default.add_Resolving($handler)
$global:__CopilotShellState.Handler = $handler
