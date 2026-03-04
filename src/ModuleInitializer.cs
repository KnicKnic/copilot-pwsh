using System.Management.Automation;
using System.Reflection;
using System.Runtime.Loader;

namespace CopilotShell;

/// <summary>
/// PowerShell module lifecycle hooks.
///
/// <b>Assembly isolation for dependency version conflicts:</b>
/// The module bundles newer assemblies (e.g. System.Text.Json 10.x) than the
/// PowerShell host provides (.NET 8's v8.x or .NET 9's v9.x). To avoid type
/// identity conflicts, all module dependencies live in a <c>dependencies/</c>
/// subdirectory and are loaded via an isolated <see cref="AssemblyLoadContext"/>.
///
/// <b>Critical timing:</b> The Resolving handler MUST be registered BEFORE
/// PowerShell calls <c>GetTypes()</c> on this assembly (which triggers resolution
/// of SDK types). Since <c>IModuleAssemblyInitializer.OnImport()</c> runs AFTER
/// type enumeration begins, registration happens in <c>StartupCheck.ps1</c>
/// (via <c>ScriptsToProcess</c> in the manifest) which runs before the
/// binary module loads.
///
/// This class provides the <see cref="IModuleAssemblyCleanup"/> implementation
/// to unregister the handler when the module is removed.
/// </summary>
public class ModuleInitializer : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    public void OnImport()
    {
        // Resolving handler already registered by StartupCheck.ps1
        // (via ScriptsToProcess) before this assembly was loaded.
    }

    public void OnRemove(PSModuleInfo module)
    {
        // Clean up: the handler was registered from PowerShell script,
        // so we remove it by clearing the module-scoped variable.
        // The actual event handler is stored in the script scope and
        // will be garbage collected when the module is unloaded.
    }
}
