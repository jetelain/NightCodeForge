using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class BenchmarkWorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceRepoDir;
    private readonly string _benchRootDir;
    private readonly GitCliRunner _gitRunner;
    private readonly BenchmarkWorkspaceManager _manager;

    public BenchmarkWorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-wks-{Guid.NewGuid():N}");
        _sourceRepoDir = Path.Combine(_tempDir, "upstream-repo");
        _benchRootDir = Path.Combine(_tempDir, "bench-root");
        Directory.CreateDirectory(_sourceRepoDir);
        Directory.CreateDirectory(_benchRootDir);

        var wslMetadata = new Mock<IWslMetadataService>();
        wslMetadata.Setup(m => m.GetDefaultDistributionName()).Returns("Ubuntu");
        wslMetadata.Setup(m => m.GetDefaultUserName()).Returns("user");

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var pathResolver = new WorkspacePathResolver(
            wslMetadata.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()));

        _gitRunner = new GitCliRunner(
            processRunner,
            pathResolver,
            wslMetadata.Object,
            NullLogger<GitCliRunner>.Instance);

        _manager = new BenchmarkWorkspaceManager(
            _gitRunner,
            Options.Create(new BenchmarkWorkspaceOptions { RootDirectory = _benchRootDir }),
            NullLogger<BenchmarkWorkspaceManager>.Instance);

        RunGit(_sourceRepoDir, "init -b main");
        RunGit(_sourceRepoDir, "config user.name \"Test User\"");
        RunGit(_sourceRepoDir, "config user.email \"test@example.com\"");
        RunGit(_sourceRepoDir, "config commit.gpgsign false");
    }

    public void Dispose()
    {
        DeleteDirectorySafe(_tempDir);
    }

    [Fact]
    public async Task ProvisionAsync_Implementation_LeavesZeroPostBaseCommitHistoryTagsOrBranches()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "Calc.cs"), "public class Calc { public int Add(int a, int b) => a - b; }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Initial buggy commit\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        File.WriteAllText(Path.Combine(_sourceRepoDir, "Calc.cs"), "public class Calc { public int Add(int a, int b) => a + b; }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Future gold fix commit\"");
        RunGit(_sourceRepoDir, "tag v2.0-future-fix");
        RunGit(_sourceRepoDir, "branch future-solution-branch");

        var instance = new BenchmarkInstance
        {
            InstanceId = "calc__001",
            Repo = _sourceRepoDir,
            BaseCommit = baseCommit,
            ProblemStatement = "Fix Add"
        };

        var handle = await _manager.ProvisionAsync(instance, BenchmarkProblemCategory.Implementation);

        Assert.Equal(baseCommit, handle.BaseCommit);
        var logAll = RunGit(handle.WorkspacePath, "log --all --oneline");
        var tags = RunGit(handle.WorkspacePath, "tag -l");
        var branches = RunGit(handle.WorkspacePath, "branch -a");
        var remotes = RunGit(handle.WorkspacePath, "remote");

        Assert.Contains("Initial buggy commit", logAll);
        Assert.DoesNotContain("Future gold fix commit", logAll);
        Assert.True(string.IsNullOrWhiteSpace(tags));
        Assert.DoesNotContain("future-solution-branch", branches);
        Assert.True(string.IsNullOrWhiteSpace(remotes));
        Assert.Contains("a - b", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "Calc.cs")));
    }

    [Fact]
    public async Task ProvisionAsync_CodeReview_MaterializesCandidateBranchAndDiffContext()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "App.cs"), "class App { int V() => 1; }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base commit\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        const string candidatePatch = """
            diff --git a/App.cs b/App.cs
            index 1111111..2222222 100644
            --- a/App.cs
            +++ b/App.cs
            @@ -1 +1 @@
            -class App { int V() => 1; }
            +class App { int V() => 2; }
            """;

        var instance = new BenchmarkInstance
        {
            InstanceId = "cr__001",
            Category = BenchmarkProblemCategory.CodeReview,
            Repo = _sourceRepoDir,
            BaseCommit = baseCommit,
            ProblemStatement = "Review change",
            CandidatePatch = candidatePatch,
            ExpectedVerdict = "MinorFix"
        };

        var handle = await _manager.ProvisionAsync(instance, BenchmarkProblemCategory.CodeReview);

        Assert.Equal(BenchmarkBranchNames.BaseBranch, handle.BaseBranch);
        Assert.Equal(BenchmarkBranchNames.CandidateBranch, handle.FeatureBranch);
        Assert.Contains("App.cs", handle.ReviewDiffStat);
        Assert.Contains("+class App { int V() => 2; }", handle.ReviewDiff);
        Assert.Contains("=> 2", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "App.cs")));
    }

    [Fact]
    public async Task ProvisionAsync_TaskReview_MaterializesMarkdownWithFrontmatterOnlyWhenMissing()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "README.md"), "# Repo\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var plainInstance = new BenchmarkInstance
        {
            InstanceId = "tr-plain",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = _sourceRepoDir,
            BaseCommit = baseCommit,
            ProblemStatement = "Add feature",
            TaskSpecification = "# Feature Spec\nImplement X.",
            ExpectedVerdict = "Ready"
        };

        var plainHandle = await _manager.ProvisionAsync(plainInstance, BenchmarkProblemCategory.TaskReview);
        Assert.Equal("docs/tasks/tr-plain.md", plainHandle.TaskMarkdownPath);
        var plainOnDisk = await File.ReadAllTextAsync(Path.Combine(plainHandle.WorkspacePath, "docs", "tasks", "tr-plain.md"));
        Assert.StartsWith("---", plainOnDisk);
        Assert.Contains("id: 1", plainOnDisk);
        Assert.Contains("status: Open", plainOnDisk);
        Assert.Contains("# Feature Spec", plainOnDisk);

        var frontmatterInstance = new BenchmarkInstance
        {
            InstanceId = "tr-fm",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = _sourceRepoDir,
            BaseCommit = baseCommit,
            ProblemStatement = "Add feature",
            TaskSpecification = "---\nid: 7\nstatus: Open\n---\n\n# Existing Spec",
            ExpectedVerdict = "Ready"
        };

        var fmHandle = await _manager.ProvisionAsync(frontmatterInstance, BenchmarkProblemCategory.TaskReview);
        var fmOnDisk = await File.ReadAllTextAsync(Path.Combine(fmHandle.WorkspacePath, "docs", "tasks", "tr-fm.md"));
        Assert.Contains("id: 7", fmOnDisk);
        Assert.DoesNotContain("id: 1", fmOnDisk);
    }

    [Fact]
    public async Task ExtractPatchAsync_StagesNewSourceFilesAndExcludesBuildArtifacts()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "Main.cs"), "class Main {}\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "patch-001",
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Add file"
            },
            BenchmarkProblemCategory.Implementation);

        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "NewService.cs"), "public class NewService {}\n");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, "bin", "Debug"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "bin", "Debug", "out.dll"), "binary");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, "obj"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "obj", "cache.txt"), "obj");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, "TestResults"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "TestResults", "results.trx"), "<trx/>");

        var patch = await _manager.ExtractPatchAsync(handle.WorkspacePath, handle.BaseCommit);

        Assert.Contains("NewService.cs", patch);
        Assert.DoesNotContain("bin/Debug/out.dll", patch);
        Assert.DoesNotContain("obj/cache.txt", patch);
        Assert.DoesNotContain("results.trx", patch);
    }

    [Fact]
    public async Task ProvisionAsync_UnknownBaseCommit_FailsFastWithBenchmarkWorkspaceException()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "Main.cs"), "class Main {}\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");

        var instance = new BenchmarkInstance
        {
            InstanceId = "bad-commit",
            Repo = _sourceRepoDir,
            BaseCommit = "0000000000000000000000000000000000000000",
            ProblemStatement = "Unknown commit"
        };

        await Assert.ThrowsAsync<BenchmarkWorkspaceException>(() =>
            _manager.ProvisionAsync(instance, BenchmarkProblemCategory.Implementation));
    }

    [Fact]
    public async Task ProvisionAsync_LongInstanceId_PreservesUniqueSuffixAcrossProvisions()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "Main.cs"), "class Main {}\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var longId = new string('a', 80) + "_suffix";
        var instance = new BenchmarkInstance
        {
            InstanceId = longId,
            Repo = _sourceRepoDir,
            BaseCommit = baseCommit,
            ProblemStatement = "Test long id"
        };

        var handle1 = await _manager.ProvisionAsync(instance, BenchmarkProblemCategory.Implementation);
        var handle2 = await _manager.ProvisionAsync(instance, BenchmarkProblemCategory.Implementation);

        Assert.NotEqual(handle1.WorkspacePath, handle2.WorkspacePath);
        Assert.True(Directory.Exists(handle1.WorkspacePath));
        Assert.True(Directory.Exists(handle2.WorkspacePath));
    }

    [Fact]
    public async Task ProvisionAsync_AbbreviatedBaseCommit_ResolvesAndProvisionsCleanly()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "Main.cs"), "class Main { int X => 1; }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base commit\"");
        var fullCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();
        var shortCommit = fullCommit[..8];

        var instance = new BenchmarkInstance
        {
            InstanceId = "short-sha-1",
            Repo = _sourceRepoDir,
            BaseCommit = shortCommit,
            ProblemStatement = "Short SHA test"
        };

        var handle = await _manager.ProvisionAsync(instance, BenchmarkProblemCategory.Implementation);

        Assert.Equal(fullCommit, handle.BaseCommit);
        Assert.True(File.Exists(Path.Combine(handle.WorkspacePath, "Main.cs")));
    }

    [Fact]
    public async Task CodeReview_ExtractPatchFromCandidateCommit_CapturesCommittedMinorFix()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "App.cs"), "class App { int V() => 1; }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        const string candidatePatch = """
            diff --git a/App.cs b/App.cs
            index 1111111..2222222 100644
            --- a/App.cs
            +++ b/App.cs
            @@ -1 +1 @@
            -class App { int V() => 1; }
            +class App { int V() => 2; }
            """;

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "cr-commit-001",
                Category = BenchmarkProblemCategory.CodeReview,
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Review",
                CandidatePatch = candidatePatch,
                ExpectedVerdict = "MinorFix"
            },
            BenchmarkProblemCategory.CodeReview);

        Assert.False(string.IsNullOrWhiteSpace(handle.CandidateCommit));

        // Agent applies and commits a Tier-1 polish fix
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "App.cs"), "class App { int V() => 3; }\n");
        RunGit(handle.WorkspacePath, "add -A");
        RunGit(handle.WorkspacePath, "commit -m \"Tier-1 minor fix\"");

        var patch = await _manager.ExtractPatchAsync(handle.WorkspacePath, handle.CandidateCommit!);
        Assert.Contains("-class App { int V() => 2; }", patch);
        Assert.Contains("+class App { int V() => 3; }", patch);
    }

    [Fact]
    public async Task TaskReview_GetModifiedFilesWithBaseCommit_DetectsCommittedOutOfScopeEdits()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "Src.cs"), "class Src {}\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "tr-commit-001",
                Category = BenchmarkProblemCategory.TaskReview,
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Review spec",
                TaskSpecification = "# Spec\nInitial.",
                ExpectedVerdict = "Ready"
            },
            BenchmarkProblemCategory.TaskReview);

        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "Src.cs"), "class Src { int Leaked = 1; }\n");
        RunGit(handle.WorkspacePath, "add Src.cs");
        RunGit(handle.WorkspacePath, "commit -m \"Out-of-scope edit\"");

        var modified = await _manager.GetModifiedFilesAsync(handle.WorkspacePath, handle.BaseCommit);
        Assert.Contains("Src.cs", modified);
    }

    [Fact]
    public void EnsureTaskFrontmatter_EscapesBackslashesAndQuotes_AndHandlesLeadingNewlinesBeforeFence()
    {
        var instance = new BenchmarkInstance
        {
            InstanceId = "yaml-escape",
            Repo = "a/b",
            BaseCommit = "c1",
            ProblemStatement = "Fix C:\\path\\\"quoted\"\\issue\twith tabs"
        };

        var generated = BenchmarkWorkspaceManager.EnsureTaskFrontmatter("Body text", instance);
        var parts = generated.Split(["---"], StringSplitOptions.RemoveEmptyEntries);
        var deserializer = NightCodeForge.Core.Services.TaskMarkdownSyncService.CreateYamlDeserializer();
        var fm = deserializer.Deserialize<NightCodeForge.Core.Services.TaskFrontmatter>(parts[0]);

        Assert.Equal(1, fm.Id);
        Assert.Equal("Open", fm.Status);
        Assert.Contains("C:\\path\\\"quoted\"\\issue", fm.Title);

        var leadingNewlineSpec = "\n\n---\nid: 42\nstatus: Open\n---\n\n# Spec";
        var preserved = BenchmarkWorkspaceManager.EnsureTaskFrontmatter(leadingNewlineSpec, instance);
        Assert.StartsWith("---\n", preserved);
        Assert.Contains("id: 42", preserved);
    }

    [Fact]
    public async Task ProvisionAsync_RemovesFetchHeadFileFromGitDirectory()
    {
        File.WriteAllText(Path.Combine(_sourceRepoDir, "File.cs"), "class F {}\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "fetch-head-check",
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Check FETCH_HEAD removal"
            },
            BenchmarkProblemCategory.Implementation);

        Assert.False(File.Exists(Path.Combine(handle.WorkspacePath, ".git", "FETCH_HEAD")));
    }

    [Fact]
    public async Task ApplyPatchAsync_PreservesCrlfPatchesAndSupportsLfFallback()
    {
        RunGit(_sourceRepoDir, "config core.autocrlf false");
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "CrlfFile.cs"), "line1\r\nline2\r\n");
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "LfFile.cs"), "lf1\nlf2\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Mixed line endings\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "crlf-roundtrip",
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix CRLF and LF files"
            },
            BenchmarkProblemCategory.Implementation);

        var crlfFilePath = Path.Combine(handle.WorkspacePath, "CrlfFile.cs");
        await File.WriteAllTextAsync(crlfFilePath, "line1\r\nline2-fixed\r\n");

        var crlfPatch = await _manager.ExtractPatchAsync(handle.WorkspacePath, handle.BaseCommit);
        Assert.Contains("\r\n", crlfPatch);

        await _manager.ResetToBaseAsync(handle.WorkspacePath, handle.BaseCommit);
        Assert.DoesNotContain("line2-fixed", await File.ReadAllTextAsync(crlfFilePath));

        await _manager.ApplyPatchAsync(handle.WorkspacePath, crlfPatch);
        Assert.Equal("line1\r\nline2-fixed\r\n", await File.ReadAllTextAsync(crlfFilePath));

        // Verify CRLF-serialized patch targeting an LF file still applies via LF fallback
        await _manager.ResetToBaseAsync(handle.WorkspacePath, handle.BaseCommit);
        const string crlfFormattedPatchForLfFile =
            "diff --git a/LfFile.cs b/LfFile.cs\r\n" +
            "--- a/LfFile.cs\r\n" +
            "+++ b/LfFile.cs\r\n" +
            "@@ -1,2 +1,2 @@\r\n" +
            " lf1\r\n" +
            "-lf2\r\n" +
            "+lf2-fixed\r\n";

        await _manager.ApplyPatchAsync(handle.WorkspacePath, crlfFormattedPatchForLfFile);
        Assert.Equal("lf1\nlf2-fixed\n", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "LfFile.cs")));
    }

    private static string RunGit(string workingDir, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed ({proc.ExitCode}): {stderr}");
        }
        return stdout;
    }

    private static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
