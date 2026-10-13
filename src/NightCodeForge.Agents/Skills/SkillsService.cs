using System.Text;
using System.Text.RegularExpressions;

namespace NightCodeForge.Agents.Skills;

public record SkillDescriptor(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillFilePath);

/// <summary>
/// Service implementing the progressive disclosure pattern for modular skills.
/// Advertises skill summaries in the prompt and loads full bodies dynamically via load_skill.
/// </summary>
public partial class SkillsService
{
    [GeneratedRegex(@"^---\s*[\r\n]+(.*?)\s*[\r\n]+---", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    public async Task<IReadOnlyList<SkillDescriptor>> DiscoverSkillsAsync(string workspaceRoot, string? globalSkillsDirectory = null, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase);

        // 1. Global skills
        if (!string.IsNullOrWhiteSpace(globalSkillsDirectory) && Directory.Exists(globalSkillsDirectory))
        {
            await ScanSkillsDirectoryAsync(globalSkillsDirectory, result, cancellationToken);
        }

        // 2. Workspace skills (override global)
        var workspaceSkillDirs = new[]
        {
            Path.Combine(workspaceRoot, ".nightcodeforge", "skills"),
            Path.Combine(workspaceRoot, ".agents", "skills"),
            Path.Combine(workspaceRoot, "skills")
        };

        foreach (var dir in workspaceSkillDirs)
        {
            if (Directory.Exists(dir))
            {
                await ScanSkillsDirectoryAsync(dir, result, cancellationToken);
            }
        }

        return result.Values.ToList();
    }

    private async Task ScanSkillsDirectoryAsync(string baseDir, Dictionary<string, SkillDescriptor> result, CancellationToken cancellationToken)
    {
        var subDirs = Directory.GetDirectories(baseDir);
        foreach (var subDir in subDirs)
        {
            var skillFile = Path.Combine(subDir, "SKILL.md");
            if (File.Exists(skillFile))
            {
                var content = await File.ReadAllTextAsync(skillFile, cancellationToken);
                var (name, desc) = ParseFrontmatter(content, Path.GetFileName(subDir));
                result[name] = new SkillDescriptor(name, desc, subDir, skillFile);
            }
        }
    }

    private static (string Name, string Description) ParseFrontmatter(string content, string defaultName)
    {
        var match = FrontmatterRegex().Match(content);
        if (!match.Success)
        {
            return (defaultName, $"Skill in {defaultName}");
        }

        var yaml = match.Groups[1].Value;
        var nameMatch = NameRegex().Match(yaml);
        var descMatch = DescriptionRegex().Match(yaml);

        var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim('\'', '"', ' ') : defaultName;
        var desc = descMatch.Success ? descMatch.Groups[1].Value.Trim('\'', '"', ' ') : $"Skill for {name}";

        return (name, desc);
    }

    public string FormatSkillsCatalogPrompt(IReadOnlyList<SkillDescriptor> skills)
    {
        if (skills == null || skills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<skills>");
        sb.AppendLine("The following modular skills are available to assist with your tasks. Use the 'load_skill' tool to view full instructions when relevant:");
        foreach (var s in skills)
        {
            sb.AppendLine($"- {s.Name}: {s.Description}");
        }
        sb.AppendLine("</skills>");
        return sb.ToString().TrimEnd();
    }

    public async Task<string> LoadSkillContentAsync(IReadOnlyList<SkillDescriptor> skills, string skillName, CancellationToken cancellationToken = default)
    {
        var skill = skills.FirstOrDefault(s => string.Equals(s.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill == null)
        {
            return $"Skill '{skillName}' not found. Available skills: {string.Join(", ", skills.Select(s => s.Name))}";
        }

        if (File.Exists(skill.SkillFilePath))
        {
            return await File.ReadAllTextAsync(skill.SkillFilePath, cancellationToken);
        }

        return $"Skill file not found at '{skill.SkillFilePath}'.";
    }
}
