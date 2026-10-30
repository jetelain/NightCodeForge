using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class GlobalSkillsManagerServiceTests
{
    [Fact]
    public async Task GetGlobalSkillsAsync_ReturnsBundledSkillsAsBuiltIn()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var bundled = new BundledSkillsProvider();
            var service = new GlobalSkillsManagerService(bundled, tempDir, NullLogger<GlobalSkillsManagerService>.Instance);

            var skills = await service.GetGlobalSkillsAsync();

            Assert.Contains(skills, s => s.Name == "csharp-nav" && s.IsBuiltIn);
            Assert.Contains(skills, s => s.Name == "mdfetch" && s.IsBuiltIn);
            Assert.Contains(skills, s => s.Name == "codecondenser" && s.IsBuiltIn);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CreateCustomSkillAsync_CreatesSkillFileWithFrontmatter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgr-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var bundled = new BundledSkillsProvider();
            var service = new GlobalSkillsManagerService(bundled, tempDir, NullLogger<GlobalSkillsManagerService>.Instance);

            var req = new CreateSkillRequest(
                Name: "efcore-helper",
                Stack: "dotnet",
                Description: "EF Core migrations helper",
                Instructions: "# EF Core Helper\nRun migrations carefully.");

            await service.CreateCustomSkillAsync(req);

            var createdFile = Path.Combine(tempDir, "dotnet", "efcore-helper", "SKILL.md");
            Assert.True(File.Exists(createdFile));

            var content = await File.ReadAllTextAsync(createdFile);
            Assert.Contains("name: efcore-helper", content);
            Assert.Contains("description: EF Core migrations helper", content);
            Assert.Contains("stack: dotnet", content);
            Assert.Contains("# EF Core Helper", content);

            var allSkills = await service.GetGlobalSkillsAsync();
            var custom = allSkills.FirstOrDefault(s => s.Name == "efcore-helper");
            Assert.NotNull(custom);
            Assert.False(custom.IsBuiltIn);
            Assert.Equal("dotnet", custom.Stack);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CreateCustomSkillAsync_RejectsBuiltInNameCollision()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgr-collide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var bundled = new BundledSkillsProvider();
            var service = new GlobalSkillsManagerService(bundled, tempDir, NullLogger<GlobalSkillsManagerService>.Instance);

            var req = new CreateSkillRequest("csharp-nav", "dotnet", "Collision", "Instructions");

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateCustomSkillAsync(req));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CreateCustomSkillAsync_RejectsPathTraversalAttempts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgr-traversal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var bundled = new BundledSkillsProvider();
            var service = new GlobalSkillsManagerService(bundled, tempDir, NullLogger<GlobalSkillsManagerService>.Instance);

            var req = new CreateSkillRequest("../../../evil", "dotnet", "Evil", "Evil");

            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateCustomSkillAsync(req));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DeleteCustomSkillAsync_PreventsDeletingBuiltInSkill()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgr-del-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var bundled = new BundledSkillsProvider();
            var service = new GlobalSkillsManagerService(bundled, tempDir, NullLogger<GlobalSkillsManagerService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteCustomSkillAsync("csharp-nav"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DeleteCustomSkillAsync_DeletesCustomSkillFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgr-del-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var bundled = new BundledSkillsProvider();
            var service = new GlobalSkillsManagerService(bundled, tempDir, NullLogger<GlobalSkillsManagerService>.Instance);

            await service.CreateCustomSkillAsync(new CreateSkillRequest("temp-skill", "universal", "Desc", "Body"));
            var targetDir = Path.Combine(tempDir, "universal", "temp-skill");
            Assert.True(Directory.Exists(targetDir));

            await service.DeleteCustomSkillAsync("temp-skill");
            Assert.False(Directory.Exists(targetDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

