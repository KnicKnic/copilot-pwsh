// ============================================================================
// Investigation — origin of the mysterious "sql" tool
// ============================================================================
//
// The agent-tool-scoping repros consistently surface a "sql" tool that leaks
// past the restricted agent's allow-list (Tools = ["view"]). This harness
// reproduces that, then interrogates the model in the SAME session to extract
// the verbatim tool definitions / origin for every tool it can see — with a
// focus on "sql".
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

var restricted = new CustomAgentConfig
{
    Name = "restricted",
    Description = "Agent with only 1 tool",
    Prompt = "You are a restricted agent. You should only have access to 'view' tool.",
    Tools = new List<string> { "view" }
};

await using var client = new CopilotClient(
    new CopilotClientOptions { Connection = RuntimeConnection.ForStdio(path: cliPath) });
await client.StartAsync();

var sessionConfig = new SessionConfig
{
    Model = "claude-haiku-4.5",
    CustomAgents = new List<CustomAgentConfig> { restricted },
    Agent = "restricted",
    OnPermissionRequest = PermissionHandler.ApproveAll,
};

Console.WriteLine("Creating session with SessionConfig.Agent = 'restricted' (Tools = [view])...\n");
await using var session = await client.CreateSessionAsync(sessionConfig);

// --- Interrogation prompts, asked sequentially in the same session ----------
var prompts = new (string Label, string Prompt)[]
{
    ("1. Reproduce tool list",
        "List every tool you have access to. Output ONLY the exact full internal tool identifier for each tool, as a comma-separated list. No descriptions, no markdown."),

    ("2. Full definition of each tool",
        "For EVERY tool you can call, output a numbered block with these fields copied VERBATIM from the tool definition you were given:\n" +
        "- name (exact internal identifier)\n" +
        "- description (the full description text, word for word)\n" +
        "- parameters / input JSON schema (exact)\n" +
        "Do not summarize or paraphrase. If you cannot see a field, write '<not provided>'."),

    ("3. Focus on the 'sql' tool",
        "Focus only on the tool named 'sql'. Answer precisely:\n" +
        "a) What is its exact internal identifier?\n" +
        "b) Quote its full description verbatim.\n" +
        "c) Quote its full input schema / parameters verbatim.\n" +
        "d) Based ONLY on its definition, where does it appear to originate — a built-in CLI tool, an MCP server (which one?), or a custom agent? Quote any text in the definition that indicates the source.\n" +
        "If 'sql' is not actually one of your tools, say so explicitly."),

    ("4. Origin classification table",
        "Output one line per tool you can call, pipe-delimited:\n" +
        "<identifier> | <origin: built-in | mcp:<server> | agent> | <evidence from the definition for that origin>\n" +
        "Include 'sql' and 'skill'. Base the origin ONLY on the tool definitions, not on guesses."),
};

foreach (var (label, prompt) in prompts)
{
    Console.WriteLine("============================================================");
    Console.WriteLine($"  {label}");
    Console.WriteLine("============================================================");
    var answer = await Repro.QueryAsync(session, prompt);
    Console.WriteLine(answer);
    Console.WriteLine();
}

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

        var explicitArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (!string.IsNullOrWhiteSpace(explicitArg) && File.Exists(explicitArg))
        {
            Console.WriteLine($"Using CLI from argument: {explicitArg}");
            return explicitArg;
        }

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
