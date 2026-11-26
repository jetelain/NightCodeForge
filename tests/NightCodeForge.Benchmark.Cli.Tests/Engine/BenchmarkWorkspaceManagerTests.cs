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

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        (_gitRunner, _manager) = CreateGitRunnerAndManager(processRunner);

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
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, "node_modules", "pkg"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "node_modules", "pkg", "index.js"), "module.exports = 1;");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, ".venv", "bin"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, ".venv", "bin", "pytest"), "#!/bin/sh");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, "__pycache__"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "__pycache__", "mod.cpython-312.pyc"), "pyc");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, ".pytest_cache", "v", "cache"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, ".pytest_cache", "v", "cache", "lastfailed"), "{}");
        Directory.CreateDirectory(Path.Combine(handle.WorkspacePath, "packages", "app", ".pytest_cache"));
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "packages", "app", ".pytest_cache", "README.md"), "cache");

        var patch = await _manager.ExtractPatchAsync(handle.WorkspacePath, handle.BaseCommit);

        Assert.Contains("NewService.cs", patch);
        Assert.DoesNotContain("bin/Debug/out.dll", patch);
        Assert.DoesNotContain("obj/cache.txt", patch);
        Assert.DoesNotContain("results.trx", patch);
        Assert.DoesNotContain("node_modules/pkg/index.js", patch);
        Assert.DoesNotContain(".venv/bin/pytest", patch);
        Assert.DoesNotContain("__pycache__/mod.cpython-312.pyc", patch);
        Assert.DoesNotContain(".pytest_cache/v/cache/lastfailed", patch);
        Assert.DoesNotContain("packages/app/.pytest_cache/README.md", patch);
    }

    [Fact]
    public async Task ProvisionAsync_EnablesCoreLongpathsAndChecksOutDeepNestedPaths()
    {
        RunGit(_sourceRepoDir, "config core.longpaths true");
        var deepDir = Path.Combine(
            _sourceRepoDir,
            "packages",
            "docusaurus-plugin-content-docs",
            "src",
            "__tests__",
            "__fixtures__",
            "versioned-docs-with-long-subpath-segment-alpha",
            "versioned_docs",
            "version-1.0.0-beta.long-segment-bravo",
            "nested-category-charlie-delta-echo");
        Directory.CreateDirectory(deepDir);
        var deepFile = Path.Combine(deepDir, "very-long-fixture-file-name-for-windows-max-path-verification.md");
        await File.WriteAllTextAsync(deepFile, "# Deep fixture\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Deep fixture path\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "longpaths-001",
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Test long paths"
            },
            BenchmarkProblemCategory.Implementation);

        var longPathsSetting = RunGit(handle.WorkspacePath, "config --get core.longpaths").Trim();
        Assert.Equal("true", longPathsSetting);

        var relPath = Path.GetRelativePath(_sourceRepoDir, deepFile);
        var checkedOutFile = Path.Combine(handle.WorkspacePath, relPath);
        Assert.True(File.Exists(checkedOutFile));
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

    [Fact]
    public async Task ResetPathsToBaseAsync_And_ExtractPatchTargetPaths_ResolveTestPatchCollisionsWithModelEdits()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRepoDir, "test"));
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "test", "ExistingTests.cs"), "class ExistingTests { void BaseTest() {} }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base tests\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        const string evalTestPatch = """
            diff --git a/test/ExistingTests.cs b/test/ExistingTests.cs
            --- a/test/ExistingTests.cs
            +++ b/test/ExistingTests.cs
            @@ -1 +1,2 @@
             class ExistingTests { void BaseTest() {} }
            +// gold assertion
            diff --git a/test/NewGoldTests.cs b/test/NewGoldTests.cs
            new file mode 100644
            --- /dev/null
            +++ b/test/NewGoldTests.cs
            @@ -0,0 +1 @@
            +class NewGoldTests {}
            """;

        var traversalSample = evalTestPatch + "\ndiff --git a/../escape.txt b/../escape.txt\n--- a/../escape.txt\n+++ b/../escape.txt\n";
        Assert.Equal(["test/ExistingTests.cs", "test/NewGoldTests.cs"], BenchmarkWorkspaceManager.ExtractPatchTargetPaths(traversalSample));

        var extracted = BenchmarkWorkspaceManager.ExtractPatchTargetPaths(evalTestPatch);
        Assert.Equal(["test/ExistingTests.cs", "test/NewGoldTests.cs"], extracted);

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "reset-paths-001",
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix bug"
            },
            BenchmarkProblemCategory.Implementation);

        // Model also modified ExistingTests.cs and created a conflicting NewGoldTests.cs
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "ExistingTests.cs"), "// model conflicting edit\n");
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "NewGoldTests.cs"), "class ModelCreatedSameFile {}\n");
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "ModelExtraFile.cs"), "class KeepMe {}\n");

        // Applying evalTestPatch without resetting would conflict; ResetPathsToBaseAsync resolves both tracked & new files cleanly
        await Assert.ThrowsAsync<BenchmarkWorkspaceException>(() => _manager.ApplyPatchAsync(handle.WorkspacePath, evalTestPatch));

        await _manager.ResetPathsToBaseAsync(handle.WorkspacePath, handle.BaseCommit, extracted);
        await _manager.ApplyPatchAsync(handle.WorkspacePath, evalTestPatch);

        Assert.Contains("// gold assertion", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "ExistingTests.cs")));
        Assert.Contains("class NewGoldTests {}", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "NewGoldTests.cs")));
        Assert.True(File.Exists(Path.Combine(handle.WorkspacePath, "ModelExtraFile.cs")));

        await Assert.ThrowsAsync<BenchmarkWorkspaceException>(() =>
            _manager.ResetPathsToBaseAsync(handle.WorkspacePath, handle.BaseCommit, ["../outside.cs"]));
    }

    [Fact]
    public async Task ResetPathsToBaseAsync_WithCallbackProcessRunnerAndMultipleTrackedFiles_PreservesTrackedFilesAndAppliesTestPatch()
    {
        var subADir = Path.Combine(_sourceRepoDir, "test", "SubA");
        var subBDir = Path.Combine(_sourceRepoDir, "test", "SubB");
        Directory.CreateDirectory(subADir);
        Directory.CreateDirectory(subBDir);
        await File.WriteAllTextAsync(Path.Combine(subADir, "FirstTests.cs"), "class FirstTests { void Base() {} }\n");
        await File.WriteAllTextAsync(Path.Combine(subBDir, "SecondTests.cs"), "class SecondTests { void Base() {} }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base nested tests\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        const string evalTestPatch = """
            diff --git a/test/SubA/FirstTests.cs b/test/SubA/FirstTests.cs
            --- a/test/SubA/FirstTests.cs
            +++ b/test/SubA/FirstTests.cs
            @@ -1 +1,2 @@
             class FirstTests { void Base() {} }
            +// gold assertion 1
            diff --git a/test/SubB/SecondTests.cs b/test/SubB/SecondTests.cs
            --- a/test/SubB/SecondTests.cs
            +++ b/test/SubB/SecondTests.cs
            @@ -1 +1,2 @@
             class SecondTests { void Base() {} }
            +// gold assertion 2
            diff --git a/test/SubA/NewEvalTests.cs b/test/SubA/NewEvalTests.cs
            new file mode 100644
            --- /dev/null
            +++ b/test/SubA/NewEvalTests.cs
            @@ -0,0 +1 @@
            +class NewEvalTests {}
            """;

        var (_, callbackManager) = CreateGitRunnerAndManager(
            new CallbackAttachingProcessRunner(new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance)));

        var handle = await callbackManager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "reset-paths-callback-001",
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix multi-file test reset"
            },
            BenchmarkProblemCategory.Implementation);

        // Model overwrote both tracked test files and created a conflicting new test file
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "SubA", "FirstTests.cs"), "// model edit 1\n");
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "SubB", "SecondTests.cs"), "// model edit 2\n");
        await File.WriteAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "SubA", "NewEvalTests.cs"), "class ModelConflict {}\n");

        var targetPaths = BenchmarkWorkspaceManager.ExtractPatchTargetPaths(evalTestPatch);
        Assert.Equal(["test/SubA/FirstTests.cs", "test/SubB/SecondTests.cs", "test/SubA/NewEvalTests.cs"], targetPaths);

        await callbackManager.ResetPathsToBaseAsync(handle.WorkspacePath, handle.BaseCommit, targetPaths);
        await callbackManager.ApplyPatchAsync(handle.WorkspacePath, evalTestPatch);

        Assert.Contains("// gold assertion 1", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "SubA", "FirstTests.cs")));
        Assert.Contains("// gold assertion 2", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "SubB", "SecondTests.cs")));
        Assert.Contains("class NewEvalTests {}", await File.ReadAllTextAsync(Path.Combine(handle.WorkspacePath, "test", "SubA", "NewEvalTests.cs")));
    }

    [Fact]
    public async Task Step1163_ResetToBaseAsync_PurgesGitignoredTargetArtifactsWhilePreservingNodeModulesAndVenv()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, ".gitignore"), "target/\nnode_modules/\n.venv/\n");
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "lib.rs"), "pub fn v() -> i32 { 1 }\n");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base with gitignore\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "reset-clean-ignores",
                Language = BenchmarkLanguages.Rust,
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix rust"
            },
            BenchmarkProblemCategory.Implementation);

        var targetOutDir = Path.Combine(handle.WorkspacePath, "target", "debug", "build");
        var nodeModulesDir = Path.Combine(handle.WorkspacePath, "node_modules", "pkg");
        var venvDir = Path.Combine(handle.WorkspacePath, ".venv", "bin");
        Directory.CreateDirectory(targetOutDir);
        Directory.CreateDirectory(nodeModulesDir);
        Directory.CreateDirectory(venvDir);

        await File.WriteAllTextAsync(Path.Combine(targetOutDir, "stale-out.bin"), "stale-target");
        await File.WriteAllTextAsync(Path.Combine(nodeModulesDir, "index.js"), "module.exports = 1;");
        await File.WriteAllTextAsync(Path.Combine(venvDir, "pytest"), "#!/bin/sh\n");

        await _manager.ResetToBaseAsync(handle.WorkspacePath, handle.BaseCommit);

        Assert.False(Directory.Exists(Path.Combine(handle.WorkspacePath, "target")));
        Assert.True(File.Exists(Path.Combine(nodeModulesDir, "index.js")));
        Assert.True(File.Exists(Path.Combine(venvDir, "pytest")));
    }

    [Fact]
    public async Task Step1163_CleanupAsync_HandlesCyclicDirectoryReparsePointsWithoutHanging()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "pkg.json"), "{}");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "cleanup-cyclic-reparse",
                Language = BenchmarkLanguages.TypeScript,
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix"
            },
            BenchmarkProblemCategory.Implementation);

        var packagesDir = Path.Combine(handle.WorkspacePath, "packages");
        var pkgA = Path.Combine(packagesDir, "pkg-a", "node_modules");
        var pkgB = Path.Combine(packagesDir, "pkg-b", "node_modules");
        Directory.CreateDirectory(pkgA);
        Directory.CreateDirectory(pkgB);
        await File.WriteAllTextAsync(Path.Combine(packagesDir, "pkg-a", "index.ts"), "export const a = 1;");

        var linkAtoB = Path.Combine(pkgA, "pkg-b");
        var linkBtoA = Path.Combine(pkgB, "pkg-a");
        if (OperatingSystem.IsWindows())
        {
            using var p1 = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkAtoB}\" \"{Path.Combine(packagesDir, "pkg-b")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p1.WaitForExit(5000);
            using var p2 = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkBtoA}\" \"{Path.Combine(packagesDir, "pkg-a")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p2.WaitForExit(5000);
        }
        else
        {
            Directory.CreateSymbolicLink(linkAtoB, Path.Combine(packagesDir, "pkg-b"));
            Directory.CreateSymbolicLink(linkBtoA, Path.Combine(packagesDir, "pkg-a"));
        }

        await _manager.CleanupAsync(handle).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(handle.WorkspacePath));
    }

    [Fact]
    public async Task Step1163_CleanupAsync_HandlesDanglingDirectoryReparsePointsAndReadOnlyFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "pkg.json"), "{}");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "cleanup-dangling-reparse",
                Language = BenchmarkLanguages.TypeScript,
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix"
            },
            BenchmarkProblemCategory.Implementation);

        var targetDir = Path.Combine(handle.WorkspacePath, "deleted-target");
        var linkParent = Path.Combine(handle.WorkspacePath, "a-links");
        var siblingDir = Path.Combine(handle.WorkspacePath, "z-sibling");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(linkParent);
        Directory.CreateDirectory(siblingDir);

        var danglingLink = Path.Combine(linkParent, "broken-link");
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{danglingLink}\" \"{targetDir}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(5000);
        }
        else
        {
            Directory.CreateSymbolicLink(danglingLink, targetDir);
        }

        // Delete target so the junction/symlink becomes dangling before cleanup.
        Directory.Delete(targetDir, recursive: true);

        var readOnlyFile = Path.Combine(siblingDir, "readonly.txt");
        await File.WriteAllTextAsync(readOnlyFile, "locked");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        await _manager.CleanupAsync(handle).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(handle.WorkspacePath));
    }

    [Fact]
    public async Task CleanupAsync_ContinuesDeletingSiblingFilesAndDirectoriesWhenOneFileIsLocked()
    {
        await File.WriteAllTextAsync(Path.Combine(_sourceRepoDir, "pkg.json"), "{}");
        RunGit(_sourceRepoDir, "add -A");
        RunGit(_sourceRepoDir, "commit -m \"Base\"");
        var baseCommit = RunGit(_sourceRepoDir, "rev-parse HEAD").Trim();

        var handle = await _manager.ProvisionAsync(
            new BenchmarkInstance
            {
                InstanceId = "cleanup-locked-file",
                Language = BenchmarkLanguages.CSharp,
                Repo = _sourceRepoDir,
                BaseCommit = baseCommit,
                ProblemStatement = "Fix"
            },
            BenchmarkProblemCategory.Implementation);

        var lockedDir = Path.Combine(handle.WorkspacePath, "a-locked");
        var siblingDir = Path.Combine(handle.WorkspacePath, "z-sibling");
        Directory.CreateDirectory(lockedDir);
        Directory.CreateDirectory(siblingDir);

        var lockedFile = Path.Combine(lockedDir, "locked.bin");
        var lockedSiblingFile = Path.Combine(lockedDir, "removable.txt");
        var siblingFile = Path.Combine(siblingDir, "sibling.txt");
        await File.WriteAllTextAsync(lockedFile, "lock");
        await File.WriteAllTextAsync(lockedSiblingFile, "delete-me");
        await File.WriteAllTextAsync(siblingFile, "delete-me-too");

        using (var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await _manager.CleanupAsync(handle).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(File.Exists(lockedFile));
            Assert.False(File.Exists(lockedSiblingFile));
            Assert.False(Directory.Exists(siblingDir));
        }

        await _manager.CleanupAsync(handle).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(handle.WorkspacePath));
    }

    private (GitCliRunner GitRunner, BenchmarkWorkspaceManager Manager) CreateGitRunnerAndManager(IProcessRunner processRunner)
    {
        var wslMetadata = new Mock<IWslMetadataService>();
        wslMetadata.Setup(m => m.GetDefaultDistributionName()).Returns("Ubuntu");
        wslMetadata.Setup(m => m.GetDefaultUserName()).Returns("user");

        var pathResolver = new WorkspacePathResolver(
            wslMetadata.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()));

        var gitRunner = new GitCliRunner(
            processRunner,
            pathResolver,
            wslMetadata.Object,
            NullLogger<GitCliRunner>.Instance);

        var manager = new BenchmarkWorkspaceManager(
            gitRunner,
            Options.Create(new BenchmarkWorkspaceOptions { RootDirectory = _benchRootDir }),
            pathResolver,
            processRunner,
            NullLogger<BenchmarkWorkspaceManager>.Instance);

        return (gitRunner, manager);
    }

    [Fact]
    public void WorkspacesDirectory_ResolvesWslUncWhenEnabledAndRespectsExplicitOverride()
    {
        var wslMetadata = new Mock<IWslMetadataService>();
        wslMetadata.Setup(m => m.GetDefaultDistributionName()).Returns("Ubuntu");
        wslMetadata.Setup(m => m.GetDefaultUserName()).Returns("gruearbre");

        var windowsResolver = new WorkspacePathResolver(
            wslMetadata.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: true);

        var processRunner = new Mock<IProcessRunner>().Object;
        var gitRunner = new Mock<IGitCliRunner>().Object;

        var defaultManager = new BenchmarkWorkspaceManager(
            gitRunner,
            Options.Create(new BenchmarkWorkspaceOptions { RootDirectory = _benchRootDir, UseWslExt4Workspaces = false }),
            windowsResolver,
            processRunner,
            NullLogger<BenchmarkWorkspaceManager>.Instance);
        Assert.Equal(Path.Combine(Path.GetFullPath(_benchRootDir), BenchmarkPaths.WorkspacesSubdir), defaultManager.WorkspacesDirectory);

        var wslManager = new BenchmarkWorkspaceManager(
            gitRunner,
            Options.Create(new BenchmarkWorkspaceOptions { RootDirectory = _benchRootDir, UseWslExt4Workspaces = true }),
            windowsResolver,
            processRunner,
            NullLogger<BenchmarkWorkspaceManager>.Instance);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"\\wsl.localhost\Ubuntu\home\gruearbre\.nightcodeforge\benchmarks\workspaces", wslManager.WorkspacesDirectory);
        }

        var overrideManager = new BenchmarkWorkspaceManager(
            gitRunner,
            Options.Create(new BenchmarkWorkspaceOptions { RootDirectory = _benchRootDir, WorkspacesDirectory = "~/custom/bench-wks" }),
            windowsResolver,
            processRunner,
            NullLogger<BenchmarkWorkspaceManager>.Instance);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\gruearbre\custom\bench-wks", overrideManager.WorkspacesDirectory);
    }

    [Fact]
    public void IsSafeWslCleanupTarget_AcceptsOnlyDeepNightCodeForgeSubdirectories()
    {
        Assert.True(BenchmarkWorkspaceManager.IsSafeWslCleanupTarget("/home/gruearbre/.nightcodeforge/benchmarks/workspaces/serilog-123"));
        Assert.False(BenchmarkWorkspaceManager.IsSafeWslCleanupTarget("/"));
        Assert.False(BenchmarkWorkspaceManager.IsSafeWslCleanupTarget("/home/gruearbre"));
        Assert.False(BenchmarkWorkspaceManager.IsSafeWslCleanupTarget("/home/gruearbre/.nightcodeforge"));
        Assert.False(BenchmarkWorkspaceManager.IsSafeWslCleanupTarget("/home/gruearbre/.nightcodeforge/benchmarks/workspaces/../../etc"));
        Assert.False(BenchmarkWorkspaceManager.IsSafeWslCleanupTarget("relative/path"));
    }

    [Fact]
    public async Task ProvisionAsync_And_ApplyPatchAsync_WithWslUncWorkspace_TranslatesRepoAndPatchPathsToWsl()
    {
        var wslMetadata = new Mock<IWslMetadataService>();
        wslMetadata.Setup(m => m.GetDefaultDistributionName()).Returns("Ubuntu");
        wslMetadata.Setup(m => m.GetDefaultUserName()).Returns("gruearbre");
        var pathResolver = new WorkspacePathResolver(
            wslMetadata.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: true);

        var cacheRepoDir = Path.Combine(_benchRootDir, BenchmarkPaths.ReposCacheSubdir, "owner__repo");
        Directory.CreateDirectory(Path.Combine(cacheRepoDir, "objects"));
        await File.WriteAllTextAsync(Path.Combine(cacheRepoDir, "HEAD"), "ref: refs/heads/main\n");

        var wslWorkspace = Path.Combine(_tempDir, "fake-wsl-wks");
        Directory.CreateDirectory(wslWorkspace);
        var recordedCalls = new List<(string WorkingDir, string[] Args)>();

        var gitRunner = new Mock<IGitCliRunner>();
        gitRunner
            .Setup(g => g.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string wd, IEnumerable<string> args, int? _, CancellationToken _) =>
            {
                var arr = args.ToArray();
                recordedCalls.Add((wd, arr));
                if (arr.Length >= 2 && arr[0] == "rev-parse")
                {
                    return new GitCommandResult(0, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n", string.Empty, TimeSpan.Zero);
                }
                return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
            });

        var manager = new BenchmarkWorkspaceManager(
            gitRunner.Object,
            Options.Create(new BenchmarkWorkspaceOptions { RootDirectory = _benchRootDir }),
            pathResolver,
            new Mock<IProcessRunner>().Object,
            NullLogger<BenchmarkWorkspaceManager>.Instance);

        await manager.ApplyPatchAsync(wslWorkspace, "diff --git a/a.cs b/a.cs\n");
        Assert.Contains(recordedCalls, c => c.Args.Length == 3 && c.Args[0] == "apply" && !c.Args[2].StartsWith("/mnt/", StringComparison.Ordinal));

        if (OperatingSystem.IsWindows() && Directory.Exists(@"\\wsl.localhost\Ubuntu\tmp"))
        {
            var uncWorkspace = $@"\\wsl.localhost\Ubuntu\tmp\ncf-test-{Guid.NewGuid():N}";
            try
            {
                recordedCalls.Clear();
                var handle = await manager.ProvisionAsync(
                    new BenchmarkInstance
                    {
                        InstanceId = "wsl-unc-test",
                        Repo = "owner/repo",
                        BaseCommit = "deadbeef",
                        ProblemStatement = "Test"
                    },
                    BenchmarkProblemCategory.Implementation,
                    instanceWorkspacePath: uncWorkspace);

                var expectedWslCachePath = WslPathConverter.ToWslPath(cacheRepoDir);
                Assert.Contains(recordedCalls, c =>
                    c.WorkingDir == uncWorkspace &&
                    c.Args.SequenceEqual(["-c", "safe.directory=*", "fetch", expectedWslCachePath, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"]));

                recordedCalls.Clear();
                await manager.ApplyPatchAsync(handle.WorkspacePath, "diff --git a/a.cs b/a.cs\n");
                Assert.Contains(recordedCalls, c =>
                    c.WorkingDir == uncWorkspace &&
                    c.Args.Length == 3 &&
                    c.Args[0] == "apply" &&
                    c.Args[2].StartsWith("/mnt/", StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(uncWorkspace))
                {
                    Directory.Delete(uncWorkspace, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// Simulates TelemetryProcessRunner by attaching stdout/stderr line callbacks before delegating,
    /// exercising SystemProcessRunner.ReadStreamWithCallbacksAsync where lines get trailing newlines.
    /// </summary>
    private sealed class CallbackAttachingProcessRunner(IProcessRunner inner) : IProcessRunner
    {
        public ProcessExecutionResult Run(ProcessStartSpec spec)
        {
            AttachCallbacks(spec);
            return inner.Run(spec);
        }

        public Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            AttachCallbacks(spec);
            return inner.RunAsync(spec, cancellationToken);
        }

        public IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
            => inner.RunStreamingAsync(spec, cancellationToken);

        public IProcessStreamSession StartSession(ProcessStartSpec spec)
            => inner.StartSession(spec);

        private static void AttachCallbacks(ProcessStartSpec spec)
        {
            var origOut = spec.OnStandardOutput;
            var origErr = spec.OnStandardError;
            spec.OnStandardOutput = line => origOut?.Invoke(line);
            spec.OnStandardError = line => origErr?.Invoke(line);
        }
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
