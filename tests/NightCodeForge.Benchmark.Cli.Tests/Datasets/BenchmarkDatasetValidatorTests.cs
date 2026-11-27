using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Commands;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Tests.Datasets;

public sealed class BenchmarkDatasetValidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BenchmarkDatasetLoader _loader = new();

    public BenchmarkDatasetValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-val-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_Implementation_PassesWhenBaseFailsAndGoldPassesAndCleansUp()
    {
        var datasetPath = Path.Combine(_tempDir, "impl.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"impl-1","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc123","problem_statement":"Fix bug","patch":"diff gold","test_patch":"diff test","fail_to_pass":["TestA"],"pass_to_pass":["TestB"]}
        """);

        var workspaceDir = Path.Combine(_tempDir, "wks-1");
        Directory.CreateDirectory(workspaceDir);
        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "impl-1",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = workspaceDir,
            BaseCommit = "abc123"
        };

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, null, ExecutionMode.ContainerSandbox, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailToPassPassed = 0,
                FailToPassTotal = 1,
                PassToPassPassed = 1,
                PassToPassTotal = 1,
                FailedTests = ["TestA"]
            });
        verifier
            .Setup(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, "diff gold", ExecutionMode.ContainerSandbox, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = true,
                FailToPassPassed = 1,
                FailToPassTotal = 1,
                PassToPassPassed = 1,
                PassToPassTotal = 1
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var progress = new List<string>();
        var report = await validator.ValidateAsync(
            datasetPath,
            categoryOverride: null,
            instanceIdFilter: null,
            limit: null,
            verifyWithTests: true,
            progressCallback: progress.Add);

        Assert.True(report.AllPassed);
        Assert.Equal(1, report.PassedCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Contains(progress, p => p.Contains("[PASS]") && p.Contains("impl-1"));
        workspaceManager.Verify(m => m.CleanupAsync(handle, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_Implementation_FailsWhenBaseAlreadyPassesOrGoldFails()
    {
        var datasetPath = Path.Combine(_tempDir, "impl-bad.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"impl-base-passes","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug","patch":"diff1","fail_to_pass":["T1"]}""",
            """{"instance_id":"impl-gold-fails","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug","patch":"diff2","fail_to_pass":["T2"]}""",
            """{"instance_id":"impl-no-gold","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug","fail_to_pass":["T3"]}"""
        ]));

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BenchmarkInstance inst, BenchmarkProblemCategory cat, string? _, CancellationToken _) => new BenchmarkWorkspaceHandle
            {
                InstanceId = inst.InstanceId,
                Category = cat,
                WorkspacePath = Path.Combine(_tempDir, inst.InstanceId),
                BaseCommit = inst.BaseCommit
            });

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "impl-base-passes"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                null,
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = true,
                FailToPassPassed = 1,
                FailToPassTotal = 1
            });

        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "impl-gold-fails"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                null,
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailToPassPassed = 0,
                FailToPassTotal = 1
            });

        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "impl-gold-fails"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff2",
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailToPassPassed = 0,
                FailToPassTotal = 1,
                FailedTests = ["T2"],
                ErrorMessage = "Failed tests: T2"
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);

        Assert.False(report.AllPassed);
        Assert.Equal(3, report.FailedCount);
        Assert.Contains("already pass on BaseCommit", report.Results[0].ErrorMessage);
        Assert.Contains("GoldPatch failed verification", report.Results[1].ErrorMessage);
        Assert.Contains("missing required 'patch'", report.Results[2].ErrorMessage);
        workspaceManager.Verify(m => m.CleanupAsync(It.IsAny<BenchmarkWorkspaceHandle>(), CancellationToken.None), Times.Exactly(3));
    }

    [Fact]
    public async Task ValidateAsync_CodeReviewAndTaskReview_ValidatesDiffsAndFrontmatter()
    {
        var crDataset = Path.Combine(_tempDir, "cr.jsonl");
        await File.WriteAllTextAsync(crDataset, string.Join(Environment.NewLine,
        [
            """{"instance_id":"cr-ok","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff --git a/A.cs b/A.cs\n","expected_verdict":"Approve"}""",
            """{"instance_id":"cr-empty","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff --git a/B.cs b/B.cs\n","expected_verdict":"MinorFix"}"""
        ]));

        var wksCrOk = Path.Combine(_tempDir, "wks-cr-ok");
        var wksCrEmpty = Path.Combine(_tempDir, "wks-cr-empty");
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-ok"), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "cr-ok",
                Category = BenchmarkProblemCategory.CodeReview,
                WorkspacePath = wksCrOk,
                BaseCommit = "abc",
                ReviewDiff = "diff --git a/A.cs b/A.cs\n+line"
            });
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-empty"), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "cr-empty",
                Category = BenchmarkProblemCategory.CodeReview,
                WorkspacePath = wksCrEmpty,
                BaseCommit = "abc",
                ReviewDiff = "   "
            });

        var verifier = new Mock<IBenchmarkVerifier>();
        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var crReport = await validator.ValidateAsync(crDataset, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.False(crReport.AllPassed);
        Assert.True(crReport.Results[0].Passed);
        Assert.False(crReport.Results[1].Passed);
        Assert.Contains("empty ReviewDiff", crReport.Results[1].ErrorMessage);

        var trDataset = Path.Combine(_tempDir, "tr.jsonl");
        await File.WriteAllTextAsync(trDataset, string.Join(Environment.NewLine,
        [
            """{"instance_id":"tr-ok","category":"TaskReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Task","task_specification":"---\nid: 1\ntitle: \"Valid\"\nstatus: Open\ndifficulty: standard\n---\n\nDetailed requirements.","expected_verdict":"Ready"}""",
            """{"instance_id":"tr-bad","category":"TaskReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Task","task_specification":"No frontmatter","expected_verdict":"Ready"}"""
        ]));

        var wksTrOk = Path.Combine(_tempDir, "wks-tr-ok");
        Directory.CreateDirectory(Path.Combine(wksTrOk, "docs", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(wksTrOk, "docs", "tasks", "tr-ok.md"),
            "---\nid: 1\ntitle: \"Valid\"\nstatus: Open\ndifficulty: standard\n---\n\nDetailed requirements.");

        var wksTrBad = Path.Combine(_tempDir, "wks-tr-bad");
        Directory.CreateDirectory(Path.Combine(wksTrBad, "docs", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(wksTrBad, "docs", "tasks", "tr-bad.md"),
            "---\ntitle: MissingId\n---\n\n");

        workspaceManager
            .Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "tr-ok"), BenchmarkProblemCategory.TaskReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "tr-ok",
                Category = BenchmarkProblemCategory.TaskReview,
                WorkspacePath = wksTrOk,
                BaseCommit = "abc",
                TaskMarkdownPath = "docs/tasks/tr-ok.md"
            });
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "tr-bad"), BenchmarkProblemCategory.TaskReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "tr-bad",
                Category = BenchmarkProblemCategory.TaskReview,
                WorkspacePath = wksTrBad,
                BaseCommit = "abc",
                TaskMarkdownPath = "docs/tasks/tr-bad.md"
            });

        var trReport = await validator.ValidateAsync(trDataset, null, null, null, verifyWithTests: true, progressCallback: null);
        Assert.False(trReport.AllPassed);
        Assert.True(trReport.Results[0].Passed);
        Assert.False(trReport.Results[1].Passed);
        Assert.Contains("missing a valid positive 'id'", trReport.Results[1].ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_UnknownFilterId_FailsFastWithDatasetException()
    {
        var datasetPath = Path.Combine(_tempDir, "ds.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"cr-1","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff","expected_verdict":"Approve"}
        """);

        var validator = new BenchmarkDatasetValidator(
            _loader,
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IBenchmarkVerifier>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var ex = await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            validator.ValidateAsync(datasetPath, null, ["missing-id"], null, verifyWithTests: false, progressCallback: null));

        Assert.Contains("missing-id", ex.Message);
    }

    [Theory]
    [InlineData(true, BenchmarkCliConstants.ExitSuccess)]
    [InlineData(false, BenchmarkCliConstants.ExitFailure)]
    public async Task DatasetValidateCommand_ExecuteAsync_ReturnsExpectedExitCode(bool allPassed, int expectedExitCode)
    {
        var validator = new Mock<IBenchmarkDatasetValidator>();
        validator
            .Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<BenchmarkProblemCategory?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkDatasetValidationReport
            {
                DatasetPath = "ds.jsonl",
                Results =
                [
                    new BenchmarkInstanceValidationResult
                    {
                        InstanceId = "inst-1",
                        Category = BenchmarkProblemCategory.Implementation,
                        Language = BenchmarkLanguages.CSharp,
                        Passed = allPassed,
                        Summary = allPassed ? "OK" : null,
                        ErrorMessage = allPassed ? null : "Failed"
                    }
                ]
            });

        var command = new DatasetValidateCommand(validator.Object);
        var lines = new List<string>();
        var exitCode = await command.ExecuteAsync(
            new DatasetValidateCommandOptions { DatasetPath = "ds.jsonl", VerifyWithTests = false },
            lines.Add);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Contains(lines, l => l.Contains("Validation complete:", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationReport_AllPassed_IsFalseWhenEmpty()
    {
        var emptyReport = new BenchmarkDatasetValidationReport
        {
            DatasetPath = "empty.jsonl",
            Results = []
        };

        Assert.False(emptyReport.AllPassed);
        Assert.Equal(0, emptyReport.TotalCount);
    }

    [Fact]
    public async Task ValidateAsync_ImplementationNoVerify_ChecksTestPatchAloneAndGoldPlusTestPatch()
    {
        var datasetPath = Path.Combine(_tempDir, "impl-noverify.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"impl-nv-1","category":"Implementation","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Bug","patch":"diff gold","test_patch":"diff test"}
        """);

        var workspaceDir = Path.Combine(_tempDir, "wks-nv");
        Directory.CreateDirectory(workspaceDir);
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "impl-nv-1",
                Category = BenchmarkProblemCategory.Implementation,
                WorkspacePath = workspaceDir,
                BaseCommit = "abc"
            });

        workspaceManager
            .Setup(m => m.ApplyPatchAsync(workspaceDir, "diff test", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("test_patch conflict"));

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            new Mock<IBenchmarkVerifier>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var failedReport = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.False(failedReport.AllPassed);
        Assert.Contains("TestPatch failed to apply cleanly", failedReport.Results[0].ErrorMessage);
        workspaceManager.Verify(m => m.ApplyPatchAsync(workspaceDir, "diff gold", It.IsAny<CancellationToken>()), Times.Never);

        workspaceManager
            .Setup(m => m.ApplyPatchAsync(workspaceDir, "diff test", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var passedReport = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.True(passedReport.AllPassed);
        workspaceManager.Verify(m => m.ResetToBaseAsync(workspaceDir, "abc", It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ValidateAsync_CodeReview_VerifyAndNoVerifyBranches_ValidateTestPatchApproveAndMinorFix()
    {
        var datasetPath = Path.Combine(_tempDir, "cr-branches.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"cr-approve","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand","test_patch":"diff test","expected_verdict":"Approve","fail_to_pass":["T1"]}""",
            """{"instance_id":"cr-minor-gold","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand","patch":"diff gold","test_patch":"diff test","expected_verdict":"MinorFix","fail_to_pass":["T2"]}""",
            """{"instance_id":"cr-minor-nogold","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand","test_patch":"diff test","expected_verdict":"MinorFix"}"""
        ]));

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BenchmarkInstance inst, BenchmarkProblemCategory cat, string? _, CancellationToken _) => new BenchmarkWorkspaceHandle
            {
                InstanceId = inst.InstanceId,
                Category = cat,
                WorkspacePath = Path.Combine(_tempDir, inst.InstanceId),
                BaseCommit = "abc",
                ReviewDiff = "diff --git a/A.cs b/A.cs\n+x"
            });

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-approve"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand",
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailedTests = ["T1"],
                ErrorMessage = null
            });
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-gold"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand",
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailToPassPassed = 0,
                FailToPassTotal = 1,
                FailedTests = ["T2"]
            });
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-gold"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff gold",
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = true,
                FailToPassPassed = 1,
                FailToPassTotal = 1
            });
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-nogold"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand",
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                ErrorMessage = "Failed"
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var verifyReport = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);
        Assert.False(verifyReport.Results[0].Passed);
        Assert.Contains("Failed tests: T1", verifyReport.Results[0].ErrorMessage);
        Assert.True(verifyReport.Results[1].Passed);
        Assert.True(verifyReport.Results[2].Passed);

        // --no-verify must also test-apply CandidatePatch + TestPatch and MinorFix GoldPatch + TestPatch
        workspaceManager
            .Setup(m => m.ApplyPatchAsync(Path.Combine(_tempDir, "cr-minor-gold"), "diff gold", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("gold conflict"));

        var noVerifyReport = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.True(noVerifyReport.Results[0].Passed);
        Assert.False(noVerifyReport.Results[1].Passed);
        Assert.Contains("MinorFix reference GoldPatch/TestPatch failed", noVerifyReport.Results[1].ErrorMessage);
        Assert.True(noVerifyReport.Results[2].Passed);
    }

    [Fact]
    public async Task ValidateAsync_TaskReview_RejectsPathOutsideDocsTasksOrTraversal()
    {
        var datasetPath = Path.Combine(_tempDir, "tr-path.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"tr-1","category":"TaskReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Spec","task_specification":"---\nid: 1\ntitle: \"T\"\nstatus: Open\n---\nBody","expected_verdict":"Ready"}
        """);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.TaskReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "tr-1",
                Category = BenchmarkProblemCategory.TaskReview,
                WorkspacePath = _tempDir,
                BaseCommit = "abc",
                TaskMarkdownPath = "docs/tasks/../outside.md"
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            new Mock<IBenchmarkVerifier>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.False(report.AllPassed);
        Assert.Contains("must reside under 'docs/tasks/'", report.Results[0].ErrorMessage);
    }

    [Fact]
    public async Task ValidateAsync_CodeReviewMinorFixWithGoldPatchAndNoTestPatch_NoVerifyValidatesGoldPatchApplication()
    {
        var datasetPath = Path.Combine(_tempDir, "cr-minor-gold-notest.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"cr-minor-gold-only","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand","patch":"diff gold","expected_verdict":"MinorFix"}
        """);

        var workspaceDir = Path.Combine(_tempDir, "wks-cr-minor-gold-only");
        Directory.CreateDirectory(workspaceDir);
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "cr-minor-gold-only",
                Category = BenchmarkProblemCategory.CodeReview,
                WorkspacePath = workspaceDir,
                BaseCommit = "abc",
                ReviewDiff = "diff --git a/A.cs b/A.cs\n+x"
            });

        workspaceManager
            .Setup(m => m.ApplyPatchAsync(workspaceDir, "diff gold", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("gold conflict"));

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            new Mock<IBenchmarkVerifier>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var failedReport = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.False(failedReport.AllPassed);
        Assert.Contains("MinorFix reference GoldPatch/TestPatch failed to apply cleanly", failedReport.Results[0].ErrorMessage);

        workspaceManager
            .Setup(m => m.ApplyPatchAsync(workspaceDir, "diff gold", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var passedReport = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);
        Assert.True(passedReport.AllPassed);
        workspaceManager.Verify(m => m.ResetToBaseAsync(workspaceDir, "abc", It.IsAny<CancellationToken>()), Times.Exactly(2));
        workspaceManager.Verify(m => m.ApplyPatchAsync(workspaceDir, "diff gold", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DatasetValidateCommand_ExecuteAsync_ThrowsBenchmarkDatasetExceptionOnEmptyDatasetPath()
    {
        var command = new DatasetValidateCommand(new Mock<IBenchmarkDatasetValidator>().Object);
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            command.ExecuteAsync(new DatasetValidateCommandOptions { DatasetPath = "   " }, _ => { }));
    }

    [Fact]
    public async Task ValidateAsync_RustPreFlightFailure_FailsWithoutProvisioningWorkspaceAndLeavesOtherStacksPassing()
    {
        var datasetPath = Path.Combine(_tempDir, "mixed-preflight.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"rust-1","category":"Implementation","language":"rust","repo":"burntsushi/ripgrep","base_commit":"abc","problem_statement":"Bug","patch":"diff --git a/a.rs b/a.rs\n"}""",
            """{"instance_id":"cs-1","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug","patch":"diff --git a/A.cs b/A.cs\n"}"""
        ]));

        var csWks = Path.Combine(_tempDir, "wks-cs");
        Directory.CreateDirectory(csWks);
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cs-1"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "cs-1",
                Category = BenchmarkProblemCategory.Implementation,
                WorkspacePath = csWks,
                BaseCommit = "abc"
            });

        var preflight = new Mock<IBenchmarkToolchainPreflightValidator>();
        preflight
            .Setup(p => p.ValidateAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "rust-1"), BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Pre-flight Rust toolchain check failed for 'rust-1': install rustup.");
        preflight
            .Setup(p => p.ValidateAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cs-1"), BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            new Mock<IBenchmarkVerifier>().Object,
            preflight.Object,
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: false, progressCallback: null);

        Assert.Equal(2, report.TotalCount);
        Assert.False(report.Results[0].Passed);
        Assert.Contains("install rustup", report.Results[0].ErrorMessage);
        Assert.True(report.Results[1].Passed);
        workspaceManager.Verify(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "rust-1"), It.IsAny<BenchmarkProblemCategory>(), null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DatasetValidateCommand_ExecuteAsync_ForwardsManifestPathAndThrowsWhenMissing()
    {
        var manifestPath = Path.Combine(_tempDir, "suite.manifest.json");
        var validatorMock = new Mock<IBenchmarkDatasetValidator>();
        string? capturedManifest = null;
        validatorMock
            .Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<BenchmarkProblemCategory?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, BenchmarkProblemCategory?, IReadOnlyList<string>?, int?, bool, Action<string>?, string?, int, CancellationToken>(
                (_, _, _, _, _, _, m, _, _) => capturedManifest = m)
            .ReturnsAsync(new BenchmarkDatasetValidationReport
            {
                DatasetPath = "ds.jsonl",
                Results =
                [
                    new BenchmarkInstanceValidationResult
                    {
                        InstanceId = "inst-1",
                        Category = BenchmarkProblemCategory.Implementation,
                        Language = BenchmarkLanguages.CSharp,
                        Passed = true
                    }
                ]
            });

        var command = new DatasetValidateCommand(validatorMock.Object);
        var exitCode = await command.ExecuteAsync(
            new DatasetValidateCommandOptions { DatasetPath = "ds.jsonl", ManifestPath = manifestPath, VerifyWithTests = false },
            _ => { });

        Assert.Equal(BenchmarkCliConstants.ExitSuccess, exitCode);
        Assert.Equal(manifestPath, capturedManifest);

        var realValidator = new BenchmarkDatasetValidator(
            _loader,
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IBenchmarkVerifier>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);
        var datasetPath = Path.Combine(_tempDir, "ad-hoc.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cs-1","category":"Implementation","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Fix","patch":"diff"}""");

        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            realValidator.ValidateAsync(
                datasetPath,
                null,
                null,
                null,
                verifyWithTests: false,
                progressCallback: null,
                manifestPath: Path.Combine(_tempDir, "nonexistent.manifest.json")));
    }

    [Fact]
    public async Task ValidateAsync_Implementation_PartialFailToPassOnBase_ReportsSpecificPassingTestNames()
    {
        var datasetPath = Path.Combine(_tempDir, "impl-partial-base.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"impl-partial","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc123","problem_statement":"Bug","patch":"diff gold","fail_to_pass":["Test.RealFailA","Test.AlreadyPasses","Test.RealFailB"]}
        """);

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "impl-partial",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = Path.Combine(_tempDir, "wks-partial"),
            BaseCommit = "abc123"
        };

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, null, ExecutionMode.ContainerSandbox, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailToPassPassed = 1,
                FailToPassTotal = 3,
                FailedTests = ["Test.RealFailA", "Test.RealFailB"]
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);

        Assert.False(report.AllPassed);
        var error = report.Results[0].ErrorMessage;
        Assert.NotNull(error);
        Assert.Contains("1/3 FailToPass test(s) already pass on BaseCommit without GoldPatch.", error);
        Assert.Contains("Passing test(s): Test.AlreadyPasses", error);
        Assert.DoesNotContain("Test.RealFailA", error);
        Assert.DoesNotContain("Test.RealFailB", error);
    }

    [Fact]
    public async Task ValidateAsync_WithConcurrencyGreaterThanOne_RunsInParallelPreservesOrderAndRejectsNonPositiveConcurrency()
    {
        var datasetPath = Path.Combine(_tempDir, "parallel.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"p-1","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug 1","patch":"diff1","fail_to_pass":["T1"]}""",
            """{"instance_id":"p-2","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug 2","patch":"diff2","fail_to_pass":["T2"]}""",
            """{"instance_id":"p-3","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug 3","patch":"diff3","fail_to_pass":["T3"]}""",
            """{"instance_id":"p-4","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug 4","patch":"diff4","fail_to_pass":["T4"]}"""
        ]));

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BenchmarkInstance inst, BenchmarkProblemCategory cat, string? _, CancellationToken _) => new BenchmarkWorkspaceHandle
            {
                InstanceId = inst.InstanceId,
                Category = cat,
                WorkspacePath = Path.Combine(_tempDir, inst.InstanceId),
                BaseCommit = "abc"
            });

        int active = 0;
        int peakActive = 0;
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.IsAny<BenchmarkInstance>(),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                It.IsAny<string?>(),
                ExecutionMode.ContainerSandbox,
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (BenchmarkInstance inst, BenchmarkWorkspaceHandle _, string? patch, ExecutionMode _, bool _, CancellationToken ct) =>
            {
                if (patch == null)
                {
                    int current = Interlocked.Increment(ref active);
                    while (true)
                    {
                        int snapshot = Volatile.Read(ref peakActive);
                        if (current <= snapshot || Interlocked.CompareExchange(ref peakActive, current, snapshot) == snapshot)
                        {
                            break;
                        }
                    }

                    if (current >= 2)
                    {
                        barrier.TrySetResult(true);
                    }

                    await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                    await Task.Delay(25, ct);
                    Interlocked.Decrement(ref active);

                    return new BenchmarkVerificationResult { Passed = false, FailToPassPassed = 0, FailToPassTotal = 1, FailedTests = inst.FailToPass };
                }

                return new BenchmarkVerificationResult { Passed = true, FailToPassPassed = 1, FailToPassTotal = 1 };
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null, concurrency: 0));

        var progress = new List<string>();
        var report = await validator.ValidateAsync(
            datasetPath,
            categoryOverride: null,
            instanceIdFilter: null,
            limit: null,
            verifyWithTests: true,
            progressCallback: progress.Add,
            concurrency: 3);

        Assert.True(report.AllPassed);
        Assert.Equal(4, report.PassedCount);
        Assert.True(peakActive >= 2, $"Expected parallel execution with peakActive >= 2, got {peakActive}.");
        Assert.Equal(["p-1", "p-2", "p-3", "p-4"], report.Results.Select(r => r.InstanceId).ToArray());
        Assert.Equal(4, progress.Count(p => p.StartsWith("[PASS]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ValidateAsync_ImplementationWithEmptyFailToPass_DoesNotFilterBaseRun()
    {
        var datasetPath = Path.Combine(_tempDir, "impl-empty-f2p.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"impl-nof2p","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"abc","problem_statement":"Bug","patch":"diff gold"}
        """);

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "impl-nof2p",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = Path.Combine(_tempDir, "wks-nof2p"),
            BaseCommit = "abc"
        };

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, null, ExecutionMode.ContainerSandbox, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = false });
        verifier
            .Setup(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, "diff gold", ExecutionMode.ContainerSandbox, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);

        Assert.True(report.AllPassed);
        verifier.Verify(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, null, ExecutionMode.ContainerSandbox, false, It.IsAny<CancellationToken>()), Times.Once);
        verifier.Verify(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, "diff gold", ExecutionMode.ContainerSandbox, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_Implementation_FailsWhenBaseVerificationHitsInfrastructureError()
    {
        var datasetPath = Path.Combine(_tempDir, "impl-infra-err.jsonl");
        await File.WriteAllTextAsync(datasetPath, """
        {"instance_id":"polly-1852","category":"Implementation","language":"csharp","repo":"App-vNext/Polly","base_commit":"abc","problem_statement":"Bug","patch":"diff gold","fail_to_pass":["TestA"]}
        """);

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "polly-1852",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = Path.Combine(_tempDir, "wks-infra"),
            BaseCommit = "abc"
        };

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, null, ExecutionMode.ContainerSandbox, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                IsInfrastructureError = true,
                FailToPassPassed = 0,
                FailToPassTotal = 1,
                ErrorMessage = "Verification infrastructure error: MSBuild child-node crash persisted after single-node (-m:1) retry (exit code 1)."
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);

        Assert.False(report.AllPassed);
        var result = Assert.Single(report.Results);
        Assert.False(result.Passed);
        Assert.Contains("infrastructure error", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        verifier.Verify(
            v => v.RunImplementationTestsAsync(It.IsAny<BenchmarkInstance>(), handle, "diff gold", ExecutionMode.ContainerSandbox, false, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_CodeReviewMinorFix_FailsWhenCandidateAlreadyPassesOrHitsInfraError_AndPassesWhenCandidateFailsAndGoldPasses()
    {
        var datasetPath = Path.Combine(_tempDir, "cr-minor-preflight.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"cr-minor-cand-passes","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand1","patch":"diff gold1","test_patch":"diff test1","expected_verdict":"MinorFix","fail_to_pass":["BugTest1"]}""",
            """{"instance_id":"cr-minor-no-f2p-passes","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand2","patch":"diff gold2","test_patch":"diff test2","expected_verdict":"MinorFix"}""",
            """{"instance_id":"cr-minor-cand-infra","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand3","patch":"diff gold3","test_patch":"diff test3","expected_verdict":"MinorFix","fail_to_pass":["BugTest3"]}""",
            """{"instance_id":"cr-minor-valid","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand4","patch":"diff gold4","test_patch":"diff test4","expected_verdict":"MinorFix","fail_to_pass":["BugTest4"],"pass_to_pass":["RegTest4"]}"""
        ]));

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BenchmarkInstance inst, BenchmarkProblemCategory cat, string? _, CancellationToken _) => new BenchmarkWorkspaceHandle
            {
                InstanceId = inst.InstanceId,
                Category = cat,
                WorkspacePath = Path.Combine(_tempDir, inst.InstanceId),
                BaseCommit = "abc",
                ReviewDiff = "diff --git a/A.cs b/A.cs\n+x"
            });

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-cand-passes"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand1",
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = true,
                FailToPassPassed = 1,
                FailToPassTotal = 1
            });

        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-no-f2p-passes"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand2",
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true });

        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-cand-infra"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand3",
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                IsInfrastructureError = true,
                ErrorMessage = "MSBuild child-node crash"
            });

        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-valid"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff cand4",
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                FailToPassPassed = 0,
                FailToPassTotal = 1,
                FailedTests = ["BugTest4"]
            });

        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-minor-valid"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                "diff gold4",
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = true,
                FailToPassPassed = 1,
                FailToPassTotal = 1,
                PassToPassPassed = 1,
                PassToPassTotal = 1
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);

        Assert.False(report.AllPassed);
        Assert.False(report.Results[0].Passed);
        Assert.Contains("already pass on CandidatePatch without GoldPatch", report.Results[0].ErrorMessage);
        Assert.Contains("Passing test(s): BugTest1", report.Results[0].ErrorMessage);

        Assert.False(report.Results[1].Passed);
        Assert.Contains("already passed on CandidatePatch without GoldPatch", report.Results[1].ErrorMessage);

        Assert.False(report.Results[2].Passed);
        Assert.Contains("infrastructure error", report.Results[2].ErrorMessage, StringComparison.OrdinalIgnoreCase);

        Assert.True(report.Results[3].Passed);
        Assert.Contains("CandidatePatch failed FailToPass", report.Results[3].Summary);

        verifier.Verify(
            v => v.RunImplementationTestsAsync(
                It.Is<BenchmarkInstance>(i => i.InstanceId != "cr-minor-valid"),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                It.Is<string?>(p => p != null && p.StartsWith("diff gold", StringComparison.Ordinal)),
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_PreFixPatchApplicationError_FailsInsteadOfTreatingAsExpectedPreFixFailure()
    {
        var datasetPath = Path.Combine(_tempDir, "patch-apply-error.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"cr-minor-with-gold-apply-err","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand1","patch":"diff gold1","test_patch":"diff test1","expected_verdict":"MinorFix","fail_to_pass":["BugTest1"]}""",
            """{"instance_id":"cr-minor-no-gold-apply-err","category":"CodeReview","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Review","candidate_patch":"diff cand2","test_patch":"diff test2","expected_verdict":"MinorFix","fail_to_pass":["BugTest2"]}""",
            """{"instance_id":"impl-base-apply-err","category":"Implementation","language":"csharp","repo":"o/r","base_commit":"abc","problem_statement":"Fix","patch":"diff gold3","test_patch":"diff test3","fail_to_pass":["BugTest3"]}"""
        ]));

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkProblemCategory>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BenchmarkInstance inst, BenchmarkProblemCategory cat, string? _, CancellationToken _) => new BenchmarkWorkspaceHandle
            {
                InstanceId = inst.InstanceId,
                Category = cat,
                WorkspacePath = Path.Combine(_tempDir, inst.InstanceId),
                BaseCommit = "abc",
                ReviewDiff = cat == BenchmarkProblemCategory.CodeReview ? "diff --git a/A.cs b/A.cs\n+x" : null
            });

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.RunImplementationTestsAsync(
                It.IsAny<BenchmarkInstance>(),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                It.Is<string?>(p => p == null || p.StartsWith("diff cand", StringComparison.Ordinal)),
                ExecutionMode.ContainerSandbox,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                IsPatchApplicationError = true,
                ErrorMessage = "Evaluation TestPatch failed to apply cleanly: corrupt patch"
            });

        var validator = new BenchmarkDatasetValidator(
            _loader,
            workspaceManager.Object,
            verifier.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<BenchmarkDatasetValidator>.Instance);

        var report = await validator.ValidateAsync(datasetPath, null, null, null, verifyWithTests: true, progressCallback: null);

        Assert.False(report.AllPassed);
        Assert.Equal(3, report.Results.Count);
        Assert.All(report.Results, r =>
        {
            Assert.False(r.Passed);
            Assert.Contains("patch application failed", r.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        });

        verifier.Verify(
            v => v.RunImplementationTestsAsync(
                It.IsAny<BenchmarkInstance>(),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                It.Is<string?>(p => p != null && p.StartsWith("diff gold", StringComparison.Ordinal)),
                ExecutionMode.ContainerSandbox,
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
