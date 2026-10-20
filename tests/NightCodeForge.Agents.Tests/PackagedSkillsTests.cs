using System.Text.RegularExpressions;
using NightCodeForge.Agents.Skills;

namespace NightCodeForge.Agents.Tests;

public class PackagedSkillsTests
{
    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "NightCodeForge.slnx")) ||
                Directory.Exists(Path.Combine(current.FullName, "skills")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from AppContext.BaseDirectory: " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("ncf-backlog-migrator")]
    [InlineData("ncf-backlog")]
    [InlineData("ncf-project-readiness")]
    public void PackagedSkill_ConformsToFrontmatterContract(string skillName)
    {
        var repoRoot = FindRepositoryRoot();
        var skillDir = Path.Combine(repoRoot, "skills", skillName);
        var skillFile = Path.Combine(skillDir, "SKILL.md");

        Assert.True(Directory.Exists(skillDir), $"Skill directory not found: {skillDir}");
        Assert.True(File.Exists(skillFile), $"SKILL.md not found in {skillDir}");

        var content = File.ReadAllText(skillFile);
        Assert.False(string.IsNullOrWhiteSpace(content), "SKILL.md must not be empty");

        // Frontmatter must be enclosed between leading --- and closing ---
        var match = Regex.Match(content, @"\A---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)\z", RegexOptions.Singleline);
        Assert.True(match.Success, $"SKILL.md in {skillName} does not have valid frontmatter delimiters (---)");

        var frontmatter = match.Groups[1].Value;
        var body = match.Groups[2].Value;

        // Name attribute
        var nameMatch = Regex.Match(frontmatter, @"^name:\s*([^\r\n]+)$", RegexOptions.Multiline);
        Assert.True(nameMatch.Success, $"Missing 'name:' field in frontmatter of {skillName}");
        Assert.Equal(skillName, nameMatch.Groups[1].Value.Trim());

        // Version attribute (must be positive integer)
        var versionMatch = Regex.Match(frontmatter, @"^version:\s*([^\r\n]+)$", RegexOptions.Multiline);
        Assert.True(versionMatch.Success, $"Missing 'version:' field in frontmatter of {skillName}");
        Assert.True(int.TryParse(versionMatch.Groups[1].Value.Trim(), out var version) && version > 0,
            $"Version in {skillName} must be a positive integer, got: {versionMatch.Groups[1].Value}");

        // Description attribute
        var descMatch = Regex.Match(frontmatter, @"^description:\s*([^\r\n]+)$", RegexOptions.Multiline);
        Assert.True(descMatch.Success, $"Missing 'description:' field in frontmatter of {skillName}");
        Assert.False(string.IsNullOrWhiteSpace(descMatch.Groups[1].Value.Trim()),
            $"Description in {skillName} must not be empty");

        // Body content
        Assert.False(string.IsNullOrWhiteSpace(body), $"Markdown body for skill {skillName} must not be empty");
    }

    [Fact]
    public async Task SkillsService_DiscoversAndLoadsAllPackagedSkills()
    {
        var repoRoot = FindRepositoryRoot();
        var skillsService = new SkillsService();

        var discovered = await skillsService.DiscoverSkillsAsync(repoRoot);
        Assert.NotNull(discovered);

        var names = discovered.Select(s => s.Name).ToList();
        Assert.Contains("ncf-backlog", names);
        Assert.Contains("ncf-backlog-migrator", names);
        Assert.Contains("ncf-project-readiness", names);

        foreach (var expectedSkill in new[] { "ncf-backlog", "ncf-backlog-migrator", "ncf-project-readiness" })
        {
            var content = await skillsService.LoadSkillAsync(repoRoot, expectedSkill);
            Assert.NotNull(content);
            Assert.DoesNotMatch(@"^Error:\s*", content);
            Assert.Contains(expectedSkill, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TaskTemplate_ConformsToContractAndRequiredSections()
    {
        var repoRoot = FindRepositoryRoot();
        var templatePath = Path.Combine(repoRoot, "skills", "ncf-backlog-migrator", "assets", "TASK_TEMPLATE.md");

        Assert.True(File.Exists(templatePath), $"TASK_TEMPLATE.md not found at {templatePath}");

        var content = File.ReadAllText(templatePath);
        Assert.Contains("title:", content);
        Assert.Contains("priority:", content);
        Assert.Contains("merge_mode:", content);
        Assert.Contains("base_branch:", content);
        Assert.Contains("depends_on:", content);

        Assert.Contains("## 📋 Requirements & Description", content);
        Assert.Contains("Summary", content);
        Assert.Contains("Implementation Checklist", content);
        Assert.Contains("Affected Files", content);
        Assert.Contains("Acceptance Criteria", content);
        Assert.Contains("Prerequisite Dependencies", content);
    }

    [Theory]
    [InlineData("ncf-backlog")]
    [InlineData("ncf-backlog-migrator")]
    public void ExternalHarnessSkills_ExplainDocsTasksFrontmatterAndDatabaseSync(string skillName)
    {
        var repoRoot = FindRepositoryRoot();
        var skillFile = Path.Combine(repoRoot, "skills", skillName, "SKILL.md");

        Assert.True(File.Exists(skillFile), $"SKILL.md not found for {skillName}");
        var content = File.ReadAllText(skillFile);

        // Explains docs/tasks management
        Assert.Contains("docs/tasks", content);

        // Explains YAML frontmatter schema
        Assert.Contains("depends_on", content);
        Assert.Contains("merge_mode", content);
        Assert.Contains("priority", content);
        Assert.Contains("title", content);

        // Explains database synchronization
        Assert.Contains("sync", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database", content, StringComparison.OrdinalIgnoreCase);

        // Explains dependency management
        Assert.Contains("dependenc", content, StringComparison.OrdinalIgnoreCase);

        // Focuses only on external harness (does not reference internal propose_tasks tool)
        Assert.DoesNotContain("propose_tasks", content);
    }

    [Theory]
    [InlineData("BACKLOG_MIGRATION_PROMPT.md", "<PASTE_YOUR_ROADMAP_OR_ISSUES_HERE>")]
    [InlineData("TASK_DECOMPOSITION_PROMPT.md", "<PASTE_FEATURE_OR_ARCHITECTURE_SPEC_HERE>")]
    [InlineData("PROJECT_PREPAREDNESS_PROMPT.md", "<PASTE_PROJECT_DETAILS_OR_DIRECTORY_TREE_HERE>")]
    public void PromptLibrary_ContainsRequiredTemplatesAndPlaceholders(string promptFileName, string expectedPlaceholder)
    {
        var repoRoot = FindRepositoryRoot();
        var promptPath = Path.Combine(repoRoot, "docs", "prompts", promptFileName);

        Assert.True(File.Exists(promptPath), $"Prompt template not found at {promptPath}");

        var content = File.ReadAllText(promptPath);
        Assert.False(string.IsNullOrWhiteSpace(content), $"{promptFileName} must not be empty");
        Assert.Contains(expectedPlaceholder, content);
        Assert.Contains("NightCodeForge", content);
    }

    [Fact]
    public async Task SkillsService_DiscoversValidSkills_WhenMalformedSkillPresent_FailSafeRule()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-failsafe-" + Guid.NewGuid().ToString("N"));
        var skillsDir = Path.Combine(tempDir, "skills");
        Directory.CreateDirectory(skillsDir);

        var validSkillDir = Path.Combine(skillsDir, "valid-skill");
        Directory.CreateDirectory(validSkillDir);
        await File.WriteAllTextAsync(Path.Combine(validSkillDir, "SKILL.md"), @"---
name: valid-skill
version: 1
description: A completely valid skill
---
# Valid Skill
Instructions go here.
");

        var malformedSkillDir = Path.Combine(skillsDir, "corrupted-skill");
        Directory.CreateDirectory(malformedSkillDir);
        // Corrupted file: not valid frontmatter
        await File.WriteAllTextAsync(Path.Combine(malformedSkillDir, "SKILL.md"), "NO_FRONTMATTER_HERE");

        try
        {
            var skillsService = new SkillsService();
            var discovered = await skillsService.DiscoverSkillsAsync(tempDir);

            // Fail-safe: malformed skill must not crash the service; valid skill is discovered
            Assert.NotNull(discovered);
            Assert.Contains(discovered, s => s.Name == "valid-skill");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
