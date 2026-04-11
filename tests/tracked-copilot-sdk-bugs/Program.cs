// ============================================================================
// Tracked Copilot SDK Bugs — Test Runner
// ============================================================================
//
// Each bug is a separate class implementing IBugRepro in its own file.
// Run failing: dotnet run                    (default — known bugs only)
// Run all:     dotnet run -- --all
// Run passing: dotnet run -- --passing
// Run one:     dotnet run -- AgentToolScoping
// List bugs:   dotnet run -- --list
// Download:    dotnet run -- --download
//
// The runner checks for the correct Copilot CLI version (matching the SDK)
// in the current working directory. Use --download to fetch it from npm.
// ============================================================================

using GitHub.Copilot.SDK;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// --- Resolve SDK and CLI versions ---
var sdkAssembly = typeof(CopilotClient).Assembly;
var sdkVersion = sdkAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? sdkAssembly.GetName().Version?.ToString() ?? "unknown";
// CopilotCliVersion is a MSBuild property from the SDK NuGet package, stamped into our assembly
var requiredCliVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "CopilotCliVersion")?.Value;

Console.WriteLine($"SDK version:          {sdkVersion}");
Console.WriteLine($"Required CLI version: {requiredCliVersion ?? "unknown"}");
Console.WriteLine();

if (requiredCliVersion is null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("ERROR: Cannot determine required CLI version from SDK assembly metadata.");
    Console.ResetColor();
    return 2;
}

// --- Check / download CLI in current working directory ---
var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
var cliPath = Path.Combine(Directory.GetCurrentDirectory(), exeName);
var wantDownload = args.Contains("--download");

