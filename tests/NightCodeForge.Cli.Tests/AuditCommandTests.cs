namespace NightCodeForge.Cli.Tests;

using Moq;
using NightCodeForge.Cli.Commands;
using NightCodeForge.Cli.Common;
using NightCodeForge.Cli.Services;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

public class AuditCommandTests
{
    private static Mock<IProcessRunner> CreateCleanProcessRunner()
    {
        var runner = new Mock<IProcessRunner>();

        // Git checks
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("is-inside-work-tree")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "true\n", ""));

        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("rev-parse --verify HEAD")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "abc123commit\n", ""));

        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("branch --show-current")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "main\n", ""));

        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("status --porcelain")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "", ""));

        // Build check
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("build")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Build succeeded.\n", ""));

        // Test check
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("test")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Passed! - Failed: 0, Passed: 12, Total: 12\n", ""));

        return runner;
    }

    private static void SetupHealthyWorkspace(string rootDir)
    {
        Directory.CreateDirectory(Path.Combine(rootDir, ".git"));
        File.WriteAllText(Path.Combine(rootDir, ".gitignore"), GitignorePreparer.Prepare(null));
        File.WriteAllText(Path.Combine(rootDir, ".editorconfig"), EditorConfigPreparer.Prepare(null));
        SkillsInstaller.InstallSkills(rootDir);
        ConfigBootstrapper.BootstrapConfig(rootDir, CodeStackPresets.DotNet, "main");
        File.WriteAllText(Path.Combine(rootDir, "App.sln"), "");
        File.WriteAllText(Path.Combine(rootDir, "App.cs"), "public class App { }");
    }

    [Fact]
    public async Task AuditCommand_ReturnsBlocker_WhenDirectoryNotFound()
    {
        var runner = new Mock<IProcessRunner>();
        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()) };

        var exitCode = await cmd.ExecuteAsync(options);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task AuditCommand_ReturnsBlocker_WhenWorkingTreeIsDirty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-dirty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        var runner = CreateCleanProcessRunner();
        // Dirty git status
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("status --porcelain")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, " M src/File.cs\n?? newfile.txt\n", ""));

        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode); // Blocker

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            Assert.True(File.Exists(reportPath));
            var report = File.ReadAllText(reportPath);
            Assert.Contains("🔴 Blockers", report);
            Assert.Contains("Dirty working tree", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReturnsBlocker_WhenBuildFails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-buildfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        var runner = CreateCleanProcessRunner();
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("build")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, "", "CS1002: ; expected"));

        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode); // Blocker

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("Build Determinism", report);
            Assert.Contains("CS1002", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReturnsBlocker_WhenFastTestsFail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-testfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        var runner = CreateCleanProcessRunner();
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("test")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, "", "Failed: 2, Passed: 10\nAssert.Equal() Failure"));

        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode); // Blocker

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("Test Fast Feedback", report);
            Assert.Contains("Assert.Equal() Failure", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReturnsWarning_WhenIsolationHeuristicMatchesHardcodedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        // Add code with hardcoded machine path
        File.WriteAllText(Path.Combine(tempDir, "Config.cs"), @"
public class MyConfig {
    public string Path = ""C:\\Users\\JohnDoe\\data"";
}");

        var runner = CreateCleanProcessRunner();
        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(1, exitCode); // Warning

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("🟡 Warnings", report);
            Assert.Contains("Isolation Readiness", report);
            Assert.Contains("Hardcoded path", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReturnsHealthy_WhenAllPillarsPass()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-healthy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        var runner = CreateCleanProcessRunner();
        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(0, exitCode); // Healthy

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            Assert.True(File.Exists(reportPath));
            var report = File.ReadAllText(reportPath);
            Assert.Contains("🟢 Healthy", report);
            Assert.Contains("| **Git Health** | 🟢 |", report);
            Assert.Contains("| **Build Determinism** | 🟢 |", report);
            Assert.Contains("| **Test Fast Feedback** | 🟢 |", report);
            Assert.Contains("| **Isolation Readiness** | 🟢 |", report);
            Assert.Contains("| **NCF Configuration** | 🟢 |", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0, Duration: 5 ms")]
    [InlineData("Réussi!  - échec :     0, réussite :     0, ignorée(s) :     0, total :     0, durée : 5 ms")]
    [InlineData("running 0 tests\ntest result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out")]
    [InlineData("collected 0 items\nno tests ran")]
    [InlineData("?   \tmygo\t[no test files]")]
    public async Task AuditCommand_ReturnsWarning_WhenZeroTestsExecuted(string testOutput)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-zerotests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        var runner = CreateCleanProcessRunner();
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("test")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, testOutput, ""));

        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(1, exitCode); // Warning

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("Test Fast Feedback", report);
            Assert.Contains("0 fast tests executed", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_IgnoresCommentsAndSchemas_InIsolationReadinessScan()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-comments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        // Add file with URLs in comments and XML schema URLs
        File.WriteAllText(Path.Combine(tempDir, "SampleTests.cs"), @"
// See documentation at https://api.external.com/docs
// Path on build agent: C:\Users\builder\repo
/*
   Another comment with http://somewebsite.com
*/
public class SampleTests {
    public const string Schema = ""http://schemas.microsoft.com/developer/msbuild/2003"";
}");

        var runner = CreateCleanProcessRunner();
        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(0, exitCode); // Healthy: comments & schemas are not isolation violations

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("| **Isolation Readiness** | 🟢 |", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReturnsBlocker_WhenBranchDoesNotMatchConfiguredBaseBranch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-branchmismatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        // Configure baseBranch to develop
        ConfigBootstrapper.BootstrapConfig(tempDir, CodeStackPresets.DotNet, "develop");

        var runner = CreateCleanProcessRunner();
        // Current branch is main, but config requires develop
        runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.Arguments != null && s.Arguments.Contains("branch --show-current")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "main\n", ""));

        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(2, exitCode); // Blocker

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("Git Health", report);
            Assert.Contains("configured base branch 'develop'", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReturnsWarning_WhenGitignoreLacksManagedBlock()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-nomanagedblock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        // Clobber .gitignore without managed block
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "bin/\nobj/\n");

        var runner = CreateCleanProcessRunner();
        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            Assert.Equal(1, exitCode); // Warning

            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);
            Assert.Contains("NCF Configuration", report);
            Assert.Contains(".gitignore is missing or lacks the NightCodeForge managed block", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AuditCommand_ReportsSkillsConfigAndCompanionTools_InPillar5()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-audit-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        SetupHealthyWorkspace(tempDir);

        // Configure skills in config.json
        var configJson = @"
{
  ""codeStack"": ""dotnet"",
  ""skills"": {
    ""enabled"": true,
    ""disabledSkills"": [""codecondenser""]
  }
}";
        await File.WriteAllTextAsync(Path.Combine(tempDir, ".nightcodeforge", "config.json"), configJson);

        var runner = CreateCleanProcessRunner();
        var cmd = new AuditCommand(runner.Object);
        var options = new AuditOptions { TargetPath = tempDir };

        try
        {
            var exitCode = await cmd.ExecuteAsync(options);
            var reportPath = Path.Combine(tempDir, ".nightcodeforge", "audit-report.md");
            var report = File.ReadAllText(reportPath);

            Assert.Contains("NCF Configuration", report);
            Assert.Contains("Skills excluded in config.json: codecondenser", report);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
