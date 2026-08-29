// ============================================================================
// Self-contained compatibility matrix — session AvailableTools MCP selectors
// ============================================================================
//
// A local stdio MCP server ("test-mcp", tools: alpha/beta/gamma) is attached to
// a session, then the runtime's resolved tool metadata is inspected. The same
// MCP server is tried against several SessionConfig.AvailableTools selectors.
//
// SDK 1.0.11 / CLI 1.0.79 supports exact wire names, bare server names,
// namespaced exact names, slash server wildcards, and source-qualified
// wildcards. The legacy dash glob remains a literal:
//
//   0. explicit dashed names                ["test-mcp-alpha", ...]   -> exposed
//   1. bare server name                     ["test-mcp"]              -> exposed
//   2. explicit namespaced / slash names    ["test-mcp/alpha", ...]   -> exposed
//   3. dash wildcard                        ["test-mcp-*"]            -> not exposed
//   4. slash wildcard                       ["test-mcp/*"]            -> exposed
//   5. source wildcard                      ["mcp:*"]                 -> exposed
//
// Exit code: 0 when every selector behaves as documented; 1 on a mismatch;
//            2 on a setup error.
//
// Run:  dotnet run                       (auto-downloads matching CLI)
//       dotnet run -- C:\path\copilot.exe (use an explicit CLI)
// ============================================================================

using GitHub.Copilot;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

var cliPath = await CliBootstrap.EnsureAsync(args);
if (cliPath is null) return 2;
Console.WriteLine($"CLI path: {cliPath}\n");

var project = McpHelper.ResolveServerProject();
if (project is null) return 2;

var mcpServer = McpHelper.CreateConfig(project);

Console.WriteLine($"MCP server: {McpHelper.ServerName}");
Console.WriteLine($"  Command: {mcpServer.Command} {string.Join(" ", mcpServer.Args!)}\n");

await using var client = new CopilotClient(
    new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
await client.StartAsync();

var scenarios = new (string Label, string[] AvailableTools, bool ExpectedExposed)[]
{
    ("explicit dashed  [test-mcp-alpha, ...]", McpHelper.Prefixed, true),
    ("bare server name [test-mcp]", new[] { McpHelper.ServerName }, true),
    ("explicit slash   [test-mcp/alpha, ...]", McpHelper.Namespaced, true),
    ("dash wildcard    [test-mcp-*] (literal)", new[] { McpHelper.DashWildcard }, false),
    ("slash wildcard   [test-mcp/*]", new[] { McpHelper.SlashWildcard }, true),
    ("source wildcard  [mcp:*]", new[] { McpHelper.SourceWildcard }, true),
};

var results = new List<(string Label, bool ExpectedExposed, bool Exposed)>();

foreach (var (label, availableTools, expectedExposed) in scenarios)
{
    Console.WriteLine("============================================================");
    Console.WriteLine($"Scenario: {label}");
    Console.WriteLine($"  AvailableTools: [{string.Join(", ", availableTools)}]");

    var sessionConfig = new SessionConfig
    {
        Model = "claude-haiku-4.5",
        McpServers = new Dictionary<string, McpServerConfig> { [McpHelper.ServerName] = mcpServer },
        AvailableTools = new List<string>(availableTools),
        OnPermissionRequest = PermissionHandler.ApproveAll,
    };

    await using var session = await client.CreateSessionAsync(sessionConfig);
    var resolvedTools = await Repro.GetCurrentToolNamesAsync(session);
    var exposed = McpHelper.AllToolsExposed(resolvedTools, McpHelper.Prefixed, out var reported, out var missing);

    Console.WriteLine($"  Resolved tools ({reported.Count}): {string.Join(", ", reported)}");
    if (!exposed) Console.WriteLine($"  Missing MCP tools: {string.Join(", ", missing)}");
    Console.WriteLine(exposed
        ? "  RESULT: MCP tools EXPOSED"
        : "  RESULT: MCP tools NOT exposed");
    Console.WriteLine();

    results.Add((label, expectedExposed, exposed));
}

Console.WriteLine("==================== Summary ====================");
foreach (var r in results)
{
    var tag = r.Exposed ? "EXPOSED" : "MISSING";
    Console.WriteLine($"  [{tag}] {r.Label}");
}

var mismatches = results.Where(r => r.ExpectedExposed != r.Exposed).ToList();
Console.WriteLine();
Console.WriteLine($"Selectors matching expected behavior: {results.Count - mismatches.Count}/{results.Count}");

if (mismatches.Count > 0)
{
    Console.WriteLine("\nFAIL: selector behavior did not match expectations:");
    foreach (var r in mismatches)
        Console.WriteLine($"  - {r.Label}: expected exposed={r.ExpectedExposed}, actual={r.Exposed}");
    return 1;
}

Console.WriteLine("\nPASS: every session AvailableTools selector behaved as documented.");
return 0;

// ============================================================================
// Helpers
// ============================================================================

static class Repro
{
    public static async Task<string> GetCurrentToolNamesAsync(CopilotSession session)
    {
        await session.Rpc.Tools.InitializeAndValidateAsync();
        var metadata = await session.Rpc.Tools.GetCurrentMetadataAsync();
        if (metadata.Tools is null)
            throw new InvalidOperationException("Runtime did not return initialized tool metadata.");

        return string.Join(",", metadata.Tools.Select(tool => tool.Name));
    }
}

static class McpHelper
{
    public const string ServerName = "test-mcp";

    public static readonly string[] Prefixed = { "test-mcp-alpha", "test-mcp-beta", "test-mcp-gamma" };
    public static readonly string[] Namespaced = { "test-mcp/alpha", "test-mcp/beta", "test-mcp/gamma" };
    public const string DashWildcard = "test-mcp-*";
    public const string SlashWildcard = "test-mcp/*";
    public const string SourceWildcard = "mcp:*";

    public static string? ResolveServerProject()
    {
        // bin/<cfg>/net8.0 -> scenario folder
        var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var proj = Path.Combine(baseDir, "test-mcp-server", "test-mcp-server.csproj");
        if (!File.Exists(proj))
        {
            Console.WriteLine($"ERROR: test-mcp-server project not found at {proj}");
            return null;
        }
        return proj;
    }

    public static McpStdioServerConfig CreateConfig(string project) => new()
    {
        Command = "dotnet",
        Args = new List<string> { "run", "--project", project, "-c", "Release" },
        Tools = new List<string> { "*" }
    };

    /// <summary>
    /// Returns true if every expected MCP tool is present in runtime metadata.
    /// </summary>
    public static bool AllToolsExposed(string response, IEnumerable<string> expectedTools,
        out List<string> reported, out List<string> missing)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill", "report_intent", "sql" };
        reported = response
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !ignored.Contains(t))
            .ToList();

        var expected = new HashSet<string>(expectedTools, StringComparer.OrdinalIgnoreCase);
        var reportedCopy = reported;
        missing = expected.Where(t => !reportedCopy.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        return missing.Count == 0;
    }
}

