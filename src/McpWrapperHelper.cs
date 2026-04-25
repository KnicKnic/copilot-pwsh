using System.Runtime.InteropServices;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Wraps MCP local server configurations to use the <c>mcp-wrapper</c> executable.
/// The wrapper handles env var propagation and, for eligible servers, manages
/// persistent "zombie" daemon connections that survive session boundaries.
/// </summary>
/// <remarks>
/// <para>When wrapping is enabled (default), each <see cref="McpLocalServerConfig"/> is
/// transformed so that <c>mcp-wrapper</c> becomes the command, and the original command,
/// args, env vars, and cwd are passed as wrapper arguments:</para>
/// <code>
/// mcp-wrapper --env KEY1=VAL1 --env KEY2=VAL2 --cwd /path -- original-command arg1 arg2
/// </code>
/// <para>The wrapper internally decides whether to use zombie mode (persistent daemon
/// with Unix domain socket) or direct proxy mode based on regex patterns matching
/// the command and args. The <c>--no-zombie</c> flag can force direct mode.</para>
/// </remarks>
internal static class McpWrapperHelper
{
    /// <summary>
    /// Resolve the path to the <c>mcp-wrapper</c> executable bundled with this module.
    /// Expected in the same directory as the module assembly.
    /// </summary>
    /// <returns>Full path to the wrapper executable, or null if not found.</returns>
    public static string? ResolveWrapperPath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(McpWrapperHelper).Assembly.Location);
        if (assemblyDir is null) return null;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "mcp-wrapper.exe"
            : "mcp-wrapper";

        var candidate = Path.Combine(assemblyDir, exeName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Wrap all local MCP server configs to use the <c>mcp-wrapper</c> executable.
    /// Remote server configs are passed through unchanged.
    /// </summary>
    /// <param name="mcpConfig">The MCP server config dictionary (from <see cref="McpConfigLoader"/>)</param>
    /// <param name="wrapperPath">Full path to the mcp-wrapper executable</param>
    /// <returns>A new dictionary with wrapped local server configs</returns>
    public static Dictionary<string, object> WrapLocalServers(
        Dictionary<string, object> mcpConfig,
        string wrapperPath)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, config) in mcpConfig)
        {
            if (config is McpLocalServerConfig localConfig && NeedsWrapping(localConfig, wrapperPath))
            {
                result[name] = WrapConfig(localConfig, wrapperPath);
            }
            else
            {
                result[name] = config; // pass through unchanged (remote or no env/cwd)
            }
        }

        return result;
    }

    /// <summary>
    /// Check whether a local MCP server config needs wrapping.
    /// All local servers are wrapped so zombie-eligible ones get persistent daemon
    /// connections.
    /// </summary>
    private static bool NeedsWrapping(McpLocalServerConfig config, string wrapperPath) => true;

    /// <summary>
    /// Transform a single <see cref="McpLocalServerConfig"/> to use the wrapper.
    /// The original command, args, env, and cwd become wrapper arguments.
    /// </summary>
    internal static McpLocalServerConfig WrapConfig(McpLocalServerConfig original, string wrapperPath)
    {
        var wrappedArgs = new List<string>();

        // Pass env vars as explicit --env args so the zombie daemon gets
        // deterministic child keys (same key from discovery and session).
        if (original.Env is { Count: > 0 })
        {
            foreach (var (key, value) in original.Env)
            {
                wrappedArgs.Add("--env");
                wrappedArgs.Add($"{key}={value}");
            }
        }

        // Pass cwd as explicit --cwd arg for the same reason.
        if (!string.IsNullOrEmpty(original.Cwd))
        {
            wrappedArgs.Add("--cwd");
            wrappedArgs.Add(original.Cwd);
        }

        // Separator between wrapper options and child command
        wrappedArgs.Add("--");

        // Original command
        wrappedArgs.Add(original.Command ?? string.Empty);

        // Original args
        if (original.Args is { Count: > 0 })
        {
            foreach (var arg in original.Args)
                wrappedArgs.Add(arg);
        }

        return new McpLocalServerConfig
        {
            Command = wrapperPath,
            Args = wrappedArgs,
            Env = original.Env,   // SDK also sets these as process env vars (harmless duplication)
            Cwd = original.Cwd,   // SDK also sets cwd on wrapper process
            Tools = original.Tools,
            Type = original.Type,
            Timeout = original.Timeout
        };
    }
}
