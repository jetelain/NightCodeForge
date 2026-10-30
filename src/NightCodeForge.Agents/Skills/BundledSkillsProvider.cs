using System.Reflection;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Skills;

namespace NightCodeForge.Agents.Skills;

public record BundledSkillEntry(
    SkillDescriptor Descriptor,
    string RelativePath,
    string Content);

public interface IBundledSkillsProvider
{
    IReadOnlyList<SkillDescriptor> GetBundledSkills();
    IReadOnlyList<BundledSkillEntry> GetBundledEntries();
    string? GetSkillContent(string skillName);
    bool TryGetSkillContent(string skillName, out string? content);
}

public class BundledSkillsProvider : IBundledSkillsProvider
{
    private static readonly Assembly AgentsAssembly = typeof(BundledSkillsProvider).Assembly;
    private readonly Lazy<List<BundledSkillEntry>> _entriesLazy;

    public BundledSkillsProvider()
    {
        _entriesLazy = new Lazy<List<BundledSkillEntry>>(LoadBundledSkills);
    }

    public IReadOnlyList<SkillDescriptor> GetBundledSkills() =>
        _entriesLazy.Value.Select(e => e.Descriptor).ToList();

    public IReadOnlyList<BundledSkillEntry> GetBundledEntries() =>
        _entriesLazy.Value;

    public string? GetSkillContent(string skillName)
    {
        if (TryGetSkillContent(skillName, out var content))
        {
            return content;
        }
        return null;
    }

    public bool TryGetSkillContent(string skillName, out string? content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return false;
        }

        var entry = _entriesLazy.Value.FirstOrDefault(e =>
            string.Equals(e.Descriptor.Name, skillName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (entry != null)
        {
            content = entry.Content;
            return true;
        }

        return false;
    }

    private static List<BundledSkillEntry> LoadBundledSkills()
    {
        var entries = new List<BundledSkillEntry>();
        var resourceNames = AgentsAssembly.GetManifestResourceNames();

        foreach (var resourceName in resourceNames)
        {
            if (!resourceName.Contains(".Skills.Bundled.", StringComparison.OrdinalIgnoreCase) ||
                !resourceName.EndsWith(".SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = AgentsAssembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var metadata = SkillFrontmatterParser.Parse(content);
            if (string.IsNullOrWhiteSpace(metadata.Name))
            {
                continue;
            }

            // Map relative path: e.g. dotnet/csharp-nav/SKILL.md
            var relativePath = $"{metadata.Stack}/{metadata.Name}/SKILL.md";

            var descriptor = new SkillDescriptor(
                Name: metadata.Name,
                Description: metadata.Description,
                DirectoryPath: string.Empty,
                SkillFilePath: resourceName,
                Stack: metadata.Stack,
                IsBuiltIn: true);

            entries.Add(new BundledSkillEntry(descriptor, relativePath, content));
        }

        return entries;
    }
}

