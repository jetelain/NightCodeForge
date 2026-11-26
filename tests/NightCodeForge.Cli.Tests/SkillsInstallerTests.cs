namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Common;
using NightCodeForge.Cli.Services;

public class SkillsInstallerTests
{
    [Fact]
    public void EmbeddedSkills_AreAvailableInAssembly()
    {
        foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
        {
            var content = SkillsInstaller.ReadEmbeddedResource($"skills/{skillName}/SKILL.md");
            Assert.NotNull(content);
            Assert.Contains($"name: {skillName}", content);
            Assert.True(SkillsInstaller.GetEmbeddedSkillVersion(skillName) > 0);
        }

        // Verify task template asset is present
        var template = SkillsInstaller.ReadEmbeddedResource("skills/ncf-backlog-migrator/assets/TASK_TEMPLATE.md");
        Assert.NotNull(template);
        Assert.Contains("## 📋 Requirements & Description", template);
    }

    [Fact]
    public void InstallSkills_WritesSkillsWithFrontmatterAndManagedMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            SkillsInstaller.InstallSkills(tempDir);

            foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
            {
                var skillDir = Path.Combine(tempDir, ".agents", "skills", skillName);
                var skillFile = Path.Combine(skillDir, "SKILL.md");

                Assert.True(Directory.Exists(skillDir), $"Directory missing: {skillDir}");
                Assert.True(File.Exists(skillFile), $"File missing: {skillFile}");

                var content = File.ReadAllText(skillFile);
                Assert.StartsWith("---\n", content);
                Assert.Contains($"name: {skillName}", content);
                Assert.Contains(Constants.ManagedBlockHeader, content);
                Assert.Contains(Constants.ManagedBlockFooter, content);
            }

