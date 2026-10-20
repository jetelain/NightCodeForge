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

            // User adds a custom note at the bottom of ncf-ideation/SKILL.md
            var ideationFile = Path.Combine(tempDir, ".agents", "skills", "ncf-ideation", "SKILL.md");
            var originalContent = File.ReadAllText(ideationFile);
            var customAppendix = "\n# User Custom Rules\nAlways double check DB migrations.\n";
            File.AppendAllText(ideationFile, customAppendix);

            // Re-run install (like a second ncf init)
            SkillsInstaller.InstallSkills(tempDir);

            var updatedContent = File.ReadAllText(ideationFile);
            Assert.Contains(customAppendix.Trim(), updatedContent);
            Assert.Contains(Constants.ManagedBlockHeader, updatedContent);
            Assert.Contains(Constants.ManagedBlockFooter, updatedContent);

            // Running a 3rd time is identical
            SkillsInstaller.InstallSkills(tempDir);
            var recheckContent = File.ReadAllText(ideationFile);
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
}
