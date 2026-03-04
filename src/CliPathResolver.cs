using System.Runtime.InteropServices;

namespace CopilotShell;

/// <summary>
/// Resolves the path to the bundled Copilot CLI binary shipped with the
/// GitHub.Copilot.SDK NuGet package inside this module's output.
/// </summary>
internal static class CliPathResolver
{
    /// <summary>
    /// Returns the full path to the bundled copilot(.exe) binary next to this
    /// assembly, or null if not found.
    /// </summary>
    public static string? Resolve()
    {
        // The assembly lives in the module folder alongside runtimes/
        var assemblyDir = Path.GetDirectoryName(typeof(CliPathResolver).Assembly.Location);
        if (assemblyDir is null) return null;

        // Determine the RID folder name
        var rid = GetRuntimeIdentifier();

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "copilot.exe"
            : "copilot";

        // runtimes/<rid>/native/copilot[.exe]
        var candidate = Path.Combine(assemblyDir, "runtimes", rid, "native", exeName);
        if (File.Exists(candidate)) return candidate;

        // Fallback: just "copilot" on PATH
        return null;
    }

    private static string GetRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        return $"linux-{arch}";
    }
}