            // Verify asset was copied
            var templatePath = Path.Combine(tempDir, ".agents", "skills", "ncf-backlog-migrator", "assets", "TASK_TEMPLATE.md");
            Assert.True(File.Exists(templatePath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void InstallSkills_IsIdempotent_AndPreservesUserContentOutsideMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-skills-idempotent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Initial install
            SkillsInstaller.InstallSkills(tempDir);

            // User adds a custom note at the bottom of ncf-backlog/SKILL.md
            var backlogFile = Path.Combine(tempDir, ".agents", "skills", "ncf-backlog", "SKILL.md");
            var originalContent = File.ReadAllText(backlogFile);
            var customAppendix = "\n# User Custom Rules\nAlways double check DB migrations.\n";
            File.AppendAllText(backlogFile, customAppendix);

            // Re-run install (like a second ncf init)
            SkillsInstaller.InstallSkills(tempDir);

            var updatedContent = File.ReadAllText(backlogFile);
            Assert.Contains(customAppendix.Trim(), updatedContent);
            Assert.Contains(Constants.ManagedBlockHeader, updatedContent);
            Assert.Contains(Constants.ManagedBlockFooter, updatedContent);

            // Running a 3rd time is identical
            SkillsInstaller.InstallSkills(tempDir);
            var recheckContent = File.ReadAllText(backlogFile);
            Assert.Equal(updatedContent, recheckContent);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void PrepareSkillContent_ProducesStrictLfLineEndings()
    {
        var rawEmbedded = "---\nname: test-skill\nversion: 1\n---\r\n# Body line 1\r\n# Body line 2\r\n";
        var result = SkillsInstaller.PrepareSkillContent(rawEmbedded);

        Assert.DoesNotContain("\r\n", result);
        Assert.Contains("\n", result);
        Assert.StartsWith("---\nname: test-skill\nversion: 1\n---\n\n", result);
    }

    [Fact]
    public void PrepareSkillContent_PreservesUserContentBeforeAndAfterManagedBlock()
    {
        var rawEmbedded = "---\nname: test-skill\nversion: 1\n---\n# Official Instructions\nDo this.\n";

        var existingFile = Path.Combine(Path.GetTempPath(), "skill-user-before-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var initialSkill = @$"---
name: test-skill
version: 1
---

> Custom note between frontmatter and managed block

{Constants.ManagedBlockHeader}
# Official Instructions
Old version
{Constants.ManagedBlockFooter}

# Custom Footer Note
Remember edge cases.";

            File.WriteAllText(existingFile, initialSkill);

            var updated = SkillsInstaller.PrepareSkillContent(rawEmbedded, existingFile);

            Assert.Contains("> Custom note between frontmatter and managed block", updated);
            Assert.Contains("# Custom Footer Note", updated);
            Assert.Contains("Do this.", updated);
            Assert.DoesNotContain("Old version", updated);
            Assert.DoesNotContain("\r\n", updated);
        }
        finally
        {
            if (File.Exists(existingFile)) File.Delete(existingFile);
        }
    }

    [Fact]
    public void PrepareSkillContent_WhenExistingFileLacksManagedBlock_AppendsUserBody()
    {
        var rawEmbedded = "---\nname: test-skill\nversion: 1\n---\n# Official Instructions\nNew instructions.\n";

        var existingFile = Path.Combine(Path.GetTempPath(), "skill-unmanaged-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            var existingContent = @"---
name: test-skill
---
My existing custom instructions written before NCF.";

            File.WriteAllText(existingFile, existingContent);

            var updated = SkillsInstaller.PrepareSkillContent(rawEmbedded, existingFile);

            Assert.Contains(Constants.ManagedBlockHeader, updated);
            Assert.Contains("New instructions.", updated);
            Assert.Contains(Constants.ManagedBlockFooter, updated);
            Assert.Contains("My existing custom instructions written before NCF.", updated);
            Assert.DoesNotContain("\r\n", updated);
        }
        finally
        {
            if (File.Exists(existingFile)) File.Delete(existingFile);
        }
    }

    [Fact]
    public void GetGlobalSkillDirectories_ReturnsGeminiAndAgentsSkillDirectories()
    {
        var fakeProfile = Path.Combine(Path.GetTempPath(), "ncf-profile-" + Guid.NewGuid().ToString("N"));
        var dirs = SkillsInstaller.GetGlobalSkillDirectories(fakeProfile);

        Assert.Equal(2, dirs.Count);
        Assert.Equal(Path.Combine(fakeProfile, ".gemini", "config", "skills"), dirs[0]);
        Assert.Equal(Path.Combine(fakeProfile, ".agents", "skills"), dirs[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetGlobalSkillDirectories_ThrowsInvalidOperation_WhenProfileIsBlank(string profilePath)
    {
        Assert.Throws<InvalidOperationException>(() => SkillsInstaller.GetGlobalSkillDirectories(profilePath));
    }

    [Fact]
    public void GetEmbeddedSkillNames_AndContent_ReturnAllRequiredSkills()
    {
        var names = SkillsInstaller.GetEmbeddedSkillNames();

        foreach (var required in SkillsInstaller.GetRequiredSkillNames())
        {
            Assert.Contains(required, names);
            var content = SkillsInstaller.GetEmbeddedSkillContent(required);
            Assert.NotNull(content);
            Assert.Contains($"name: {required}", content);
        }

        Assert.Null(SkillsInstaller.GetEmbeddedSkillContent("nonexistent-skill"));
        Assert.Null(SkillsInstaller.GetEmbeddedSkillContent("   "));
    }

    [Fact]
    public void InstallGlobalSkills_InstallsSkillsAndAssetsToBothDirectories_AndIsIdempotent()
    {
        var fakeProfile = Path.Combine(Path.GetTempPath(), "ncf-global-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeProfile);

        try
        {
            var logs = new List<string>();
            var installedDirs = SkillsInstaller.InstallGlobalSkills(fakeProfile, dryRun: false, logs.Add);

            Assert.Equal(2, installedDirs.Count);
            foreach (var baseDir in installedDirs)
            {
                foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
                {
                    var skillFile = Path.Combine(baseDir, skillName, "SKILL.md");
                    Assert.True(File.Exists(skillFile), $"Missing global skill file: {skillFile}");

                    var content = File.ReadAllText(skillFile);
                    Assert.Contains($"name: {skillName}", content);
                    Assert.Contains(Constants.ManagedBlockHeader, content);
                    Assert.Contains(Constants.ManagedBlockFooter, content);
                }

                var assetFile = Path.Combine(baseDir, "ncf-backlog-migrator", "assets", "TASK_TEMPLATE.md");
                Assert.True(File.Exists(assetFile), $"Missing global skill asset: {assetFile}");
            }

            // Preserve user customizations outside managed block on re-install
            var customSkillFile = Path.Combine(installedDirs[0], "ncf-backlog", "SKILL.md");
            var customNote = "\n# Global Custom Note\nKeep global rule.\n";
            File.AppendAllText(customSkillFile, customNote);

            SkillsInstaller.InstallGlobalSkills(fakeProfile);

            var updated = File.ReadAllText(customSkillFile);
            Assert.Contains("# Global Custom Note", updated);
            Assert.Contains(Constants.ManagedBlockHeader, updated);
        }
        finally
        {
            Directory.Delete(fakeProfile, true);
        }
    }

    [Fact]
    public void InstallGlobalSkills_DryRun_DoesNotWriteFiles()
    {
        var fakeProfile = Path.Combine(Path.GetTempPath(), "ncf-global-dryrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeProfile);

        try
        {
            var logs = new List<string>();
            var targetDirs = SkillsInstaller.InstallGlobalSkills(fakeProfile, dryRun: true, logs.Add);

            Assert.Equal(2, targetDirs.Count);
            Assert.All(targetDirs, dir => Assert.False(Directory.Exists(dir)));
            Assert.Contains(logs, l => l.StartsWith("[dry-run] Would write ", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fakeProfile, true);
        }
    }

    [Fact]
    public void InstallSkillsToDirectory_InstallsDirectlyIntoCustomBaseDirectory()
    {
        var customDir = Path.Combine(Path.GetTempPath(), "ncf-custom-skills-" + Guid.NewGuid().ToString("N"));

        try
        {
            SkillsInstaller.InstallSkillsToDirectory(customDir);

            foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
            {
                Assert.True(File.Exists(Path.Combine(customDir, skillName, "SKILL.md")));
            }
            Assert.True(File.Exists(Path.Combine(customDir, "ncf-backlog-migrator", "assets", "TASK_TEMPLATE.md")));
        }
        finally
        {
            if (Directory.Exists(customDir))
            {
                Directory.Delete(customDir, true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InstallSkills_ThrowsArgumentException_WhenWorkspaceRootIsBlank(string workspaceRoot)
    {
        Assert.ThrowsAny<ArgumentException>(() => SkillsInstaller.InstallSkills(workspaceRoot));
    }

    [Theory]
    [InlineData("ncf-backlog/assets")]
    [InlineData("../ncf-backlog")]
    [InlineData("ncf-backlog\\SKILL.md")]
    public void GetEmbeddedSkillContent_RejectsPathSeparators(string skillName)
    {
        Assert.Null(SkillsInstaller.GetEmbeddedSkillContent(skillName));
    }

    [Fact]
    public void GetEmbeddedSkillVersion_TrimsSkillName()
    {
        var canonicalVersion = SkillsInstaller.GetEmbeddedSkillVersion("ncf-backlog-migrator");
        var paddedVersion = SkillsInstaller.GetEmbeddedSkillVersion(" ncf-backlog-migrator ");

        Assert.True(canonicalVersion > 1);
        Assert.Equal(canonicalVersion, paddedVersion);
    }
}
