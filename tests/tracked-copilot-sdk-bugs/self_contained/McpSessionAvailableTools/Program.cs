// ============================================================================
// Self-contained repro — session AvailableTools selector forms that fail to
// expose MCP tools (github/copilot-sdk#861)
// ============================================================================
//
// A local stdio MCP server ("test-mcp", tools: alpha/beta/gamma) is attached to
// a session, then the model is asked to list its tools. The same MCP server is
// tried against several SessionConfig.AvailableTools selector forms.
//
// At the session level the CLI only honors the explicit DASHED tool names
// (test-mcp-alpha, ...). Every other form below fails to expose the MCP tools:
//
//   1. explicit namespaced / slash names   ["test-mcp/alpha", ...]   -> FAILS
//   2. dash wildcard                        ["test-mcp-*"]            -> FAILS
//   3. slash wildcard                       ["test-mcp/*"]            -> FAILS
//
// For reference the working baseline form is also exercised:
//
//   0. explicit dashed names                ["test-mcp-alpha", ...]   -> works
//
// EXPECTED (once fixed): every form exposes test-mcp-alpha/beta/gamma.
// ACTUAL:   only the dashed-explicit form exposes them; forms 1-3 reproduce
//           the bug (model sees only built-in tools).
//
// Exit code: 0 only if EVERY bug form (1-3) now exposes the MCP tools (fully
//            fixed); 1 while any bug form still fails (bug reproduces); 2 on a
//            setup error.
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

// Each scenario: a label, the AvailableTools selector form, and whether it is a
// known bug form (true) or the working baseline (false).
var scenarios = new (string Label, string[] AvailableTools, bool IsBug)[]
{
    ("explicit dashed  [test-mcp-alpha, ...]   (baseline)", McpHelper.Prefixed,         false),
    ("explicit slash   [test-mcp/alpha, ...]   (bug)",      McpHelper.Namespaced,       true),
    ("dash wildcard    [test-mcp-*]            (bug)",       new[] { McpHelper.DashWildcard },  true),
    ("slash wildcard   [test-mcp/*]            (bug)",       new[] { McpHelper.SlashWildcard }, true),
};

var results = new List<(string Label, bool IsBug, bool Exposed)>();

foreach (var (label, availableTools, isBug) in scenarios)
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
    var response = await Repro.QueryAsync(session, McpHelper.ListToolsPrompt);
    var exposed = McpHelper.AllToolsExposed(response, McpHelper.Prefixed, out var reported, out var missing);

    Console.WriteLine($"  Reported tools ({reported.Count}): {string.Join(", ", reported)}");
    if (!exposed) Console.WriteLine($"  Missing MCP tools: {string.Join(", ", missing)}");
    Console.WriteLine(exposed
        ? "  RESULT: MCP tools EXPOSED"
        : "  RESULT: MCP tools NOT exposed (bug reproduces)");
    Console.WriteLine();

    results.Add((label, isBug, exposed));
}

Console.WriteLine("==================== Summary ====================");
foreach (var r in results)
{
    var tag = r.Exposed ? "EXPOSED" : "MISSING";
    Console.WriteLine($"  [{tag}] {r.Label}");
}

var bugForms = results.Where(r => r.IsBug).ToList();
var stillBroken = bugForms.Where(r => !r.Exposed).ToList();
Console.WriteLine();
Console.WriteLine($"Bug forms exposing MCP tools: {bugForms.Count - stillBroken.Count}/{bugForms.Count}");

if (stillBroken.Count > 0)
{
    Console.WriteLine("\nFAIL: the following session AvailableTools forms do NOT expose MCP tools:");
    foreach (var r in stillBroken) Console.WriteLine($"  - {r.Label}");
    return 1;
}

Console.WriteLine("\nPASS: every session AvailableTools selector form now exposes the MCP tools.");
return 0;

// ============================================================================
// Helpers
// ============================================================================

static class Repro
{
    public static async Task<string> QueryAsync(CopilotSession session, string prompt)
    {
        var done = new TaskCompletionSource();
        string? content = null;
        using var sub = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg: content = msg.Data.Content; break;
                case SessionIdleEvent: done.TrySetResult(); break;
                case SessionErrorEvent err: done.TrySetException(new Exception(err.Data.Message)); break;
            }
        });
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(120)));
        if (completed != done.Task) throw new TimeoutException("Timed out waiting for response");
        await done.Task;
        return content?.Trim() ?? "";
    }
}

static class McpHelper
{
    public const string ServerName = "test-mcp";

    public static readonly string[] Prefixed = { "test-mcp-alpha", "test-mcp-beta", "test-mcp-gamma" };
    public static readonly string[] Namespaced = { "test-mcp/alpha", "test-mcp/beta", "test-mcp/gamma" };
    public const string DashWildcard = "test-mcp-*";
    public const string SlashWildcard = "test-mcp/*";

    public const string ListToolsPrompt =
        "List every tool you have access to. Output ONLY the exact full internal tool identifier for each tool, as a comma-separated list. No descriptions, no categories, no markdown, no short names.";

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
    /// Returns true if every expected MCP tool is present in the model's reply.
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
