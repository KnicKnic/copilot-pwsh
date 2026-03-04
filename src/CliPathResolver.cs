using System.Runtime.InteropServices;

namespace CopilotShell;

/// <summary>
/// Resolves the path to the Copilot CLI binary.
/// Checks in order: bundled in module folder, user-local download cache.
/// </summary>
internal static class CliPathResolver
{
    /// <summary>
    /// Returns the full path to copilot(.exe), checking the module folder first,
    /// then the user-local cache. Returns null if not found anywhere.
    /// Call <see cref="ResolveOrDownloadAsync"/> to auto-download when missing.
    /// </summary>
    public static string? Resolve()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CliPathResolver).Assembly.Location);
        var rid = GetRuntimeIdentifier();
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "copilot.exe"
            : "copilot";

        // 1. Check bundled location: runtimes/<rid>/native/copilot[.exe]
        //    The bundled binary ships with the SDK and is always the correct version.
        if (assemblyDir is not null)
        {
            var bundled = Path.Combine(assemblyDir, "runtimes", rid, "native", exeName);
            if (File.Exists(bundled))
                return bundled;
        }

        // 2. Check user-local download cache (version-aware)
        var requiredVersion = CliDownloader.GetRequiredCliVersion();
        if (requiredVersion is not null)
        {
            var cached = CliDownloader.FindCached(requiredVersion, rid);
            if (cached is not null) return cached;
        }

        return null;
    }

    /// <summary>
    /// Resolves the CLI path, downloading from npm if not found locally.
    /// </summary>
    public static async Task<string?> ResolveOrDownloadAsync(
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        // Try local resolution first
        var path = Resolve();
        if (path is not null) return path;

        // Auto-download from npm
        var version = CliDownloader.GetRequiredCliVersion();
        if (version is null)
        {
            log?.Invoke("Cannot determine required Copilot CLI version — CopilotCliVersion metadata not found in assembly.");
            return null;
        }

        var rid = GetRuntimeIdentifier();
        return await CliDownloader.DownloadAsync(version, rid, log, cancellationToken);
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
