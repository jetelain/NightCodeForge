namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Commands;
using NightCodeForge.Cli.Services;

public class SkillsCommandTests
{
    [Fact]
    public async Task ExecuteAsync_GlobalInstall_InstallsToGeminiAndAgentsDirectories()
    {
        var fakeProfile = Path.Combine(Path.GetTempPath(), "ncf-cmd-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeProfile);

        try
        {
            var cmd = new SkillsCommand(() => fakeProfile);
            var logs = new List<string>();
            var options = new SkillsOptions { Action = SkillsAction.Install, Global = true };

            var exitCode = await cmd.ExecuteAsync(options, logs.Add);

            Assert.Equal(0, exitCode);
            var geminiSkill = Path.Combine(fakeProfile, ".gemini", "config", "skills", "ncf-backlog", "SKILL.md");
            var agentsSkill = Path.Combine(fakeProfile, ".agents", "skills", "ncf-backlog", "SKILL.md");
            var geminiAsset = Path.Combine(fakeProfile, ".gemini", "config", "skills", "ncf-backlog-migrator", "assets", "TASK_TEMPLATE.md");
            var agentsAsset = Path.Combine(fakeProfile, ".agents", "skills", "ncf-backlog-migrator", "assets", "TASK_TEMPLATE.md");

            Assert.True(File.Exists(geminiSkill));
            Assert.True(File.Exists(agentsSkill));
            Assert.True(File.Exists(geminiAsset));
            Assert.True(File.Exists(agentsAsset));
            Assert.Contains(logs, l => l.Contains(geminiSkill, StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains(agentsSkill, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fakeProfile, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_GlobalDryRun_DoesNotWriteToDisk()
    {
        var fakeProfile = Path.Combine(Path.GetTempPath(), "ncf-cmd-global-dry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeProfile);

        try
        {
            var cmd = new SkillsCommand(() => fakeProfile);
            var logs = new List<string>();
            var options = new SkillsOptions { Action = SkillsAction.Install, Global = true, DryRun = true };

            var exitCode = await cmd.ExecuteAsync(options, logs.Add);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(fakeProfile, ".gemini")));
            Assert.False(Directory.Exists(Path.Combine(fakeProfile, ".agents")));
            Assert.Contains(logs, l => l.StartsWith("[dry-run]", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(fakeProfile, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CustomDirectory_CreatesAndInstallsDirectlyIntoDirectory()
    {
        var customDir = Path.Combine(Path.GetTempPath(), "ncf-cmd-custom-" + Guid.NewGuid().ToString("N"), "nested");

        try
        {
            var cmd = new SkillsCommand(() => throw new InvalidOperationException("UserProfile should not be queried."));
            var options = new SkillsOptions { Action = SkillsAction.Install, CustomDirectory = customDir };

            var exitCode = await cmd.ExecuteAsync(options, _ => { });

            Assert.Equal(0, exitCode);
            foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
            {
                Assert.True(File.Exists(Path.Combine(customDir, skillName, "SKILL.md")));
            }
        }
        finally
        {
            var rootDir = Path.GetDirectoryName(customDir)!;
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WorkspaceInstall_InstallsIntoAgentsSkills()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), "ncf-cmd-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);

        try
        {
            var cmd = new SkillsCommand(() => throw new InvalidOperationException("UserProfile should not be queried."));
            var options = new SkillsOptions { Action = SkillsAction.Install, TargetPath = workspaceDir };

            var exitCode = await cmd.ExecuteAsync(options, _ => { });

            Assert.Equal(0, exitCode);
            foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
            {
                Assert.True(File.Exists(Path.Combine(workspaceDir, ".agents", "skills", skillName, "SKILL.md")));
            }
        }
        finally
        {
            Directory.Delete(workspaceDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WorkspaceInstall_MissingDirectory_ReturnsExitCode2()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "ncf-cmd-missing-" + Guid.NewGuid().ToString("N"));
        var cmd = new SkillsCommand();
        var logs = new List<string>();
        var options = new SkillsOptions { Action = SkillsAction.Install, TargetPath = missingDir };

        var exitCode = await cmd.ExecuteAsync(options, logs.Add);

        Assert.Equal(2, exitCode);
        Assert.Contains(logs, l => l.Contains("Directory not found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_List_OutputsAllEmbeddedSkills()
    {
        var cmd = new SkillsCommand();
        var logs = new List<string>();

        var exitCode = await cmd.ExecuteAsync(new SkillsOptions { Action = SkillsAction.List }, logs.Add);

        Assert.Equal(0, exitCode);
        foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
        {
            Assert.Contains(logs, l => l.StartsWith($"{skillName} (v", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ExecuteAsync_Show_WithValidName_OutputsSkillContent()
    {
        var cmd = new SkillsCommand();
        var logs = new List<string>();

        var exitCode = await cmd.ExecuteAsync(new SkillsOptions { Action = SkillsAction.Show, SkillName = "ncf-backlog" }, logs.Add);

        Assert.Equal(0, exitCode);
        var output = string.Join("\n", logs);
        Assert.Contains("name: ncf-backlog", output);
        Assert.Contains("# Backlog Management Skill", output);
    }

    [Fact]
    public async Task ExecuteAsync_Show_WithoutName_OutputsAllSkillsWithHeaders()
    {
        var cmd = new SkillsCommand();
        var logs = new List<string>();

        var exitCode = await cmd.ExecuteAsync(new SkillsOptions { Action = SkillsAction.Show }, logs.Add);

        Assert.Equal(0, exitCode);
        var output = string.Join("\n", logs);
        foreach (var skillName in SkillsInstaller.GetRequiredSkillNames())
        {
            Assert.Contains($"=== {skillName} ===", output);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Show_WithUnknownName_ReturnsExitCode2()
    {
        var cmd = new SkillsCommand();
        var logs = new List<string>();

        var exitCode = await cmd.ExecuteAsync(new SkillsOptions { Action = SkillsAction.Show, SkillName = "does-not-exist" }, logs.Add);

        Assert.Equal(2, exitCode);
        Assert.Contains(logs, l => l.Contains("Unknown skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_GlobalAndCustomDirectoryCombined_ReturnsExitCode2()
    {
        var cmd = new SkillsCommand();
        var logs = new List<string>();
        var options = new SkillsOptions
        {
            Action = SkillsAction.Install,
            Global = true,
            CustomDirectory = Path.Combine(Path.GetTempPath(), "ncf-cmd-conflict-" + Guid.NewGuid().ToString("N"))
        };

        var exitCode = await cmd.ExecuteAsync(options, logs.Add);

        Assert.Equal(2, exitCode);
        Assert.Contains(logs, l => l.Contains("Cannot combine '--global' with '--dir'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_CustomDirectoryPointsToExistingFile_ReturnsExitCode2()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "ncf-cmd-file-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tempFile, "not a directory");

        try
        {
            var cmd = new SkillsCommand();
            var logs = new List<string>();
            var options = new SkillsOptions
            {
                Action = SkillsAction.Install,
                CustomDirectory = tempFile
            };

            var exitCode = await cmd.ExecuteAsync(options, logs.Add);

            Assert.Equal(2, exitCode);
            Assert.Contains(logs, l => l.Contains("existing file", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
