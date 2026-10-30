using Microsoft.Agents.AI;
using Moq;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Interfaces;

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
            taskScopeAndPlan: "ForgeTask #1",
            skillsCatalog: catalog);

        Assert.DoesNotContain("<tools>", prefix);
        Assert.DoesNotContain("skillName", prefix);
        Assert.Contains("<skills>", prefix);
        Assert.Contains("- unit-testing: Unit testing guidelines with xUnit", prefix);
        Assert.Contains("</skills>", prefix);
        Assert.Contains("<stage_directives>", prefix);
    }

    [Fact]
    public async Task CanonicalAIFunctionFactory_CreatesLoadSkill_AndExecutesCleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-canon-test-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(tempDir, ".agents", "skills", "debugging");
        Directory.CreateDirectory(skillDir);

        var skillContent = @"---
name: debugging
description: Systematic debugging workflows
---
Check logs first.
";
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), skillContent);

        try
        {
            var canonicalTools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var tools = CanonicalAIFunctionFactory.CreateTools(canonicalTools);

            var loadSkillTool = tools.FirstOrDefault(t => t.Name == "load_skill");
            Assert.NotNull(loadSkillTool);
            Assert.Equal("Loads full instructions and reference documentation for a modular skill on demand.", loadSkillTool.Description);

            var result = await loadSkillTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?> { ["skill_name"] = "debugging" }));
            Assert.NotNull(result);
            Assert.Contains("Check logs first.", result.ToString());
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
    public async Task CanonicalAIFunctionFactory_EditTool_AppliesMultipleDisjointEdits_AndSingleEditShortcut()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-edit-schema-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "notes.txt");
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\ngamma\n");

        try
        {
            var canonicalTools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var editTool = CanonicalAIFunctionFactory.CreateTools(canonicalTools).Single(t => t.Name == "edit");

            // The schema must expose the multi-edit array alongside the single-edit shortcut.
            var schema = editTool.JsonSchema.GetRawText();
            Assert.Contains("\"edits\"", schema);
            Assert.Contains("oldText", schema);
            Assert.Contains("old_text", schema);

            var editsJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "[{\"oldText\":\"alpha\",\"newText\":\"ALPHA\"},{\"oldText\":\"gamma\",\"newText\":\"GAMMA\"}]");
            var multi = await editTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "notes.txt",
                ["edits"] = editsJson
            }));
            Assert.Contains("2 edit(s)", multi?.ToString());

            var single = await editTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "notes.txt",
                ["old_text"] = "beta",
                ["new_text"] = "BETA"
            }));
            Assert.Contains("1 edit(s)", single?.ToString());

            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("ALPHA", content);
            Assert.Contains("BETA", content);
            Assert.Contains("GAMMA", content);

            var missing = await editTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "notes.txt"
            }));
            Assert.StartsWith("Error:", missing?.ToString());
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
    public void DefaultAgentTools_DoNotExposeSkillResourceOrScriptTools_ButKeepLoadSkill()
    {
        foreach (var set in new[] { Core.Domain.DefaultAgentTools.ReadOnly, Core.Domain.DefaultAgentTools.Full, Core.Domain.DefaultAgentTools.Review })
        {
            Assert.Contains(Core.Domain.CanonicalToolNames.LoadSkill, set);
            Assert.DoesNotContain(Core.Domain.CanonicalToolNames.ReadSkillResource, set);
            Assert.DoesNotContain(Core.Domain.CanonicalToolNames.RunSkillScript, set);
        }
    }

    [Fact]
    public async Task CanonicalTools_LoadSkillAsync_DelegatesToWorkspaceSkillsService()
    {
        var mockSkillsService = new Moq.Mock<IWorkspaceSkillsService>();
        mockSkillsService.Setup(s => s.LoadSkillAsync("test-skill", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Mock skill instructions");

        var tools = new TestCanonicalToolsBuilder
        {
            SkillsService = mockSkillsService.Object
        }.Build();

        var result = await tools.LoadSkillAsync("test-skill");
        Assert.Equal("Mock skill instructions", result);
        mockSkillsService.Verify(s => s.LoadSkillAsync("test-skill", It.IsAny<CancellationToken>()), Moq.Times.Once);
    }

    [Fact]
    public async Task CanonicalTools_LoadSkillAsync_EmptySkillName_ReturnsRequiredError()
    {
        var tools = new TestCanonicalToolsBuilder().Build();
        var result = await tools.LoadSkillAsync("");
        Assert.Equal("Error: skill_name parameter is required.", result);
    }
}
