using System.Collections;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CopilotShell;

/// <summary>
/// Resolves npm client configuration from environment variables and .npmrc files.
/// <para>
/// This exists so the Copilot CLI download honours corporate npm registry mirrors.
/// The tarball is fetched over plain HTTP rather than through the npm CLI (which may
/// not even be installed), so npm's own configuration has to be read directly.
/// </para>
/// <para>
/// Precedence follows npm: <c>npm_config_*</c> environment variables, then the user
/// .npmrc, then the global npmrc. A project-local .npmrc is deliberately NOT read —
/// the resolved registry decides where an executable is downloaded from, so honouring
/// a .npmrc found in an arbitrary working directory would let any cloned repository
/// redirect that download.
/// </para>
/// </summary>
internal static class NpmConfig
{
    /// <summary>Registry used when npm has no registry configured.</summary>
    public const string DefaultRegistry = "https://registry.npmjs.org";

    /// <summary>Explicit override, honoured ahead of any npm configuration.</summary>
    public const string OverrideEnvVar = "COPILOTSHELL_NPM_REGISTRY";

    private const string EnvPrefix = "npm_config_";

    private static readonly Regex EnvPlaceholder = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Resolves the npm registry base URL, without a trailing slash.
    /// </summary>
    /// <param name="scope">Optional package scope (e.g. "@github") whose scoped
    /// registry setting takes precedence over the global one, as npm does.</param>
    /// <param name="log">Optional callback for progress messages.</param>
    public static string ResolveRegistry(string? scope = null, Action<string>? log = null)
    {
        if (TryNormalize(Environment.GetEnvironmentVariable(OverrideEnvVar), out var overrideUrl))
        {
            log?.Invoke($"  Using npm registry from {OverrideEnvVar}: {overrideUrl}");
            return overrideUrl;
        }

        Dictionary<string, string> config;
        try
        {
            config = LoadMergedConfig();
        }
        catch (Exception ex)
        {
            // Never let a malformed or unreadable .npmrc break the download.
            log?.Invoke($"  Could not read npm configuration ({ex.Message}); using {DefaultRegistry}");
            return DefaultRegistry;
        }

        if (!string.IsNullOrEmpty(scope)
            && config.TryGetValue($"{scope}:registry", out var scoped)
            && TryNormalize(scoped, out var scopedUrl))
        {
            log?.Invoke($"  Using npm registry for {scope}: {scopedUrl}");
            return scopedUrl;
        }

        if (config.TryGetValue("registry", out var registry) && TryNormalize(registry, out var registryUrl))
        {
            log?.Invoke($"  Using npm registry: {registryUrl}");
            return registryUrl;
        }

        return DefaultRegistry;
    }

    /// <summary>
    /// Merges npm configuration sources, highest precedence first.
    /// </summary>
    private static Dictionary<string, string> LoadMergedConfig()
    {
        // First writer wins, so sources are visited from highest to lowest precedence.
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value) continue;
            if (!name.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            // npm maps npm_config_foo_bar to the "foo-bar" key.
            var key = name[EnvPrefix.Length..].Replace('_', '-');
            if (key.Length > 0) config.TryAdd(key, Expand(value));
        }

        foreach (var path in EnumerateConfigFiles())
            LoadFile(path, config);

        return config;
    }

    /// <summary>
    /// Yields the user npmrc followed by the global npmrc.
    /// </summary>
    private static IEnumerable<string> EnumerateConfigFiles()
    {
        var userConfig = GetEnv("NPM_CONFIG_USERCONFIG");
        yield return !string.IsNullOrWhiteSpace(userConfig)
            ? userConfig
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");

        var globalConfig = GetEnv("NPM_CONFIG_GLOBALCONFIG");
        if (!string.IsNullOrWhiteSpace(globalConfig))
        {
            yield return globalConfig;
            yield break;
        }

        var prefix = GetEnv("NPM_CONFIG_PREFIX");
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")
                : "/usr/local";
        }

        yield return Path.Combine(prefix, "etc", "npmrc");
    }

    /// <summary>
    /// Parses an .npmrc file, adding only keys that are not already set by a
    /// higher-precedence source.
    /// </summary>
    private static void LoadFile(string path, IDictionary<string, string> config)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (key.Length == 0 || value.Length == 0) continue;
            config.TryAdd(key, Expand(value));
        }
    }

    /// <summary>
    /// Expands npm's <c>${VAR}</c> placeholders, leaving unknown variables untouched.
    /// </summary>
    private static string Expand(string value) =>
        value.Contains("${", StringComparison.Ordinal)
            ? EnvPlaceholder.Replace(value, m =>
                Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value)
            : value;

    /// <summary>
    /// Reads an environment variable, also trying the lowercase form because npm
    /// writes lowercase names and Unix environments are case-sensitive.
    /// </summary>
    private static string? GetEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name.ToLowerInvariant());

    /// <summary>
    /// Validates that a configured registry is an absolute http(s) URL and strips
    /// any trailing slash so it can be concatenated with a package path.
    /// </summary>
    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        normalized = trimmed;
        return true;
    }
}