static class CliBootstrap
{
    public static async Task<string?> EnsureAsync(string[] args)
    {
        var sdkVersion = typeof(CopilotClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var requiredCli = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CopilotCliVersion")?.Value;

        Console.WriteLine($"SDK version:          {sdkVersion}");
        Console.WriteLine($"Required CLI version: {requiredCli ?? "unknown"}");

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";

        // 1) explicit path argument
        var explicitArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (!string.IsNullOrWhiteSpace(explicitArg) && File.Exists(explicitArg))
        {
            Console.WriteLine($"Using CLI from argument: {explicitArg}");
            return explicitArg;
        }

        // 2) reuse an existing CLI in the current dir or any ancestor
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, exeName);
            if (File.Exists(candidate) && (requiredCli is null || VersionMatches(candidate, requiredCli)))
            {
                Console.WriteLine($"Using existing CLI: {candidate}");
                return candidate;
            }
            dir = dir.Parent;
        }

        // 3) download the matching CLI into the current directory
        if (requiredCli is null)
        {
            Console.WriteLine("ERROR: cannot determine required CLI version from assembly metadata.");
            return null;
        }
        var target = Path.Combine(Directory.GetCurrentDirectory(), exeName);
        Console.WriteLine($"Downloading Copilot CLI v{requiredCli}...");
        return await DownloadAsync(requiredCli, target) ? target : null;
    }

    static bool VersionMatches(string path, string required)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var v = info.ProductVersion ?? info.FileVersion;
            return v is not null && v.Contains(required.Split('-')[0]);
        }
        catch { return true; }
    }

    static async Task<bool> DownloadAsync(string version, string targetPath)
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"win32-{arch}"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"darwin-{arch}"
            : $"linux-{arch}";
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var url = $"https://registry.npmjs.org/@github/copilot-{platform}/-/copilot-{platform}-{version}.tgz";
        Console.WriteLine($"  Source: {url}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var tgz = targetPath + ".tgz";
            try
            {
                await using (var fs = new FileStream(tgz, FileMode.Create))
                    await resp.Content.CopyToAsync(fs);

                await using var tgzStream = new FileStream(tgz, FileMode.Open, FileAccess.Read);
                await using var gz = new GZipStream(tgzStream, CompressionMode.Decompress);
                using var tar = new TarReader(gz);
                while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
                {
                    var name = entry.Name.StartsWith("package/") ? entry.Name["package/".Length..] : entry.Name;
                    if (name == exeName)
                    {
                        await entry.ExtractToFileAsync(targetPath, overwrite: true);
                        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            File.SetUnixFileMode(targetPath,
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        Console.WriteLine($"  Downloaded to {targetPath}");
                        return true;
                    }
                }
                Console.WriteLine("  ERROR: binary not found in tarball");
                return false;
            }
            finally { try { File.Delete(tgz); } catch { } }
        }
        catch (Exception ex) { Console.WriteLine($"  ERROR: {ex.Message}"); return false; }
    }
}