if (wantDownload)
{
    if (File.Exists(cliPath))
    {
        var existingVersion = GetCliVersion(cliPath);
        if (existingVersion == requiredCliVersion)
        {
            Console.WriteLine($"CLI already present and correct: {cliPath} (v{existingVersion})");
        }
        else
        {
            Console.WriteLine($"CLI version mismatch: found v{existingVersion ?? "unknown"}, need v{requiredCliVersion}");
            Console.WriteLine("Replacing...");
            File.Delete(cliPath);
            Console.WriteLine($"Downloading Copilot CLI v{requiredCliVersion}...");
            if (!await DownloadCliAsync(requiredCliVersion, cliPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: Failed to download Copilot CLI.");
                Console.ResetColor();
                return 2;
            }
        }
    }
    else
    {
        Console.WriteLine($"Downloading Copilot CLI v{requiredCliVersion}...");
        if (!await DownloadCliAsync(requiredCliVersion, cliPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Failed to download Copilot CLI.");
            Console.ResetColor();
            return 2;
        }
    }
}

if (!File.Exists(cliPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ERROR: CLI not found at {cliPath}");
    Console.WriteLine($"Run with --download to fetch Copilot CLI v{requiredCliVersion}:");
    Console.WriteLine($"  dotnet run --project tests/tracked-copilot-sdk-bugs -- --download");
    Console.ResetColor();
    return 2;
}

// Verify the actual binary version (Windows file metadata first, then CLI banner fallback)
var verifiedVersion = GetCliVersion(cliPath);
var reportedVersion = GetCliReportedVersion(cliPath);
Console.WriteLine($"CLI version verified: {verifiedVersion}");
if (!string.IsNullOrWhiteSpace(reportedVersion) && !string.Equals(reportedVersion, verifiedVersion, StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"CLI --version says:   {reportedVersion} (banner output differs from file metadata)");
}
Console.WriteLine($"CLI path:             {cliPath}");
Console.WriteLine();

// --- Discover and run repros ---
var reproTypes = Assembly.GetExecutingAssembly()
    .GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && typeof(IBugRepro).IsAssignableFrom(t))
    .OrderBy(t => t.Name)
    .ToList();

if (args.Contains("--list"))
{
    Console.WriteLine("Available bug repros:");
    foreach (var t in reproTypes)
    {
        var instance = (IBugRepro)Activator.CreateInstance(t)!;
        var tag = instance.ExpectsFail ? "FAIL" : "PASS";
        Console.WriteLine($"  [{tag}] {t.Name,-30} {instance.Description}");
    }
    return 0;
}

// Filter by category and/or name
var runAll = args.Contains("--all");
var runPassing = args.Contains("--passing");
var selectedNames = args.Where(a => !a.StartsWith('-')).ToList();

var repros = reproTypes;

// Name filter takes precedence over category flags
if (selectedNames.Count > 0)
{
    repros = repros
        .Where(t => selectedNames.Any(n => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
        .ToList();
}
else if (!runAll)
{
    // Default: failing only. --passing: passing only.
    var wantFail = !runPassing;
    repros = repros
        .Where(t => ((IBugRepro)Activator.CreateInstance(t)!).ExpectsFail == wantFail)
        .ToList();
}

if (repros.Count == 0)
{
    Console.WriteLine($"No matching bug repros found for: {string.Join(", ", selectedNames)}");
    Console.WriteLine("Use --list to see available repros.");
    return 2;
}

Console.WriteLine($"Running {repros.Count} bug repro(s)...\n");

int failures = 0;
foreach (var reproType in repros)
{
    var repro = (IBugRepro)Activator.CreateInstance(reproType)!;
    var header = $"=== {reproType.Name} ===";
    Console.WriteLine(header.PadLeft((60 + header.Length) / 2).PadRight(60));
    Console.WriteLine($"  {repro.Description}");
    Console.WriteLine();

    try
    {
        var result = await repro.RunAsync(cliPath);
        var failed = result != 0;
        var asExpected = failed == repro.ExpectsFail;

        if (failed)
        {
            Console.ForegroundColor = asExpected ? ConsoleColor.Green : ConsoleColor.Red;
            var tag = asExpected ? "BUG-OK" : "BUG";
            Console.WriteLine($"[{tag}]  {reproType.Name} — bug still present (exit {result})\n");
            if (!asExpected) failures++;
        }
        else
        {
            Console.ForegroundColor = asExpected ? ConsoleColor.Green : ConsoleColor.Yellow;
            var tag = asExpected ? "PASS" : "FIXED?";
            Console.WriteLine($"[{tag}] {reproType.Name} — bug appears fixed\n");
            if (!asExpected) failures++;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {reproType.Name} — {ex.Message}\n");
        failures++;
    }
    finally
    {
        Console.ResetColor();
    }
}

Console.WriteLine($"\n{repros.Count - failures}/{repros.Count} passed, {failures} bug(s) still present.");
return failures > 0 ? 1 : 0;

// ============================================================================
// Helpers
// ============================================================================

static string? GetCliVersion(string path)
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                return info.ProductVersion;
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
                return info.FileVersion;
        }
        catch
        {
            // Fall back to the CLI banner below.
        }
    }

    return GetCliReportedVersion(path);
}

static string? GetCliReportedVersion(string path)
{
    try
    {
        var psi = new ProcessStartInfo(path, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        var stdout = proc?.StandardOutput.ReadToEnd();
        var stderr = proc?.StandardError.ReadToEnd();
        proc?.WaitForExit(5000);

        var combined = string.Join(Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

        if (string.IsNullOrWhiteSpace(combined))
            return null;

        var match = Regex.Match(combined, @"\b\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\b");
        return match.Success ? match.Value.TrimEnd('.') : combined;
    }
    catch
    {
        return null;
    }
}

static async Task<bool> DownloadCliAsync(string version, string targetPath)
{
    var arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => "x64"
    };
    var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"win32-{arch}"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"darwin-{arch}"
        : $"linux-{arch}";

    var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
    var url = $"https://registry.npmjs.org/@github/copilot-{platform}/-/copilot-{platform}-{version}.tgz";

    Console.WriteLine($"  Source: {url}");

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    try
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var size = response.Content.Headers.ContentLength;
        if (size.HasValue)
            Console.WriteLine($"  Size: {size.Value / 1024 / 1024} MB");

        var tempTgz = targetPath + ".tgz";
        try
        {
            await using (var fs = new FileStream(tempTgz, FileMode.Create))
                await response.Content.CopyToAsync(fs);

            await using var tgzStream = new FileStream(tempTgz, FileMode.Open, FileAccess.Read);
            await using var gzip = new GZipStream(tgzStream, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);

            while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
            {
                var name = entry.Name;
                if (name.StartsWith("package/"))
                    name = name["package/".Length..];

                if (name == exeName)
                {
                    var tempBin = targetPath + ".tmp";
                    await entry.ExtractToFileAsync(tempBin, overwrite: true);
                    File.Move(tempBin, targetPath, overwrite: true);

                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        File.SetUnixFileMode(targetPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }

                    Console.WriteLine($"  Downloaded to {targetPath}");
                    return true;
                }
            }

            Console.WriteLine("  ERROR: Binary not found in tarball");
            return false;
        }
        finally
        {
            try { File.Delete(tempTgz); } catch { }
            try { File.Delete(targetPath + ".tmp"); } catch { }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.Message}");
        return false;
    }
}

// ============================================================================
// Interface for bug repro files
// ============================================================================

/// <summary>
/// Implement this interface in a separate file for each tracked SDK bug.
/// </summary>
public interface IBugRepro
{
    string Description { get; }
    /// <summary>True if this repro demonstrates a known bug (expected to fail).</summary>
    bool ExpectsFail { get; }
    Task<int> RunAsync(string cliPath);
}
