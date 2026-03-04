using System.Text.RegularExpressions;

namespace McpWrapper;

/// <summary>
/// Determines whether an MCP server is eligible for zombie (persistent daemon)
/// mode based on regex patterns matched against the command and arguments.
/// </summary>
/// <remarks>
/// <para>MCP servers are stateful — the zombie daemon keeps them alive across
/// sessions so that authentication tokens persist and there's no startup delay.
/// However, not all servers benefit from this (some may have issues with
/// long-lived connections), so eligibility is controlled by regex patterns.</para>
///
/// <para>A server is eligible if ANY of its command or args match ANY pattern.</para>
/// </remarks>
internal static class ZombieEligibility
{
    /// <summary>
    /// Regex patterns that qualify an MCP server for zombie mode.
    /// Matched against the command string and each argument individually.
    /// </summary>
    private static readonly Regex[] s_patterns =
    [
        new(@".*@azure-devops.*",           RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@".*microsoft-fabric-rti-mcp.*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@".*ev2.*",                      RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@".*grafana.*",                  RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@".*@microsoft/workiq.*",        RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>
    /// Check whether the given command + args are eligible for zombie mode.
    /// </summary>
    /// <param name="command">The MCP server command (e.g. "npx")</param>
    /// <param name="args">The command arguments (e.g. ["-y", "@azure-devops/mcp-server"])</param>
    /// <returns>True if any command/arg matches a zombie eligibility pattern.</returns>
    public static bool IsEligible(string command, IList<string> args)
    {
        // Check command
        foreach (var pattern in s_patterns)
        {
            if (pattern.IsMatch(command))
                return true;
        }

        // Check each arg
        foreach (var arg in args)
        {
            foreach (var pattern in s_patterns)
            {
                if (pattern.IsMatch(arg))
                    return true;
            }
        }

        return false;
    }
}
