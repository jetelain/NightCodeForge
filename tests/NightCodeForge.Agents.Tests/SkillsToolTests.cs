using Microsoft.Agents.AI;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;

namespace NightCodeForge.Agents.Tests;

public class SkillsToolTests
{
    [Fact]
    public void SkillsService_ResolvesHierarchicalSkillPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-path-test-" + Guid.NewGuid().ToString("N"));
        var repoSkillDir = Path.Combine(tempDir, ".agents", "skills");
        Directory.CreateDirectory(repoSkillDir);

        try
        {
            var paths = SkillsService.ResolveSkillPaths(tempDir);
            Assert.Contains(repoSkillDir, paths);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task SkillsService_LoadsSkillContent_UsingMicrosoftAgentFrameworkSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-test-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(tempDir, ".agents", "skills", "unit-testing");
        Directory.CreateDirectory(skillDir);

        var skillContent = @"---
name: unit-testing
description: Guidelines for unit testing with xUnit
---
# Unit Testing Guidelines
Always write tests before code.
";
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), skillContent);

        try
        {
            var skillsService = new SkillsService();
            var content = await skillsService.LoadSkillAsync(tempDir, "unit-testing");

            Assert.NotNull(content);
            Assert.Contains("# Unit Testing Guidelines", content);
            Assert.Contains("Always write tests before code.", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task FrameworkSkillsProvider_ProvidesTools_RegisteredAndDispatchedViaLenientToolDispatcher()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-tool-test-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(tempDir, ".agents", "skills", "csharp-best-practices");
        Directory.CreateDirectory(skillDir);

        var skillContent = @"---
name: csharp-best-practices
description: C# best practices
---
Use records for immutable data.
";
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), skillContent);

        try
        {
            var skillsService = new SkillsService();
            var skillsContext = await skillsService.GetSkillsContextAsync(tempDir);

            Assert.NotNull(skillsContext);
            Assert.NotNull(skillsContext.Tools);
            Assert.Contains(skillsContext.Tools, t => t is Microsoft.Extensions.AI.AIFunction f && f.Name == "load_skill");

            var canonicalTools = new CanonicalTools(tempDir);
            var dispatcher = new LenientToolDispatcher(canonicalTools);
            dispatcher.RegisterAITools(skillsContext.Tools);

            var result = await dispatcher.DispatchAsync("load_skill", "{\"skillName\": \"csharp-best-practices\"}");

            Assert.Contains("Use records for immutable data.", result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void SkillsService_FormatSkillsCatalogPrompt_UsesPiMinimalSyntax()
    {
        var skillsService = new SkillsService();
        var skills = new List<SkillDescriptor>
        {
            new("unit-testing", "Unit testing guidelines with xUnit", "", ""),
            new("refactoring", "Safe code refactoring patterns", "", "")
        };

        var catalog = skillsService.FormatSkillsCatalogPrompt(skills);

        Assert.Contains("<skills>", catalog);
        Assert.Contains("- unit-testing: Unit testing guidelines with xUnit", catalog);
        Assert.Contains("- refactoring: Safe code refactoring patterns", catalog);
        Assert.Contains("</skills>", catalog);
        Assert.DoesNotContain("<available_skills>", catalog);
    }

    [Fact]
    public void PromptPrefixStabilizer_IncludesMinimalSkillsCatalog_InLayer1()
    {
        var stabilizer = new Optimizations.PromptPrefixStabilizer();
        var skills = new List<SkillDescriptor>
        {
            new("unit-testing", "Unit testing guidelines with xUnit", "", "")
        };
        var skillsService = new SkillsService();
        var catalog = skillsService.FormatSkillsCatalogPrompt(skills);

        var prefix = stabilizer.BuildStabilizedPrefix(
            stagePersonaDirective: "Implement task",
            issueScopeAndPlan: "Issue #1",
            skillsCatalog: catalog);

        Assert.Contains("<tools>", prefix);
        Assert.Contains("- load_skill: Load full instructions and reference documentation for a modular skill (skillName)", prefix);
        Assert.Contains("<skills>", prefix);
        Assert.Contains("- unit-testing: Unit testing guidelines with xUnit", prefix);
        Assert.Contains("</skills>", prefix);
        Assert.Contains("<stage_directives>", prefix);
    }
}
