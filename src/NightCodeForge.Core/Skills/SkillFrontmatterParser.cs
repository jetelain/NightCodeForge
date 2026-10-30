using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Skills;

public record SkillFrontmatter(
    string Name,
    string Description,
    string Stack,
    string Instructions);

/// <summary>
/// Unified, robust parser and serializer for skill markdown frontmatter (SKILL.md).
/// Extracts metadata (name, description, stack) confined to YAML frontmatter fences (---),
/// preventing body content from leaking into metadata attributes.
/// </summary>
public static class SkillFrontmatterParser
{
    private static readonly Regex FrontmatterFenceRegex = new(
        @"\A---\r?\n([\s\S]*?)\r?\n---\r?\n?",
        RegexOptions.Compiled);

    private static readonly Regex NameRegex = new(@"(?m)^name:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex DescriptionRegex = new(@"(?m)^description:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex StackRegex = new(@"(?m)^stack:\s*(.+)$", RegexOptions.Compiled);

    public static SkillFrontmatter Parse(string? content, string? fallbackName = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new SkillFrontmatter(
                Name: CleanValue(fallbackName),
                Description: string.Empty,
                Stack: CodeStack.Universal,
                Instructions: string.Empty);
        }

        var normalizedContent = content.TrimStart('\uFEFF');
        var fenceMatch = FrontmatterFenceRegex.Match(normalizedContent);
        string header;
        string instructions;

        if (fenceMatch.Success)
        {
            header = fenceMatch.Groups[1].Value;
            instructions = normalizedContent.Substring(fenceMatch.Index + fenceMatch.Length).Trim();
        }
        else
        {
            header = string.Empty;
            instructions = normalizedContent.Trim();
        }

        var nameMatch = NameRegex.Match(header);
        var descMatch = DescriptionRegex.Match(header);
        var stackMatch = StackRegex.Match(header);

        var rawName = nameMatch.Success ? nameMatch.Groups[1].Value : null;
        var name = !string.IsNullOrWhiteSpace(rawName)
            ? CleanValue(rawName)
            : CleanValue(fallbackName);

        var desc = descMatch.Success ? CleanValue(descMatch.Groups[1].Value) : string.Empty;
        var rawStack = stackMatch.Success ? CleanValue(stackMatch.Groups[1].Value) : CodeStack.Universal;
        var stack = NormalizeStack(rawStack);

        return new SkillFrontmatter(name, desc, stack, instructions);
    }

    public static string BuildMarkdown(string name, string description, string stack, string instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normStack = NormalizeStack(stack);
        var cleanDesc = description?.Trim() ?? string.Empty;
        var cleanInstructions = instructions?.Trim() ?? string.Empty;

        return $"---\nname: {name.Trim()}\ndescription: {cleanDesc}\nstack: {normStack}\n---\n\n{cleanInstructions}\n";
    }

    public static string NormalizeStack(string? stack) =>
        CodeStack.NormalizeStack(stack);

    private static string CleanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
             (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))))
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
        }
        return trimmed;
    }
}

