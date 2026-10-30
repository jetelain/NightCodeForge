using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class WorkspaceSkillsServiceTests
{
    private readonly ISkillsService _skillsService = new SkillsService();
    private readonly BundledSkillsProvider _bundledSkillsProvider = new();

    [Fact]
    public async Task WorkspaceSkillsService_ResolvesBundledSkills_WhenNoDiskSkillsExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "skills-svc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ws = new Workspace { Id = 1, Name = "DotNetWorkspace" };
            var service = new WorkspaceSkillsService(
                ws,
                tempDir,
                taskId: null,
                _skillsService,
                _bundledSkillsProvider,
                NullLogger<WorkspaceSkillsService>.Instance);

            var active = await service.GetActiveSkillsAsync();

            Assert.Contains(active, s => s.Name == "csharp-nav");
            Assert.Contains(active, s => s.Name == "mdfetch");
            Assert.Contains(active, s => s.Name == "codecondenser");

            var csharpNavContent = await service.LoadSkillAsync("csharp-nav");
            Assert.Contains("csnav search", csharpNavContent);
            Assert.Contains("csnav decompile", csharpNavContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WorkspaceSkillsService_RepoSkillOverridesBundledSkill()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "skills-svc-repo-override-" + Guid.NewGuid().ToString("N"));
        var repoSkillDir = Path.Combine(tempDir, ".agents", "skills", "csharp-nav");
        Directory.CreateDirectory(repoSkillDir);

        var customContent = @"---
name: csharp-nav
description: Overridden repo version of csharp-nav
stack: dotnet
---
# Custom Repository csnav Instructions
";
        await File.WriteAllTextAsync(Path.Combine(repoSkillDir, "SKILL.md"), customContent);

        try
        {
            var ws = new Workspace { Id = 2, Name = "RepoOverride" };
            var service = new WorkspaceSkillsService(
                ws,
                tempDir,
                taskId: null,
                _skillsService,
                _bundledSkillsProvider,
                NullLogger<WorkspaceSkillsService>.Instance);

            var content = await service.LoadSkillAsync("csharp-nav");
            Assert.Contains("Custom Repository csnav Instructions", content);
            Assert.DoesNotContain("csnav decompile", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WorkspaceSkillsService_DisabledSkill_ReturnsPolicyDisabledMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "skills-svc-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ws = new Workspace
            {
                Id = 3,
                Name = "DisabledWorkspace",
                DisabledGlobalSkillsJson = "[\"codecondenser\"]"
            };

            var service = new WorkspaceSkillsService(
                ws,
                tempDir,
                taskId: null,
                _skillsService,
                _bundledSkillsProvider,
                NullLogger<WorkspaceSkillsService>.Instance);

            var active = await service.GetActiveSkillsAsync();
            Assert.DoesNotContain(active, s => s.Name == "codecondenser");
            Assert.Contains(active, s => s.Name == "csharp-nav");

            var loadResult = await service.LoadSkillAsync("codecondenser");
            Assert.Contains("Error: Skill 'codecondenser' is disabled by workspace configuration.", loadResult);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WorkspaceSkillsService_StackFiltering_NodeStackOnlyIncludesUniversalSkills()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "skills-svc-node-" + Guid.NewGuid().ToString("N"));
        var ncfDir = Path.Combine(tempDir, ".nightcodeforge");
        Directory.CreateDirectory(ncfDir);

        var configJson = @"
{
  ""codeStack"": ""node""
}";
        await File.WriteAllTextAsync(Path.Combine(ncfDir, "config.json"), configJson);

        try
        {
            var ws = new Workspace { Id = 4, Name = "NodeWorkspace" };
            var service = new WorkspaceSkillsService(
                ws,
                tempDir,
                taskId: null,
                _skillsService,
                _bundledSkillsProvider,
                NullLogger<WorkspaceSkillsService>.Instance);

            var active = await service.GetActiveSkillsAsync();

            Assert.Contains(active, s => s.Name == "mdfetch");
            Assert.DoesNotContain(active, s => s.Name == "csharp-nav");
            Assert.DoesNotContain(active, s => s.Name == "codecondenser");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WorkspaceSkillsService_GetSkillsCatalogPromptAsync_FormatsPromptCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "skills-svc-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ws = new Workspace { Id = 5, Name = "CatalogTest" };
            var service = new WorkspaceSkillsService(
                ws,
                tempDir,
                taskId: null,
                _skillsService,
                _bundledSkillsProvider,
                NullLogger<WorkspaceSkillsService>.Instance);

            var prompt = await service.GetSkillsCatalogPromptAsync();

            Assert.Contains("<skills>", prompt);
            Assert.Contains("- csharp-nav:", prompt);
            Assert.Contains("- mdfetch:", prompt);
            Assert.Contains("</skills>", prompt);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("dotnet", "dotnet", true)]
    [InlineData("csharp", "dotnet", true)]
    [InlineData("universal", "dotnet", true)]
    [InlineData("", "dotnet", true)]
    [InlineData("node", "dotnet", false)]
    [InlineData("python", "dotnet", false)]
    public void WorkspaceSkillsService_IsStackApplicable_MatchesCorrectly(string skillStack, string workspaceStack, bool expected)
    {
        var result = WorkspaceSkillsService.IsStackApplicable(skillStack, workspaceStack);
        Assert.Equal(expected, result);
    }
}

