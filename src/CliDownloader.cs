using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CopilotShell;

/// <summary>
/// Downloads the Copilot CLI binary from the npm registry on demand.
/// The binary version is tightly coupled to the SDK version and is embedded
/// in the assembly at build time via AssemblyMetadata("CopilotCliVersion", ...).
/// </summary>
internal static class CliDownloader
{
    private const string NpmRegistry = "https://registry.npmjs.org";

    /// <summary>
    /// Gets the required CLI version from assembly metadata (stamped at build time
    /// from the GitHub.Copilot.SDK's CopilotCliVersion MSBuild property).
    /// </summary>
    public static string? GetRequiredCliVersion()
    {
        var attr = typeof(CliDownloader).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CopilotCliVersion");
        return attr?.Value;
    }

    /// <summary>
    /// Gets the user-local cache directory for a given CLI version and RID.
    /// Windows:  %LOCALAPPDATA%\CopilotShell\cli\{version}\{rid}\
    /// macOS:    ~/Library/Application Support/CopilotShell/cli/{version}/{rid}/
    /// Linux:    ~/.local/share/CopilotShell/cli/{version}/{rid}/
    /// </summary>
    public static string GetCacheDir(string version, string rid)
    {
        string baseDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CopilotShell");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "CopilotShell");
        else
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "CopilotShell");

        return Path.Combine(baseDir, "cli", version, rid);
    }

    /// <summary>
    /// Returns the path to the cached CLI binary if it exists, or null.
    /// </summary>
    public static string? FindCached(string version, string rid)
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "copilot.exe" : "copilot";
        var path = Path.Combine(GetCacheDir(version, rid), exeName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Maps .NET RID to npm platform name used in the @github/copilot-{platform} package.
    /// </summary>
    private static string? GetNpmPlatform(string rid) => rid switch
    {
        "win-x64"    => "win32-x64",
        "win-arm64"  => "win32-arm64",
        "linux-x64"  => "linux-x64",
        "linux-arm64" => "linux-arm64",
        "osx-x64"   => "darwin-x64",
        "osx-arm64"  => "darwin-arm64",
        _ => null
    };

    /// <summary>
    /// Downloads and extracts the copilot CLI binary to the user cache.
    /// Returns the path to the binary, or null on failure.
    /// </summary>
    /// <param name="version">CLI version (e.g., "0.0.420")</param>
    /// <param name="rid">Runtime identifier (e.g., "win-x64")</param>
    /// <param name="log">Optional callback for progress messages (WriteVerbose)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task<string?> DownloadAsync(
        string version,
        string rid,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "copilot.exe" : "copilot";
        var cacheDir = GetCacheDir(version, rid);
        var binaryPath = Path.Combine(cacheDir, exeName);

        // Double-check in case another process downloaded concurrently
        if (File.Exists(binaryPath))
            return binaryPath;

        var platform = GetNpmPlatform(rid);
        if (platform is null)
        {
            log?.Invoke($"Unsupported platform for CLI download: {rid}");
            return null;
        }

        var url = $"{NpmRegistry}/@github/copilot-{platform}/-/copilot-{platform}-{version}.tgz";
        log?.Invoke($"Downloading Copilot CLI {version} for {platform} from npm...");

        Directory.CreateDirectory(cacheDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue)
                log?.Invoke($"  Downloading {contentLength.Value / 1024 / 1024} MB...");

            // Download to a temp file first to avoid partial extractions
            var tempTgz = Path.Combine(cacheDir, "copilot.tgz");
            try
            {
                await using (var fs = new FileStream(tempTgz, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }

                // Extract from the tgz — the tarball has files under package/
                await using var tgzStream = new FileStream(tempTgz, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var gzip = new GZipStream(tgzStream, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);

                while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
                {
                    // Strip "package/" prefix (npm tarball convention)
                    var name = entry.Name;
                    if (name.StartsWith("package/"))
                        name = name["package/".Length..];

                    if (name == exeName)
                    {
                        var tempBin = binaryPath + ".tmp";
                        await entry.ExtractToFileAsync(tempBin, overwrite: true, cancellationToken);
                        File.Move(tempBin, binaryPath, overwrite: true);

                        // Make executable on Unix
                        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            File.SetUnixFileMode(binaryPath,
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        }

                        log?.Invoke($"  Copilot CLI {version} installed to {cacheDir}");
                        return binaryPath;
                    }
                }

                log?.Invoke("  Binary not found in npm tarball");
                return null;
            }
            finally
            {
                // Clean up temp files
                try { File.Delete(tempTgz); } catch { }
                try { File.Delete(binaryPath + ".tmp"); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  CLI download failed: {ex.Message}");
            return null;
        }
    }
}
