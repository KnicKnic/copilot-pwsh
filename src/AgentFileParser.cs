using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;

namespace CopilotShell;

/// <summary>
/// Parses .agent.md files (YAML frontmatter + markdown body) into <see cref="CustomAgentConfig"/> objects.
/// The agent name is derived from the filename: e.g. "ado-team.agent.md" → "ado-team".
/// </summary>
/// <remarks>
/// Expected file format:
/// <code>
/// ---
/// description: 'Short description of the agent'
/// tools: ['tool1', 'tool2']
/// ---
/// Markdown body used as the agent prompt.
/// </code>
/// </remarks>
public static class AgentFileParser
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)---\s*\n?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parse an .agent.md file into a <see cref="CustomAgentConfig"/>.
    /// </summary>
    public static CustomAgentConfig Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Agent file not found: {filePath}");

        var fileName = Path.GetFileName(filePath);
        var agentName = ExtractAgentName(fileName);
        var content = File.ReadAllText(filePath);

        var config = new CustomAgentConfig { Name = agentName };

        var match = FrontmatterRegex.Match(content);
        if (match.Success)
        {
            var frontmatter = match.Groups[1].Value;
            ParseFrontmatter(frontmatter, config);

            // Everything after the frontmatter is the prompt
            var body = content[match.Length..].Trim();
            if (!string.IsNullOrEmpty(body))
                config.Prompt = body;
        }
        else
        {
            // No frontmatter — entire content is the prompt
            config.Prompt = content.Trim();
        }

        return config;
    }

    /// <summary>
    /// Parse multiple agent files.
    /// </summary>
    public static List<CustomAgentConfig> ParseMany(IEnumerable<string> filePaths)
    {
        return filePaths.Select(Parse).ToList();
    }

    /// <summary>
    /// Extract the agent name from a filename like "ado-team.agent.md" → "ado-team".
    /// Falls back to filename without extension if the pattern doesn't match.
    /// </summary>
    internal static string ExtractAgentName(string fileName)
    {
        // Pattern: name.agent.md or name.agent.yaml etc.
        var name = fileName;

        // Strip .md / .yaml / .yml extension
        foreach (var ext in new[] { ".md", ".yaml", ".yml" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^ext.Length];
                break;
            }
        }

        // Strip .agent suffix
        if (name.EndsWith(".agent", StringComparison.OrdinalIgnoreCase))
            name = name[..^".agent".Length];

        return name;
    }

    /// <summary>
    /// Lightweight YAML frontmatter parser — handles description and tools fields.
    /// Avoids a full YAML dependency.
    /// </summary>
    private static void ParseFrontmatter(string frontmatter, CustomAgentConfig config)
    {
        var lines = frontmatter.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                config.Description = ExtractStringValue(line["description:".Length..]);
            }
            else if (line.StartsWith("displayName:", StringComparison.OrdinalIgnoreCase)
                  || line.StartsWith("display_name:", StringComparison.OrdinalIgnoreCase))
            {
                config.DisplayName = ExtractStringValue(line[(line.IndexOf(':') + 1)..]);
            }
            else if (line.StartsWith("tools:", StringComparison.OrdinalIgnoreCase))
            {
                var toolsValue = line["tools:".Length..].Trim();
                // Handle multi-line arrays:
                //   tools: [...]          ← single-line inline
                //   tools:                ← value is on next line(s)
                //       [                 ← array may span many lines
                //           "a",
                //           "b",
                //       ]
                if (string.IsNullOrEmpty(toolsValue) && i + 1 < lines.Length)
                {
                    // Peek at next line to see if it starts a multi-line array
                    var nextLine = lines[i + 1].Trim();
                    if (nextLine.StartsWith('[') && !nextLine.EndsWith(']'))
                    {
                        // Multi-line array — collect lines until closing bracket
                        var sb = new System.Text.StringBuilder(nextLine);
                        i++; // skip the opening bracket line
                        while (i + 1 < lines.Length)
                        {
                            i++;
                            var contLine = lines[i].Trim();
                            sb.Append(contLine);
                            if (contLine.EndsWith(']'))
                                break;
                        }
                        toolsValue = sb.ToString();
                    }
                    else
                    {
                        toolsValue = nextLine;
                        i++; // skip the continuation line
                    }
                }
                config.Tools = ParseToolsList(toolsValue);
            }
            else if (line.StartsWith("infer:", StringComparison.OrdinalIgnoreCase))
            {
                var val = ExtractStringValue(line["infer:".Length..]);
                if (bool.TryParse(val, out var infer))
                    config.Infer = infer;
            }
        }
    }

    /// <summary>
    /// Extract a string value, stripping surrounding quotes.
    /// </summary>
    private static string ExtractStringValue(string raw)
    {
        var val = raw.Trim();
        if ((val.StartsWith('\'') && val.EndsWith('\'')) ||
            (val.StartsWith('"') && val.EndsWith('"')))
        {
            val = val[1..^1];
        }
        return val;
    }

    /// <summary>
    /// Parse an inline YAML list like ['tool1', 'tool2'] into a List&lt;string&gt;.
    /// </summary>
    private static List<string> ParseToolsList(string raw)
    {
        var tools = new List<string>();

        // Handle inline array: ['a', 'b', 'c']
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            var inner = raw[1..^1];
            foreach (var item in inner.Split(','))
            {
                var tool = ExtractStringValue(item);
                if (!string.IsNullOrEmpty(tool))
                    tools.Add(tool);
            }
        }
        else if (!string.IsNullOrEmpty(raw))
        {
            // Single tool value
            tools.Add(ExtractStringValue(raw));
        }

        return tools;
    }
}
