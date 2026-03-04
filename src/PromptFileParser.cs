using System.Text.RegularExpressions;

namespace CopilotShell;

/// <summary>
/// Result of parsing a .prompt.md file.
/// Contains the prompt body text and optional frontmatter metadata.
/// </summary>
public sealed class PromptFileResult
{
    /// <summary>Prompt body text (everything after the frontmatter).</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Optional agent name from frontmatter (e.g. agent: 'my-agent').</summary>
    public string? Agent { get; set; }

    /// <summary>Optional description from frontmatter.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Parses .prompt.md files (YAML frontmatter + markdown body) into <see cref="PromptFileResult"/> objects.
/// Compatible with VS Code's .prompt.md format.
/// </summary>
/// <remarks>
/// Expected file format:
/// <code>
/// ---
/// agent: 'my-agent'
/// description: 'Your goal is to help with specific tasks.'
/// ---
/// The actual prompt text sent to the model.
/// Can span multiple lines with markdown formatting.
/// </code>
///
/// All frontmatter fields are optional. The body (after the frontmatter) becomes the prompt text.
/// If no frontmatter is present, the entire file content is used as the prompt.
/// </remarks>
public static class PromptFileParser
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)---\s*\n?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parse a .prompt.md file.
    /// </summary>
    public static PromptFileResult Parse(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Prompt file not found: {filePath}");

        var content = File.ReadAllText(filePath);
        var result = new PromptFileResult();

        var match = FrontmatterRegex.Match(content);
        if (match.Success)
        {
            var frontmatter = match.Groups[1].Value;
            ParseFrontmatter(frontmatter, result);

            // Everything after the frontmatter is the prompt
            var body = content[match.Length..].Trim();
            if (!string.IsNullOrEmpty(body))
                result.Prompt = body;
        }
        else
        {
            // No frontmatter — entire content is the prompt
            result.Prompt = content.Trim();
        }

        return result;
    }

    /// <summary>
    /// Lightweight YAML frontmatter parser — handles agent and description fields.
    /// Avoids a full YAML dependency.
    /// </summary>
    private static void ParseFrontmatter(string frontmatter, PromptFileResult result)
    {
        var lines = frontmatter.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            {
                result.Agent = ExtractStringValue(trimmed["agent:".Length..]);
            }
            else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                result.Description = ExtractStringValue(trimmed["description:".Length..]);
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
}
