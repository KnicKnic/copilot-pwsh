using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace CopilotShell;

internal static class AgentDiscovery
{
    private const string AgentFilePattern = "*.agent.md";

    public static IReadOnlyList<string> GetDefaultAgentFolders(string workingDirectory)
    {
        var directories = new List<string>();

        var projectRoot = FindRepositoryRoot(workingDirectory) ?? workingDirectory;
        directories.Add(Path.Combine(projectRoot, ".github", "agents"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            directories.Add(Path.Combine(userProfile, ".copilot", "agents"));

        return DeduplicatePaths(directories);
    }

    public static IReadOnlyList<string> ResolveAgentFolders(
        IEnumerable<string> agentFolders,
        Func<string, string> resolvePath)
    {
        return DeduplicatePaths(agentFolders.Select(resolvePath));
    }

    public static IEnumerable<string> EnumerateAgentFiles(
        IEnumerable<string> agentFolders,
        Action<string> writeWarning,
        bool warnMissing)
    {
        foreach (var path in agentFolders)
        {
            if (File.Exists(path))
            {
                if (IsAgentFile(path))
                    yield return path;
                else
                    writeWarning($"Agent folder '{path}' is a file but does not end with '.agent.md'.");

                continue;
            }

            if (!Directory.Exists(path))
            {
                if (warnMissing)
                    writeWarning($"Agent folder '{path}' does not exist.");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, AgentFilePattern, SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    public static IEnumerable<(string Name, string Path)> EnumerateAgentProfiles(IEnumerable<string> agentFolders)
    {
        foreach (var file in EnumerateAgentFiles(agentFolders, static _ => { }, warnMissing: false))
        {
            yield return (AgentFileParser.ExtractAgentName(Path.GetFileName(file)), file);
        }
    }

    public static string? FindAgentFile(
        string agentNameOrPath,
        IEnumerable<string> agentFolders,
        Func<string, string> resolvePath)
    {
        var resolvedCandidate = resolvePath(agentNameOrPath);
        if (File.Exists(resolvedCandidate) && IsAgentFile(resolvedCandidate))
            return resolvedCandidate;

        var desiredFileName = agentNameOrPath.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(agentNameOrPath)
            : $"{agentNameOrPath}.agent.md";

        foreach (var path in agentFolders)
        {
            if (File.Exists(path))
            {
                if (string.Equals(Path.GetFileName(path), desiredFileName, StringComparison.OrdinalIgnoreCase))
                    return path;
                continue;
            }

            if (!Directory.Exists(path))
                continue;

            var direct = Path.Combine(path, desiredFileName);
            if (File.Exists(direct))
                return direct;

            var match = Directory.EnumerateFiles(path, AgentFilePattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file => string.Equals(Path.GetFileName(file), desiredFileName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    private static bool IsAgentFile(string path) =>
        path.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase);

    private static string? FindRepositoryRoot(string workingDirectory)
    {
        var current = new DirectoryInfo(workingDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> DeduplicatePaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
                result.Add(fullPath);
        }

        return result;
    }
}

public sealed class CopilotAgentNameCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName,
        string parameterName,
        string wordToComplete,
        CommandAst commandAst,
        IDictionary fakeBoundParameters)
    {
        var workingDirectory = GetCurrentPowerShellDirectory();
        var agentFolders = GetBoundAgentFolders(fakeBoundParameters);
        var searchPaths = agentFolders is { Length: > 0 }
            ? AgentDiscovery.ResolveAgentFolders(agentFolders, path => ResolveCompleterPath(path, workingDirectory))
            : AgentDiscovery.GetDefaultAgentFolders(workingDirectory);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in AgentDiscovery.EnumerateAgentProfiles(searchPaths))
        {
            if (!seen.Add(name))
                continue;

            if (name.Contains(wordToComplete ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                yield return new CompletionResult(name, name, CompletionResultType.ParameterValue, path);
        }
    }

    private static string GetCurrentPowerShellDirectory()
    {
        return Directory.GetCurrentDirectory();
    }

    private static string[]? GetBoundAgentFolders(IDictionary fakeBoundParameters)
    {
        foreach (var key in new[] { "AgentFolder", "AgentFolders", "AgentFileFolder", "AgentFileFolders", "AgentPath", "AgentPaths" })
        {
            if (!fakeBoundParameters.Contains(key))
                continue;

            var value = fakeBoundParameters[key];
            if (value is string single)
                return new[] { single };

            if (value is IEnumerable enumerable)
                return enumerable.Cast<object?>()
                    .Select(item => item?.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray();
        }

        return null;
    }

    private static string ResolveCompleterPath(string path, string workingDirectory)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~", StringComparison.Ordinal))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = userProfile + expanded[1..];
        }

        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(workingDirectory, expanded));
    }
}
