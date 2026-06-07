using System.Collections;
using System.Management.Automation;
using System.Text.Json;

namespace CopilotShell;

/// <summary>
/// Helpers for reading and writing a per-session JSON "sidecar" file that lives
/// alongside the Copilot CLI's own session state at
/// <c>~/.copilot/session-state/&lt;sessionId&gt;/copilotshell.json</c>.
/// This lets callers attach arbitrary metadata to a session without modifying
/// the SDK's read-only <c>SessionMetadata</c>/<c>SessionContext</c> types.
/// </summary>
internal static class SessionDataStore
{
    internal const string SidecarFileName = "copilotshell.json";

    /// <summary>Resolves the Copilot CLI config directory (default <c>~/.copilot</c>).</summary>
    internal static string ResolveConfigDir(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir))
            return configDir;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".copilot");
    }

    /// <summary>Full path to the per-session state directory.</summary>
    internal static string SessionStateDir(string configDir, string sessionId)
        => Path.Combine(configDir, "session-state", sessionId);

    /// <summary>Full path to the sidecar JSON file for a session.</summary>
    internal static string SidecarPath(string configDir, string sessionId)
        => Path.Combine(SessionStateDir(configDir, sessionId), SidecarFileName);

    /// <summary>Reads the sidecar into a <see cref="Hashtable"/>, or null if absent.</summary>
    internal static Hashtable? Read(string path)
    {
        if (!File.Exists(path))
            return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return (Hashtable)FromElement(doc.RootElement)!;
    }

    /// <summary>Writes a <see cref="Hashtable"/> to the sidecar as indented JSON.</summary>
    internal static void Write(string path, Hashtable data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var normalized = Normalize(data);
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Writes the default sidecar created automatically when a session is created,
    /// recording the OS process that created it. Existing keys are preserved so
    /// this never clobbers user-supplied data; the creation block is only written
    /// once (on first creation).
    /// </summary>
    internal static void WriteCreationDefaults(string? configDir, string sessionId)
    {
        var dir = ResolveConfigDir(configDir);
        var path = SidecarPath(dir, sessionId);

        var data = Read(path) ?? new Hashtable();

        // Only stamp creation info once; don't overwrite if already present.
        if (!data.ContainsKey("createdByProcessId"))
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            data["createdByProcessId"] = proc.Id;
            data["createdByProcessName"] = proc.ProcessName;
            data["createdAtUtc"] = DateTime.UtcNow.ToString("o");
            data["sessionId"] = sessionId;
        }

        Write(path, data);
    }

    /// <summary>Recursively converts Hashtables/lists/scalars into JSON-friendly types.</summary>
    private static object? Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case IDictionary dict:
                var map = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in dict)
                    map[entry.Key?.ToString() ?? string.Empty] = Normalize(entry.Value);
                return map;
            case string s:
                return s;
            case IEnumerable seq:
                var list = new List<object?>();
                foreach (var item in seq)
                    list.Add(Normalize(item));
                return list;
            default:
                return value;
        }
    }

    /// <summary>Recursively converts a <see cref="JsonElement"/> into Hashtables/lists/scalars.</summary>
    private static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var table = new Hashtable();
                foreach (var prop in element.EnumerateObject())
                    table[prop.Name] = FromElement(prop.Value);
                return table;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(FromElement(item));
                return list.ToArray();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }
}

/// <summary>
/// Stores arbitrary metadata in a JSON sidecar file alongside a session's CLI
/// state (<c>~/.copilot/session-state/&lt;sessionId&gt;/copilotshell.json</c>).
/// By default the supplied data is merged into any existing sidecar; use
/// <c>-Replace</c> to overwrite it entirely.
/// </summary>
/// <example>
/// <code>Set-CopilotSessionData -SessionId $session.SessionId -Data @{ ticket = 'AB#123'; owner = 'nina' }</code>
/// <code>$session | Set-CopilotSessionData -Data @{ reviewed = $true } -PassThru</code>
/// </example>
[Cmdlet(VerbsCommon.Set, "CopilotSessionData", SupportsShouldProcess = true)]
[OutputType(typeof(Hashtable))]
public sealed class SetCopilotSessionDataCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true,
        HelpMessage = "The session ID whose sidecar should be written.")]
    public string SessionId { get; set; } = null!;

    [Parameter(Mandatory = true, Position = 1,
        HelpMessage = "Key/value data to store. Merged into existing data unless -Replace is set.")]
    public Hashtable Data { get; set; } = null!;

    [Parameter(HelpMessage = "Overwrite the existing sidecar instead of merging into it.")]
    public SwitchParameter Replace { get; set; }

    [Parameter(HelpMessage = "Copilot config directory (default: ~/.copilot).")]
    public string? ConfigDir { get; set; }

    [Parameter(HelpMessage = "Return the resulting sidecar contents.")]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        var configDir = SessionDataStore.ResolveConfigDir(ConfigDir);
        var path = SessionDataStore.SidecarPath(configDir, SessionId);

        if (!ShouldProcess(path, "Write Copilot session sidecar"))
            return;

        Hashtable result;
        if (Replace.IsPresent)
        {
            result = (Hashtable)Data.Clone();
        }
        else
        {
            result = SessionDataStore.Read(path) ?? new Hashtable();
            foreach (DictionaryEntry entry in Data)
                result[entry.Key] = entry.Value;
        }

        SessionDataStore.Write(path, result);
        WriteVerbose($"Wrote session sidecar: {path}");

        if (PassThru.IsPresent)
            WriteObject(result);
    }
}

/// <summary>
/// Reads the JSON sidecar file stored alongside a session's CLI state by
/// <c>Set-CopilotSessionData</c>. Returns nothing if no sidecar exists.
/// </summary>
/// <example>
/// <code>Get-CopilotSessionData -SessionId $session.SessionId</code>
/// <code>$session | Get-CopilotSessionData</code>
/// </example>
[Cmdlet(VerbsCommon.Get, "CopilotSessionData")]
[OutputType(typeof(Hashtable))]
public sealed class GetCopilotSessionDataCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true,
        HelpMessage = "The session ID whose sidecar should be read.")]
    public string SessionId { get; set; } = null!;

    [Parameter(HelpMessage = "Copilot config directory (default: ~/.copilot).")]
    public string? ConfigDir { get; set; }

    protected override void ProcessRecord()
    {
        var configDir = SessionDataStore.ResolveConfigDir(ConfigDir);
        var path = SessionDataStore.SidecarPath(configDir, SessionId);

        var data = SessionDataStore.Read(path);
        if (data is null)
        {
            WriteVerbose($"No session sidecar found at: {path}");
            return;
        }

        WriteObject(data);
    }
}
