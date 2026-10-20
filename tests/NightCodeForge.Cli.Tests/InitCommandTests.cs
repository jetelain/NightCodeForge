namespace NightCodeForge.Cli.Tests;

using Moq;
using NightCodeForge.Cli.Commands;
using NightCodeForge.Cli.Common;
using NightCodeForge.Core.Interfaces;

public class InitCommandTests
{
    private static Mock<IProcessRunner> CreateGitProcessRunner(string branch = "main")
    {
        var mock = new Mock<IProcessRunner>();
        mock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("is-inside-work-tree")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "true\n", ""));

        mock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("branch --show-current")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, $"{branch}\n", ""));

        mock.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet" || (s.Arguments != null && s.Arguments.Contains("format"))), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Formatted files\n", ""));

        return mock;
    }

    [Fact]
    public async Task InitCommand_FailsFast_WhenDirectoryDoesNotExist()
    {
        var runner = new Mock<IProcessRunner>();
        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid()) };

        var exitCode = await cmd.ExecuteAsync(options);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task InitCommand_FailsFast_WhenNotGitRepository()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-notgit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(128, "", "fatal: not a git repository"));

        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InitCommand_FailsFast_WhenStackUndetectableAndNoOptionProvided()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-nostack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var runner = CreateGitProcessRunner();
        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InitCommand_FailsFast_WhenInvalidStackProvided()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-badstack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var runner = CreateGitProcessRunner();
        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = tempDir, Stack = "unknown_lang" };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InitCommand_DryRun_DoesNotMutateFileSystem()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-dryrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        File.WriteAllText(Path.Combine(tempDir, "App.sln"), "");

        var runner = CreateGitProcessRunner();
        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = tempDir, DryRun = true };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(0, exitCode);

            // Files must not have been created
            Assert.False(File.Exists(Path.Combine(tempDir, ".gitignore")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".editorconfig")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".agents", "skills")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".nightcodeforge", "config.json")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InitCommand_Success_InitializesRepo_AndIsIdempotent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-init-success-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        File.WriteAllText(Path.Combine(tempDir, "Sample.csproj"), "<Project />");

        var runner = CreateGitProcessRunner("main");
        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(0, exitCode);

            // Verify .gitignore
            var gitignorePath = Path.Combine(tempDir, ".gitignore");
            Assert.True(File.Exists(gitignorePath));
            var gitignore = File.ReadAllText(gitignorePath);
            Assert.Contains(Constants.ManagedBlockHeader, gitignore);
            Assert.Contains("bin/", gitignore);

            // Verify .editorconfig
            var editorconfigPath = Path.Combine(tempDir, ".editorconfig");
            Assert.True(File.Exists(editorconfigPath));
            var editorconfig = File.ReadAllText(editorconfigPath);
            Assert.Contains("end_of_line = lf", editorconfig);

            // Verify skills
            var skillDir = Path.Combine(tempDir, ".agents", "skills", "ncf-backlog-migrator");
            Assert.True(Directory.Exists(skillDir));
            Assert.True(File.Exists(Path.Combine(skillDir, "SKILL.md")));

            // Verify config.json
            var configPath = Path.Combine(tempDir, ".nightcodeforge", "config.json");
            Assert.True(File.Exists(configPath));
            var configJson = File.ReadAllText(configPath);
            Assert.Contains("\"codeStack\": \"dotnet\"", configJson);
            Assert.Contains("\"baseBranch\": \"main\"", configJson);

            // Re-run init (idempotency check)
            var exitCode2 = await cmd.ExecuteAsync(options);
            Assert.Equal(0, exitCode2);

            Assert.Equal(gitignore, File.ReadAllText(gitignorePath));
            Assert.Equal(editorconfig, File.ReadAllText(editorconfigPath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InitCommand_WithExplicitStack_OverridesDetection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-init-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var runner = CreateGitProcessRunner("master");
        var cmd = new InitCommand(runner.Object);
        var options = new InitOptions { TargetPath = tempDir, Stack = "python", SkipFormat = true };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(0, exitCode);

            var configPath = Path.Combine(tempDir, ".nightcodeforge", "config.json");
            var configJson = File.ReadAllText(configPath);
            Assert.Contains("\"codeStack\": \"python\"", configJson);
            Assert.Contains("\"testCommand\": \"pytest\"", configJson);
            Assert.Contains("\"baseBranch\": \"master\"", configJson);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
