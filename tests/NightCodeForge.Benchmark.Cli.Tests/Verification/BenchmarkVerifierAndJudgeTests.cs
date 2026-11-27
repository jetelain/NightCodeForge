using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Tests.Verification;

public sealed class BenchmarkVerifierAndJudgeTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkVerifierAndJudgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_tempDir, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_Implementation_RunsContainerizedTestCommandParsesTrxAndCleansContainer()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-impl");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        const string trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="a" testName="BugFixTest" outcome="Passed" />
                <UnitTestResult testId="b" testName="RegressionTest" outcome="Passed" />
              </Results>
            </TestRun>
            """;

        containerFactory
            .Setup(f => f.CreateContainerEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        string? capturedCommand = null;
        execEnv
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, _, _, _, _) =>
            {
                capturedCommand = cmd;
                var resultsDir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                Directory.CreateDirectory(resultsDir);
                File.WriteAllText(Path.Combine(resultsDir, BenchmarkPaths.TrxFileName), trxXml);
            })
            .ReturnsAsync(new CommandResult(0, "Passed!", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "impl-1",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/repo",
            BaseCommit = "abc123",
            ProblemStatement = "Fix bug",
            TestPatch = "diff --git a/test.cs b/test.cs\n",
            FailToPass = ["BugFixTest"],
            PassToPass = ["RegressionTest"],
            CustomContainerImage = "custom-dotnet:v1"
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "impl-1",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = workspaceDir,
            BaseCommit = "abc123"
        };

        var outcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = "diff --git a/fix.cs b/fix.cs\n"
        };

        var result = await verifier.VerifyAsync(
            instance,
            handle,
            outcome,
            ExecutionMode.ContainerSandbox,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(result.Passed);
        Assert.Equal(1.0, result.DeterministicScore);
        Assert.Equal(1, result.FailToPassPassed);
        Assert.Equal(1, result.PassToPassPassed);
        Assert.Contains("--logger trx", capturedCommand);
        Assert.Contains(BenchmarkPaths.ContainerResultsDir, capturedCommand);
        containerFactory.Verify(f => f.CreateContainerEnvironmentAsync(
            It.Is<SandboxScopeDescriptor>(s => !s.MountHostDotnetTools && s.ContainerImage == "custom-dotnet:v1"),
            It.IsAny<CancellationToken>()), Times.Once);
        containerFactory.Verify(f => f.RemoveContainerAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()), Times.Once);
        workspaceManager.Verify(w => w.ResetToBaseAsync(workspaceDir, "abc123", It.IsAny<CancellationToken>()), Times.Once);
        workspaceManager.Verify(w => w.ApplyPatchAsync(workspaceDir, outcome.ModelPatch!, It.IsAny<CancellationToken>()), Times.Once);
        workspaceManager.Verify(w => w.ApplyPatchAsync(workspaceDir, instance.TestPatch!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_Implementation_UsesCustomTestCommandAndFailsWhenRegressionOccurs()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-py");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        const string junitXml = """
            <testsuite name="pytest">
              <testcase classname="suite" name="test_new" />
              <testcase classname="suite" name="test_existing"><failure>broken</failure></testcase>
            </testsuite>
            """;

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        string? capturedCmd = null;
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, _, _, _, _) =>
            {
                capturedCmd = cmd;
                var dir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "junit.xml"), junitXml);
            })
            .ReturnsAsync(new CommandResult(1, "1 failed", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "py-1",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.Python,
            Repo = "owner/py",
            BaseCommit = "abc",
            ProblemStatement = "Fix py",
            CustomTestCommand = "pytest -q --junitxml=.ncf-bench-results/junit.xml",
            TestResultFormat = TestResultFormat.JUnitXml,
            FailToPass = ["test_new"],
            PassToPass = ["test_existing"]
        };

        var result = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "py-1", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(result.Passed);
        Assert.Equal(0.0, result.DeterministicScore, 4);
        Assert.Equal("pytest -q --junitxml=.ncf-bench-results/junit.xml", capturedCmd);
        Assert.Contains("test_existing", result.FailedTests);
    }

    [Fact]
    public async Task VerifyAsync_CodeReview_ScoresVerdictFindingAnchorsAndOptionalJudge()
    {
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var judgeClientFactory = new Mock<IBenchmarkJudgeClientFactory>();
        var chatClient = new Mock<IChatClient>();

        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """
                ```json
                {"score": 0.92, "rationale": "Identified the null check regression accurately."}
                ```
                """)));
        judgeClientFactory.Setup(f => f.CreateJudgeClient(It.IsAny<BenchmarkJudgeConfig>())).Returns(chatClient.Object);

        var llmJudge = new BenchmarkLlmJudge(judgeClientFactory.Object, NullLogger<BenchmarkLlmJudge>.Instance);
        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            llmJudge,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "cr-1",
            Category = BenchmarkProblemCategory.CodeReview,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Check PR",
            CandidatePatch = "diff",
            ExpectedVerdict = "RequestChanges",
            ExpectedFindings =
            [
                new BenchmarkExpectedFinding("src/AuthService.cs", "ValidateToken", "high", "Null token check", ["null"])
            ]
        };

        var outcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ReviewerFeedback = "In src/AuthService.cs ValidateToken fails on null token input.",
            TargetRouting = StandardStageIds.Implementation
        };

        var judgeConfig = new BenchmarkJudgeConfig { ModelName = "judge-model" };
        var result = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "cr-1", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = _tempDir, BaseCommit = "abc" },
            outcome,
            ExecutionMode.BareHost,
            verifyWithTests: false,
            judgeConfig: judgeConfig);

        Assert.True(result.Passed);
        Assert.Equal("RequestChanges", result.ActualVerdict);
        Assert.Equal(1.0, result.DeterministicScore);
        Assert.Equal(0.92, result.JudgeScore);
        Assert.Contains("null check", result.JudgeRationale);
    }

    [Fact]
    public async Task VerifyAsync_TaskReview_EnforcesScopeFrontmatterDifficultyAndEnrichment()
    {
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "tr-1",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Plan caching feature",
            TaskSpecification = "---\nid: 1\nstatus: Open\ndifficulty: standard\n---\n\nAdd cache.",
            ExpectedVerdict = "Ready",
            ExpectedDifficulty = "difficult",
            ExpectedFindings = [new BenchmarkExpectedFinding("src/CacheService.cs", Keywords: ["TTL"])]
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "tr-1",
            Category = BenchmarkProblemCategory.TaskReview,
            WorkspacePath = _tempDir,
            BaseCommit = "abc"
        };

        var validOutcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            TaskMarkdownBefore = instance.TaskSpecification,
            UpdatedTaskMarkdown = "---\nid: 1\nstatus: Open\ndifficulty: difficult\n---\n\nAdd cache.\n\n## Architecture & Affected Files\nUpdate src/CacheService.cs to configure sliding TTL expiration and add unit tests covering cache eviction.",
            ModifiedFilesOutsideDocsTasks = Array.Empty<string>()
        };

        var validResult = await verifier.VerifyAsync(instance, handle, validOutcome, ExecutionMode.BareHost, false, null);
        Assert.True(validResult.Passed);
        Assert.Equal("Ready", validResult.ActualVerdict);
        Assert.Equal("difficult", validResult.TaskDifficultyResult);
        Assert.Equal(1.0, validResult.DeterministicScore);

        var outOfScopeOutcome = validOutcome with { ModifiedFilesOutsideDocsTasks = ["src/Program.cs"] };
        var scopeFail = await verifier.VerifyAsync(instance, handle, outOfScopeOutcome, ExecutionMode.BareHost, false, null);
        Assert.False(scopeFail.Passed);
        Assert.Equal(0.0, scopeFail.DeterministicScore);

        var tamperedFrontmatter = validOutcome with
        {
            UpdatedTaskMarkdown = "---\nid: 99\nstatus: Closed\ndifficulty: difficult\n---\n\nUpdated body long enough to satisfy enrichment length check in test."
        };
        var fmFail = await verifier.VerifyAsync(instance, handle, tamperedFrontmatter, ExecutionMode.BareHost, false, null);
        Assert.False(fmFail.Passed);
    }

    [Fact]
    public async Task LlmJudge_FailsFastOnMalformedJsonOrTransportFailure()
    {
        var factory = new Mock<IBenchmarkJudgeClientFactory>();
        var client = new Mock<IChatClient>();
        factory.Setup(f => f.CreateJudgeClient(It.IsAny<BenchmarkJudgeConfig>())).Returns(client.Object);

        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid json")));

        var judge = new BenchmarkLlmJudge(factory.Object, NullLogger<BenchmarkLlmJudge>.Instance);
        var instance = new BenchmarkInstance
        {
            InstanceId = "cr-bad",
            Repo = "a/b",
            BaseCommit = "c",
            ProblemStatement = "p",
            CandidatePatch = "diff",
            ExpectedVerdict = "Approve"
        };

        await Assert.ThrowsAsync<BenchmarkVerificationException>(() =>
            judge.EvaluateCodeReviewAsync(instance, new BenchmarkAgentOutcome(), new BenchmarkJudgeConfig { ModelName = "judge" }));

        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<BenchmarkVerificationException>(() =>
            judge.EvaluateTaskReviewAsync(instance, new BenchmarkAgentOutcome(), new BenchmarkJudgeConfig { ModelName = "judge" }));
    }

    [Fact]
    public async Task VerifyAsync_Implementation_MultiProjectTrx_BareHostWorkingDir_AndIgnoresStaleTestResults()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-multi-trx");
        Directory.CreateDirectory(workspaceDir);

        // Pre-existing stale TestResults/stale.trx from an earlier turn must be ignored when build fails
        var staleDir = Path.Combine(workspaceDir, "TestResults");
        Directory.CreateDirectory(staleDir);
        var staleFile = Path.Combine(staleDir, "stale.trx");
        const string staleTrx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="1" testName="BugFixTest" outcome="Passed" />
              </Results>
            </TestRun>
            """;
        await File.WriteAllTextAsync(staleFile, staleTrx);
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddMinutes(-10));

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        string? capturedCmd = null;
        string? capturedWorkDir = null;
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, wd, _, _, _) =>
            {
                capturedCmd = cmd;
                capturedWorkDir = wd;
            })
            .ReturnsAsync(new CommandResult(1, string.Empty, "build failed", TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "impl-stale",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Fix bug",
            FailToPass = ["BugFixTest"]
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "impl-stale",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = workspaceDir,
            BaseCommit = "abc"
        };

        var failedResult = await verifier.VerifyAsync(
            instance,
            handle,
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(failedResult.Passed);
        Assert.Equal(workspaceDir, capturedWorkDir);
        Assert.Contains($"--results-directory {BenchmarkPaths.BenchmarkResultDirRelative}", capturedCmd);
        Assert.DoesNotContain(BenchmarkPaths.ContainerResultsDir, capturedCmd);

        // Now simulate multi-project TRX output (two distinct .trx files in .ncf-bench-results)
        var multiInstance = instance with { FailToPass = ["ProjATest"], PassToPass = ["ProjBTest"] };
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, _, _, _) =>
            {
                var resultsDir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                File.WriteAllText(Path.Combine(resultsDir, "projA.trx"), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="a" testName="ProjATest" outcome="Passed" /></Results>
                    </TestRun>
                    """);
                File.WriteAllText(Path.Combine(resultsDir, "projB.trx"), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="b" testName="ProjBTest" outcome="Passed" /></Results>
                    </TestRun>
                    """);
            })
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var multiResult = await verifier.VerifyAsync(
            multiInstance,
            handle,
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(multiResult.Passed);
        Assert.Equal(1, multiResult.FailToPassPassed);
        Assert.Equal(1, multiResult.PassToPassPassed);
    }

    [Fact]
    public async Task VerifyAsync_Implementation_CorruptXmlAndAllSkippedAssertionFree_FailCleanlyWithoutThrowing()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-corrupt");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, _, _, _) =>
            {
                var resultsDir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                File.WriteAllText(Path.Combine(resultsDir, "corrupt.trx"), "<TestRun><broken");
            })
            .ReturnsAsync(new CommandResult(0, string.Empty, string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "corrupt-trx",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            FailToPass = ["Test1"]
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "corrupt-trx",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = workspaceDir,
            BaseCommit = "abc"
        };

        var corruptRes = await verifier.VerifyAsync(
            instance,
            handle,
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(corruptRes.Passed);
        Assert.Contains("Test1", corruptRes.FailedTests);

        // Assertion-free run where every discovered test was skipped must fail
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, _, _, _) =>
            {
                var resultsDir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                File.WriteAllText(Path.Combine(resultsDir, "skipped.trx"), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="s" testName="SkippedOnlyTest" outcome="Skipped" /></Results>
                    </TestRun>
                    """);
            })
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var allSkippedInstance = instance with { FailToPass = [], PassToPass = [] };
        var skippedRes = await verifier.VerifyAsync(
            allSkippedInstance,
            handle,
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(skippedRes.Passed);
        Assert.Contains("skipped", skippedRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_WslDirect_CleansUpVerificationScopeEnvironment()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-wsl");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var result = await verifier.VerifyAsync(
            new BenchmarkInstance { InstanceId = "wsl-1", Language = BenchmarkLanguages.CSharp, Repo = "a/b", BaseCommit = "c", ProblemStatement = "p" },
            new BenchmarkWorkspaceHandle { InstanceId = "wsl-1", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "c" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.WslDirect,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(result.Passed);
        envProvider.Verify(p => p.CleanupScopeEnvironmentAsync(
            It.Is<SandboxScopeDescriptor>(s => s.ExecutionMode == ExecutionMode.WslDirect),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_CodeReview_MinorFixPatchApplyFailure_ReturnsFailedVerificationResult()
    {
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager.Setup(m => m.ApplyPatchAsync(_tempDir, "bad-minor-patch", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("patch conflict"));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "cr-bad-patch",
            Category = BenchmarkProblemCategory.CodeReview,
            Repo = "a/b",
            BaseCommit = "c",
            ProblemStatement = "Fix minor bug",
            CandidatePatch = "cand-patch",
            ExpectedVerdict = "MinorFix",
            FailToPass = ["TestFix"]
        };

        var res = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "cr-bad-patch", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = _tempDir, BaseCommit = "c" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "bad-minor-patch" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(res.Passed);
        Assert.Equal("MinorFix", res.ActualVerdict);
        Assert.Contains("failed to apply cleanly", res.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_TaskReview_RejectsOneCharEditOnLongSpec_AndDeletedFrontmatter()
    {
        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var longBeforeBody = new string('x', BenchmarkDefaults.MinimumReadyBodyChars + 20);
        var beforeMarkdown = $"---\nid: 1\nstatus: Open\ndifficulty: standard\n---\n\n{longBeforeBody}";

        var instance = new BenchmarkInstance
        {
            InstanceId = "tr-trivial",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = "a/b",
            BaseCommit = "c",
            ProblemStatement = "Spec",
            TaskSpecification = beforeMarkdown,
            ExpectedVerdict = "Ready"
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "tr-trivial",
            Category = BenchmarkProblemCategory.TaskReview,
            WorkspacePath = _tempDir,
            BaseCommit = "c"
        };

        // 1-char edit on already >=80-char spec must fail non-trivial enrichment
        var oneCharOutcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            TaskMarkdownBefore = beforeMarkdown,
            UpdatedTaskMarkdown = $"---\nid: 1\nstatus: Open\ndifficulty: standard\n---\n\n{longBeforeBody}."
        };

        var trivialRes = await verifier.VerifyAsync(instance, handle, oneCharOutcome, ExecutionMode.BareHost, false, null);
        Assert.False(trivialRes.Passed);
        Assert.Contains("non-trivial", trivialRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Deleted YAML frontmatter block must fail frontmatter preservation
        var deletedFmOutcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            TaskMarkdownBefore = beforeMarkdown,
            UpdatedTaskMarkdown = longBeforeBody + "\n\n" + new string('y', BenchmarkDefaults.MinimumEnrichedBodyDeltaChars + 50)
        };

        var deletedFmRes = await verifier.VerifyAsync(instance, handle, deletedFmOutcome, ExecutionMode.BareHost, false, null);
        Assert.False(deletedFmRes.Passed);
    }

    [Fact]
    public void EvaluateExpectedFindings_SummaryOnlyAnchor_DoesNotMatchEmptyCorpus_AndMatchesSignificantTerms()
    {
        var findings = new[]
        {
            new BenchmarkExpectedFinding(Summary: "Missing null validation on authentication token")
        };

        var (emptyScore, _) = BenchmarkVerifier.EvaluateExpectedFindings(findings, string.Empty);
        Assert.Equal(0.0, emptyScore);

        var (matchedScore, _) = BenchmarkVerifier.EvaluateExpectedFindings(
            findings,
            "The token validation logic lacks a null check during authentication.");
        Assert.Equal(1.0, matchedScore);
    }

    [Fact]
    public async Task LlmJudge_DisposesCreatedChatClientOnSuccessAndFailure()
    {
        var factory = new Mock<IBenchmarkJudgeClientFactory>();
        var client = new Mock<IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"score":0.8,"rationale":"good"}""")));
        factory.Setup(f => f.CreateJudgeClient(It.IsAny<BenchmarkJudgeConfig>())).Returns(client.Object);

        var judge = new BenchmarkLlmJudge(factory.Object, NullLogger<BenchmarkLlmJudge>.Instance);
        var instance = new BenchmarkInstance { InstanceId = "j1", Repo = "a/b", BaseCommit = "c", ProblemStatement = "p", CandidatePatch = "d", ExpectedVerdict = "Approve" };

        await judge.EvaluateCodeReviewAsync(instance, new BenchmarkAgentOutcome(), new BenchmarkJudgeConfig { ModelName = "m" });
        client.As<IDisposable>().Verify(d => d.Dispose(), Times.Once);

        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<BenchmarkVerificationException>(() =>
            judge.EvaluateCodeReviewAsync(instance, new BenchmarkAgentOutcome(), new BenchmarkJudgeConfig { ModelName = "m" }));
        client.As<IDisposable>().Verify(d => d.Dispose(), Times.Exactly(2));
    }

    [Fact]
    public void LlmJudge_ParseJudgeResponse_StripsThinkTagContainingBraces()
    {
        const string responseWithThink = """
            <think>
            Drafting score: {"score": 0.1, "rationale": "incomplete draft"}
            Wait, let me reconsider carefully.
            </think>
            ```json
            {"score": 0.85, "rationale": "Accurate review."}
            ```
            """;

        var parsed = BenchmarkLlmJudge.ParseJudgeResponse(responseWithThink, "inst-think");

        Assert.Equal(0.85, parsed.Score, 3);
        Assert.Equal("Accurate review.", parsed.Rationale);
    }

    [Fact]
    public void DefaultBenchmarkJudgeClientFactory_ResolveOpenAiApiKey_IgnoresWhitespaceEnvVars()
    {
        var withFallbackToOpenAi = DefaultBenchmarkJudgeClientFactory.ResolveOpenAiApiKey(
            explicitKey: "   ",
            name => name switch
            {
                BenchmarkCliConstants.BenchmarkApiKeyEnvVar => "  \t ",
                BenchmarkCliConstants.OpenAiApiKeyEnvVar => " sk-real-key ",
                _ => null
            });
        Assert.Equal("sk-real-key", withFallbackToOpenAi);

        var withAllBlank = DefaultBenchmarkJudgeClientFactory.ResolveOpenAiApiKey(
            explicitKey: null,
            name => name switch
            {
                BenchmarkCliConstants.BenchmarkApiKeyEnvVar => "",
                BenchmarkCliConstants.OpenAiApiKeyEnvVar => "   ",
                _ => null
            });
        Assert.Equal(KnownDefaultModels.LocalAuthBypassKey, withAllBlank);

        var withExplicit = DefaultBenchmarkJudgeClientFactory.ResolveOpenAiApiKey(
            explicitKey: " sk-explicit ",
            _ => "sk-ignored");
        Assert.Equal("sk-explicit", withExplicit);
    }

    [Fact]
    public async Task VerifyAsync_Implementation_DiscoversCustomCommandNestedTrxAndRootJunit_IgnoringBuildDirs()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-custom-scan");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        // Nested subproject TRX written by a CustomTestCommand, plus a failing decoy inside obj/
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, _, _, _) =>
            {
                var nestedDir = Path.Combine(workspaceDir, "tests", "SubProject.Tests", "TestResults");
                Directory.CreateDirectory(nestedDir);
                File.WriteAllText(Path.Combine(nestedDir, "results.trx"), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="1" testName="SubProjTest" outcome="Passed" /></Results>
                    </TestRun>
                    """);

                var objDir = Path.Combine(workspaceDir, "obj");
                Directory.CreateDirectory(objDir);
                File.WriteAllText(Path.Combine(objDir, "decoy.trx"), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="1" testName="SubProjTest" outcome="Failed" /></Results>
                    </TestRun>
                    """);
            })
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var trxInstance = new BenchmarkInstance
        {
            InstanceId = "custom-nested-trx",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "dotnet test --results-directory tests/SubProject.Tests/TestResults",
            FailToPass = ["SubProjTest"]
        };

        var trxResult = await verifier.VerifyAsync(
            trxInstance,
            new BenchmarkWorkspaceHandle { InstanceId = "custom-nested-trx", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(trxResult.Passed);
        Assert.Equal(1, trxResult.FailToPassPassed);

        // Workspace root junit.xml with failing decoy XML in obj/
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, _, _, _) =>
            {
                File.WriteAllText(Path.Combine(workspaceDir, "junit.xml"), """
                    <testsuite name="pytest">
                      <testcase classname="suite" name="test_py" />
                    </testsuite>
                    """);
                File.WriteAllText(Path.Combine(workspaceDir, "obj", "doc.xml"), """
                    <testsuite name="decoy">
                      <testcase classname="suite" name="test_py"><failure>decoy</failure></testcase>
                    </testsuite>
                    """);
            })
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var pyInstance = new BenchmarkInstance
        {
            InstanceId = "custom-root-junit",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.Python,
            Repo = "owner/py",
            BaseCommit = "abc",
            ProblemStatement = "Fix py",
            CustomTestCommand = "pytest --junitxml=junit.xml",
            TestResultFormat = TestResultFormat.JUnitXml,
            FailToPass = ["test_py"]
        };

        var pyResult = await verifier.VerifyAsync(
            pyInstance,
            new BenchmarkWorkspaceHandle { InstanceId = "custom-root-junit", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(pyResult.Passed);
        Assert.Equal(1, pyResult.FailToPassPassed);
    }

    [Fact]
    public async Task VerifyAsync_Implementation_NonZeroExitWithNoParsedOutcomes_IncludesExitCodeInErrorMessage()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-nonzero-empty");
        Directory.CreateDirectory(workspaceDir);

        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(2, string.Empty, "compile failed", TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            envProvider.Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "nonzero-empty",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            FailToPass = ["TestA"]
        };

        var res = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "nonzero-empty", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(res.Passed);
        Assert.Contains("exited with code 2", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TestA", res.ErrorMessage);
    }

    [Fact]
    public void ParseTaskMarkdown_RejectsMalformedFencesAndPreservesTrailingWhitespaceFenceHandling()
    {
        var malformedOpen = BenchmarkVerifier.ParseTaskMarkdown("----extra\nid: 1\n---\n\nBody");
        Assert.Null(malformedOpen.Frontmatter);

        var malformedClose = BenchmarkVerifier.ParseTaskMarkdown("---\nid: 1\n---extra\nBody");
        Assert.Null(malformedClose.Frontmatter);

        var whitespaceFences = BenchmarkVerifier.ParseTaskMarkdown("---   \nid: 42\ntitle: \"Valid\"\nstatus: Open\n---   \n\nBody content");
        Assert.NotNull(whitespaceFences.Frontmatter);
        Assert.Equal(42, whitespaceFences.Frontmatter.Id);
        Assert.Equal("Open", whitespaceFences.Frontmatter.Status);
        Assert.Equal("Body content", whitespaceFences.Body);
    }

    [Fact]
    public async Task VerifyAsync_Implementation_ResetsTestPatchPathsSetsDotNetRollForwardAndCatchesTestPatchCollision()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-tp-reset");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        IReadOnlyDictionary<string, string>? capturedEnvVars = null;
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((_, _, _, envVars, _) =>
            {
                capturedEnvVars = envVars;
                var dir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, BenchmarkPaths.TrxFileName), """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testId="1" testName="NewGoldTest" outcome="Passed" /></Results>
                    </TestRun>
                    """);
            })
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            envProvider.Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        const string testPatch = """
            diff --git a/tests/NewGoldTest.cs b/tests/NewGoldTest.cs
            new file mode 100644
            --- /dev/null
            +++ b/tests/NewGoldTest.cs
            @@ -0,0 +1 @@
            +class NewGoldTest {}
            """;

        var instance = new BenchmarkInstance
        {
            InstanceId = "cs-reset-1",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/repo",
            BaseCommit = "base1",
            ProblemStatement = "Fix",
            TestPatch = testPatch,
            FailToPass = ["NewGoldTest"]
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "cs-reset-1",
            Category = BenchmarkProblemCategory.Implementation,
            WorkspacePath = workspaceDir,
            BaseCommit = "base1"
        };

        var passResult = await verifier.VerifyAsync(
            instance,
            handle,
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff model" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(passResult.Passed);
        Assert.NotNull(capturedEnvVars);
        Assert.Equal(BenchmarkDefaults.DotNetRollForwardLatestMajor, capturedEnvVars[BenchmarkDefaults.DotNetRollForwardEnvVar]);
        workspaceManager.Verify(w => w.ResetPathsToBaseAsync(
            workspaceDir,
            "base1",
            It.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "tests/NewGoldTest.cs" })),
            It.IsAny<CancellationToken>()), Times.Once);

        // If TestPatch still fails to apply after path reset, verifier returns Passed = false rather than throwing
        workspaceManager
            .Setup(w => w.ApplyPatchAsync(workspaceDir, testPatch, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("patch conflict"));

        var failResult = await verifier.VerifyAsync(
            instance,
            handle,
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff model" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(failResult.Passed);
        Assert.Contains("Evaluation TestPatch failed to apply cleanly", failResult.ErrorMessage);
    }

    [Fact]
    public void CreateVerificationScope_DisablesAcpMountsAndBlocksCheatHosts()
    {
        var scope = BenchmarkVerifier.CreateVerificationScope("C:/tmp/wks", "custom:img", ExecutionMode.ContainerSandbox, "ncf-bench-verify-1");
        Assert.False(scope.MountHostDotnetTools);
        Assert.False(scope.MountAcpHostPaths);
        Assert.False(scope.DisableNetwork);
        Assert.Equal(BenchmarkDefaults.BlockedAntiCheatHosts, scope.BlockedHosts);
    }

    [Fact]
    public void DotNetGlobalJsonResilienceScope_RelaxesSdkRollForwardPreservesOtherKeysAndRestoresExactBytes()
    {
        var wks = Path.Combine(_tempDir, "wks-global-json");
        Directory.CreateDirectory(wks);
        var subDir = Path.Combine(wks, "nested");
        Directory.CreateDirectory(subDir);

        var rootGlobalJson = Path.Combine(wks, "global.json");
        const string originalRootContent = "{\r\n  \"sdk\": {\r\n    \"version\": \"7.0.102\",\r\n    \"rollForward\": \"latestFeature\"\r\n  },\r\n  \"msbuild-sdks\": {\r\n    \"My.Sdk\": \"1.0.0\"\r\n  }\r\n}\r\n";
        File.WriteAllText(rootGlobalJson, originalRootContent);

        var nestedGlobalJson = Path.Combine(subDir, "global.json");
        const string originalNestedContent = "{\n  \"sdk\": {\n    \"version\": \"8.0.100\"\n  }\n}\n";
        File.WriteAllText(nestedGlobalJson, originalNestedContent);

        // Non-C# languages must be a no-op
        using (var pyScope = DotNetGlobalJsonResilienceScope.Apply(wks, BenchmarkLanguages.Python, NullLogger.Instance))
        {
            Assert.Equal(0, pyScope.ModifiedFileCount);
            Assert.Equal(originalRootContent, File.ReadAllText(rootGlobalJson));
        }

        using (var csScope = DotNetGlobalJsonResilienceScope.Apply(wks, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(2, csScope.ModifiedFileCount);
            var relaxedRoot = File.ReadAllText(rootGlobalJson);
            Assert.Contains("\"latestMajor\"", relaxedRoot);
            Assert.Contains("\"7.0.102\"", relaxedRoot);
            Assert.Contains("\"msbuild-sdks\"", relaxedRoot);
            Assert.Contains("\"latestMajor\"", File.ReadAllText(nestedGlobalJson));
        }

        // Exact original bytes (including CRLF formatting) restored on dispose
        Assert.Equal(originalRootContent, File.ReadAllText(rootGlobalJson));
        Assert.Equal(originalNestedContent, File.ReadAllText(nestedGlobalJson));
    }

    [Fact]
    public void BenchmarkTestCommandResolver_DiscoversSubdirectorySolutionsTestProjectsAndVitestConfigs()
    {
        var slnWorkspace = Path.Combine(_tempDir, "wks-sub-sln");
        Directory.CreateDirectory(Path.Combine(slnWorkspace, "src"));
        File.WriteAllText(Path.Combine(slnWorkspace, "src", "Spectre.Console.sln"), "");

        var csharpDefaultInstance = new BenchmarkInstance
        {
            InstanceId = "spectre-1",
            Language = BenchmarkLanguages.CSharp,
            Repo = "spectreconsole/spectre.console",
            BaseCommit = "abc",
            ProblemStatement = "Fix"
        };
        var defaultSlnCmd = BenchmarkVerifier.ResolveTestCommand(csharpDefaultInstance, slnWorkspace, ExecutionMode.ContainerSandbox);
        Assert.Contains("dotnet build src/Spectre.Console.sln --nologo", defaultSlnCmd);
        Assert.Contains("dotnet test src/Spectre.Console.sln --no-build --logger trx --results-directory /workspace/.ncf-bench-results", defaultSlnCmd);

        var bareCustomSlnInstance = csharpDefaultInstance with
        {
            CustomTestCommand = "dotnet test --nologo --logger trx --results-directory /workspace/.ncf-bench-results"
        };
        var customSlnCmd = BenchmarkVerifier.ResolveTestCommand(bareCustomSlnInstance, slnWorkspace, ExecutionMode.ContainerSandbox);
        Assert.Equal("dotnet test src/Spectre.Console.sln --nologo --logger trx --results-directory /workspace/.ncf-bench-results", customSlnCmd);

        // Fallback to test .csproj discovery when no .sln exists
        var csprojWorkspace = Path.Combine(_tempDir, "wks-sub-csproj");
        Directory.CreateDirectory(Path.Combine(csprojWorkspace, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(csprojWorkspace, "test", "Lib.Tests"));
        File.WriteAllText(Path.Combine(csprojWorkspace, "src", "Lib", "Lib.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(csprojWorkspace, "test", "Lib.Tests", "Lib.Tests.csproj"), "<Project />");

        var csprojCmd = BenchmarkVerifier.ResolveTestCommand(csharpDefaultInstance, csprojWorkspace, ExecutionMode.BareHost);
        Assert.Contains("dotnet build test/Lib.Tests/Lib.Tests.csproj --nologo", csprojCmd);
        Assert.DoesNotContain("Lib.csproj --nologo", csprojCmd);

        // Vitest unit config injection for both default and bare custom commands
        var vueWorkspace = Path.Combine(_tempDir, "wks-vue");
        Directory.CreateDirectory(vueWorkspace);
        File.WriteAllText(Path.Combine(vueWorkspace, "package.json"), "{\"devDependencies\":{\"vitest\":\"^2.0.0\"}}");
        File.WriteAllText(Path.Combine(vueWorkspace, "vitest.unit.config.ts"), "export default {}");

        var vitestDefaultInstance = new BenchmarkInstance
        {
            InstanceId = "vue-1",
            Language = BenchmarkLanguages.TypeScript,
            Repo = "vuejs/core",
            BaseCommit = "abc",
            ProblemStatement = "Fix"
        };
        var vitestDefaultCmd = BenchmarkVerifier.ResolveTestCommand(vitestDefaultInstance, vueWorkspace, ExecutionMode.ContainerSandbox);
        Assert.Contains("npx vitest run -c vitest.unit.config.ts --reporter=junit", vitestDefaultCmd);

        var vitestCustomInstance = vitestDefaultInstance with
        {
            CustomTestCommand = "npx vitest run --reporter=junit --outputFile=/workspace/.ncf-bench-results/junit.xml"
        };
        var vitestCustomCmd = BenchmarkVerifier.ResolveTestCommand(vitestCustomInstance, vueWorkspace, ExecutionMode.ContainerSandbox);
        Assert.Equal("npx vitest run -c vitest.unit.config.ts --reporter=junit --outputFile=/workspace/.ncf-bench-results/junit.xml", vitestCustomCmd);

        // Explicit -c / --config must be preserved without duplication
        var vitestExplicitConfigInstance = vitestDefaultInstance with
        {
            CustomTestCommand = "npx vitest run -c vitest.e2e.config.ts --reporter=junit"
        };
        Assert.Equal(
            "npx vitest run -c vitest.e2e.config.ts --reporter=junit",
            BenchmarkVerifier.ResolveTestCommand(vitestExplicitConfigInstance, vueWorkspace, ExecutionMode.ContainerSandbox));
    }

    [Fact]
    public async Task VerifyAsync_ContainerSandbox_WrapsPythonAndNodeDependencySetup_AndRestoresGlobalJson()
    {
        var pyWks = Path.Combine(_tempDir, "wks-wrap-py");
        var nodeWks = Path.Combine(_tempDir, "wks-wrap-node");
        var csWks = Path.Combine(_tempDir, "wks-wrap-cs");
        Directory.CreateDirectory(pyWks);
        Directory.CreateDirectory(nodeWks);
        Directory.CreateDirectory(csWks);

        var globalJsonPath = Path.Combine(csWks, "global.json");
        const string originalGlobalJson = "{\"sdk\":{\"version\":\"7.0.102\",\"rollForward\":\"latestFeature\"}}";
        await File.WriteAllTextAsync(globalJsonPath, originalGlobalJson);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        containerFactory
            .Setup(f => f.CreateContainerEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        string? capturedCmd = null;
        string? globalJsonDuringExec = null;
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, _, _, _, _) =>
            {
                capturedCmd = cmd;
                if (File.Exists(globalJsonPath))
                {
                    globalJsonDuringExec = File.ReadAllText(globalJsonPath);
                }
            })
            .ReturnsAsync(new CommandResult(0, "ok", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        await verifier.VerifyAsync(
            new BenchmarkInstance { InstanceId = "py-wrap", Language = BenchmarkLanguages.Python, Repo = "psf/requests", BaseCommit = "c", ProblemStatement = "p" },
            new BenchmarkWorkspaceHandle { InstanceId = "py-wrap", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = pyWks, BaseCommit = "c" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.ContainerSandbox,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.Contains("/tmp/ncf-bench-venv", capturedCmd);
        Assert.Contains("pip install -q pytest", capturedCmd);
        Assert.Contains("pytest --junitxml=/workspace/.ncf-bench-results/junit.xml", capturedCmd);

        await verifier.VerifyAsync(
            new BenchmarkInstance
            {
                InstanceId = "node-wrap",
                Language = BenchmarkLanguages.JavaScript,
                Repo = "axios/axios",
                BaseCommit = "c",
                ProblemStatement = "p",
                CustomTestCommand = "npm run test:mocha",
                TestResultFormat = TestResultFormat.ExitCode
            },
            new BenchmarkWorkspaceHandle { InstanceId = "node-wrap", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = nodeWks, BaseCommit = "c" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.ContainerSandbox,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.Contains("pnpm-lock.yaml", capturedCmd);
        Assert.Contains("npm ci || npm ci --ignore-scripts || npm install --ignore-scripts", capturedCmd);
        Assert.EndsWith("&& { npm run test:mocha; }", capturedCmd);

        await verifier.VerifyAsync(
            new BenchmarkInstance { InstanceId = "cs-wrap", Language = BenchmarkLanguages.CSharp, Repo = "serilog/serilog", BaseCommit = "c", ProblemStatement = "p" },
            new BenchmarkWorkspaceHandle { InstanceId = "cs-wrap", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = csWks, BaseCommit = "c" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.ContainerSandbox,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.NotNull(globalJsonDuringExec);
        Assert.Contains("\"latestMajor\"", globalJsonDuringExec);
        Assert.Equal(originalGlobalJson, await File.ReadAllTextAsync(globalJsonPath));
    }

    [Fact]
    public void BenchmarkTestCommandResolver_HandlesPriorityMultipleTargetsShellGuardsAndQuoting()
    {
        // Low-priority solutions/test projects must not beat real deeper solutions/test projects
        var prioritySlnWks = Path.Combine(_tempDir, "wks-prio-sln");
        Directory.CreateDirectory(Path.Combine(prioritySlnWks, "samples"));
        Directory.CreateDirectory(Path.Combine(prioritySlnWks, "src", "MyLib"));
        File.WriteAllText(Path.Combine(prioritySlnWks, "samples", "Demo.sln"), "");
        File.WriteAllText(Path.Combine(prioritySlnWks, "src", "MyLib", "MyLib.sln"), "");

        var csharpInstance = new BenchmarkInstance
        {
            InstanceId = "cs-prio",
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/mylib",
            BaseCommit = "abc",
            ProblemStatement = "Fix"
        };

        var slnCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, prioritySlnWks, ExecutionMode.ContainerSandbox);
        Assert.Contains("src/MyLib/MyLib.sln", slnCmd);
        Assert.DoesNotContain("samples/Demo.sln", slnCmd);

        // Multi-target test project discovery: exclude low-priority sample tests and run all remaining test projects
        var multiProjWks = Path.Combine(_tempDir, "wks-multi-proj");
        Directory.CreateDirectory(Path.Combine(multiProjWks, "samples", "Sample.Tests"));
        Directory.CreateDirectory(Path.Combine(multiProjWks, "test", "Core.Tests"));
        Directory.CreateDirectory(Path.Combine(multiProjWks, "test", "Ext.Tests"));
        File.WriteAllText(Path.Combine(multiProjWks, "samples", "Sample.Tests", "Sample.Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(multiProjWks, "test", "Core.Tests", "Core.Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(multiProjWks, "test", "Ext.Tests", "Ext.Tests.csproj"), "<Project />");

        var posixMultiCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, multiProjWks, ExecutionMode.ContainerSandbox);
        Assert.Contains("ncf_rc=0;", posixMultiCmd);
        Assert.Contains("test/Core.Tests/Core.Tests.csproj", posixMultiCmd);
        Assert.Contains("test/Ext.Tests/Ext.Tests.csproj", posixMultiCmd);
        Assert.DoesNotContain("Sample.Tests.csproj", posixMultiCmd);

        var bareMultiCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, multiProjWks, ExecutionMode.BareHost);
        Assert.DoesNotContain("ncf_rc=0", bareMultiCmd);
        Assert.Contains(" && ", bareMultiCmd);

        // Custom command guards: positional target or unquoted shell operator is preserved; quoted filter '|' is rewritten
        var positionalCustom = csharpInstance with { CustomTestCommand = "dotnet test tests/UnitTests --logger trx" };
        Assert.Equal(
            "dotnet test tests/UnitTests --logger trx",
            BenchmarkTestCommandResolver.ResolveTestCommand(positionalCustom, prioritySlnWks, ExecutionMode.ContainerSandbox));

        var chainedCustom = csharpInstance with { CustomTestCommand = "dotnet test --nologo && echo done" };
        Assert.Equal(
            "dotnet test --nologo && echo done",
            BenchmarkTestCommandResolver.ResolveTestCommand(chainedCustom, prioritySlnWks, ExecutionMode.ContainerSandbox));

        var quotedFilterCustom = csharpInstance with { CustomTestCommand = "dotnet test --nologo --filter \"Name~A|Name~B\"" };
        Assert.Equal(
            "dotnet test src/MyLib/MyLib.sln --nologo --filter \"Name~A|Name~B\"",
            BenchmarkTestCommandResolver.ResolveTestCommand(quotedFilterCustom, prioritySlnWks, ExecutionMode.ContainerSandbox));

        // Node wrapping: isolated jest-junit prefix + NODE_PATH, global npm bin guard, and safe compound grouping
        var wrappedJest = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "JEST_JUNIT_OUTPUT_DIR=/workspace/.ncf-bench-results npx jest --reporters=jest-junit; ",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        Assert.Contains("/tmp/ncf-npm-global/bin", wrappedJest);
        Assert.Contains("if [ -z \"${npm_config_cache:-}\" ] || [ ! -w \"${npm_config_cache}\" ]; then mkdir -p /tmp/ncf-npm-cache && export npm_config_cache=\"/tmp/ncf-npm-cache\"; fi", wrappedJest);
        Assert.Contains("npm install --prefix /tmp/ncf-jest-junit --legacy-peer-deps --omit=peer --ignore-scripts --no-audit --no-fund --no-save jest-junit", wrappedJest);
        Assert.DoesNotContain("--cache /tmp/ncf-npm-cache", wrappedJest);
        Assert.Contains("NODE_PATH=\"$PWD/node_modules:/tmp/ncf-jest-junit/node_modules", wrappedJest);
        Assert.DoesNotContain("npm install --no-save jest-junit", wrappedJest);
        Assert.EndsWith("&& { JEST_JUNIT_OUTPUT_DIR=/workspace/.ncf-bench-results npx jest --reporters=jest-junit; }", wrappedJest);

        // Python wrapping: safe compound grouping
        var wrappedPy = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "pytest -q; ",
            BenchmarkLanguages.Python,
            ExecutionMode.ContainerSandbox);
        Assert.EndsWith("&& { pytest -q; }", wrappedPy);

        // Safe quoting for spaces and shell metacharacters
        Assert.Equal("\"dir with spaces/My App.sln\"", BenchmarkTestCommandResolver.QuotePathArgument("dir with spaces/My App.sln"));
        Assert.Equal("\"dir/App\\$1.sln\"", BenchmarkTestCommandResolver.QuotePathArgument("dir/App$1.sln"));
        Assert.Equal("src/App.sln", BenchmarkTestCommandResolver.QuotePathArgument("src/App.sln"));
    }

    [Fact]
    public void DotNetGlobalJsonResilienceScope_HandlesBomCommentsEdgeCasesAndAgentEdits()
    {
        var wks = Path.Combine(_tempDir, "wks-gj-edge");
        Directory.CreateDirectory(wks);
        Directory.CreateDirectory(Path.Combine(wks, "bom"));
        Directory.CreateDirectory(Path.Combine(wks, "already"));
        Directory.CreateDirectory(Path.Combine(wks, "agent-edited"));
        Directory.CreateDirectory(Path.Combine(wks, "agent-deleted"));
        Directory.CreateDirectory(Path.Combine(wks, "invalid"));
        Directory.CreateDirectory(Path.Combine(wks, "duplicate"));
        Directory.CreateDirectory(Path.Combine(wks, "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(wks, "node_modules", "pkg"));

        var bomPath = Path.Combine(wks, "bom", "global.json");
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(
            System.Text.Encoding.UTF8.GetBytes("{\n  // pinned sdk\n  \"sdk\": { \"version\": \"6.0.400\", \"rollForward\": \"patch\", }\n}\n"))
            .ToArray();
        File.WriteAllBytes(bomPath, bomBytes);

        var alreadyPath = Path.Combine(wks, "already", "global.json");
        const string alreadyContent = "{\"sdk\":{\"version\":\"9.0.100\",\"rollForward\":\"latestMajor\"}}";
        File.WriteAllText(alreadyPath, alreadyContent);

        var agentEditedPath = Path.Combine(wks, "agent-edited", "global.json");
        File.WriteAllText(agentEditedPath, "{\"sdk\":{\"version\":\"7.0.100\"}}");

        var agentDeletedPath = Path.Combine(wks, "agent-deleted", "global.json");
        File.WriteAllText(agentDeletedPath, "{\"sdk\":{\"version\":\"7.0.100\"}}");

        File.WriteAllText(Path.Combine(wks, "invalid", "global.json"), "{ not valid json");
        File.WriteAllText(Path.Combine(wks, "duplicate", "global.json"), "{\"sdk\":{\"version\":\"7.0.100\",\"version\":\"8.0.100\"}}");
        var binIgnoredPath = Path.Combine(wks, "bin", "Debug", "global.json");
        const string binOriginal = "{\"sdk\":{\"version\":\"7.0.100\"}}";
        File.WriteAllText(binIgnoredPath, binOriginal);
        var nodeModulesIgnoredPath = Path.Combine(wks, "node_modules", "pkg", "global.json");
        File.WriteAllText(nodeModulesIgnoredPath, binOriginal);

        const string agentReplacement = "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"minor\"}}";
        using (var scope = DotNetGlobalJsonResilienceScope.Apply(wks, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(3, scope.ModifiedFileCount);
            Assert.Contains("\"latestMajor\"", File.ReadAllText(bomPath));
            Assert.Equal(alreadyContent, File.ReadAllText(alreadyPath));
            Assert.Equal(binOriginal, File.ReadAllText(binIgnoredPath));
            Assert.Equal(binOriginal, File.ReadAllText(nodeModulesIgnoredPath));

            // Simulate agent modifying one file and deleting another
            File.WriteAllText(agentEditedPath, agentReplacement);
            File.Delete(agentDeletedPath);
        }

        Assert.Equal(bomBytes, File.ReadAllBytes(bomPath));
        Assert.Equal(agentReplacement, File.ReadAllText(agentEditedPath));
        Assert.False(File.Exists(agentDeletedPath));
    }

    [Fact]
    public void BenchmarkTestCommandResolver_DeprioritizesDocsScriptsToolsAndPrefersSolutionsWithTests()
    {
        var csharpInstance = new BenchmarkInstance
        {
            InstanceId = "spectreconsole__spectre-console-1458",
            Language = BenchmarkLanguages.CSharp,
            Repo = "spectreconsole/spectre.console",
            BaseCommit = "abc",
            ProblemStatement = "Fix"
        };

        // docs/Docs.sln must lose to src/Spectre.Console.sln even though docs < src alphabetically
        var docsVsSrcWks = Path.Combine(_tempDir, "wks-docs-vs-src");
        Directory.CreateDirectory(Path.Combine(docsVsSrcWks, "docs"));
        Directory.CreateDirectory(Path.Combine(docsVsSrcWks, "src"));
        File.WriteAllText(Path.Combine(docsVsSrcWks, "docs", "Docs.sln"), "");
        File.WriteAllText(Path.Combine(docsVsSrcWks, "src", "Spectre.Console.sln"), "");

        var spectreCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, docsVsSrcWks, ExecutionMode.ContainerSandbox);
        Assert.Contains("src/Spectre.Console.sln", spectreCmd);
        Assert.DoesNotContain("docs/Docs.sln", spectreCmd);

        // doc/, resources/, scripts/, tools/ are also deprioritized, while MyDocuments/MyDocuments.sln is NOT
        foreach (var lowDir in new[] { "doc", "resources", "scripts", "tools" })
        {
            var wks = Path.Combine(_tempDir, $"wks-low-{lowDir}");
            Directory.CreateDirectory(Path.Combine(wks, lowDir));
            Directory.CreateDirectory(Path.Combine(wks, "src", "MyDocuments"));
            File.WriteAllText(Path.Combine(wks, lowDir, "Aaa.sln"), "");
            File.WriteAllText(Path.Combine(wks, "src", "MyDocuments", "MyDocuments.sln"), "");

            var cmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, wks, ExecutionMode.ContainerSandbox);
            Assert.Contains("src/MyDocuments/MyDocuments.sln", cmd);
            Assert.DoesNotContain($"{lowDir}/Aaa.sln", cmd);
        }

        // Solution referencing test projects beats an alphabetically earlier solution without tests (.sln)
        var testAwareSlnWks = Path.Combine(_tempDir, "wks-test-aware-sln");
        Directory.CreateDirectory(Path.Combine(testAwareSlnWks, "a_build"));
        Directory.CreateDirectory(Path.Combine(testAwareSlnWks, "z_src"));
        File.WriteAllText(Path.Combine(testAwareSlnWks, "a_build", "Build.sln"), """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "BuildTools", "BuildTools\BuildTools.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        File.WriteAllText(Path.Combine(testAwareSlnWks, "z_src", "App.sln"), """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App.Tests", "..\tests\App.Tests\App.Tests.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var testAwareSlnCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, testAwareSlnWks, ExecutionMode.ContainerSandbox);
        Assert.Contains("z_src/App.sln", testAwareSlnCmd);
        Assert.DoesNotContain("a_build/Build.sln", testAwareSlnCmd);

        // Solution referencing test projects beats an alphabetically earlier solution without tests (.slnx)
        var testAwareSlnxWks = Path.Combine(_tempDir, "wks-test-aware-slnx");
        Directory.CreateDirectory(Path.Combine(testAwareSlnxWks, "a_build"));
        Directory.CreateDirectory(Path.Combine(testAwareSlnxWks, "z_src"));
        File.WriteAllText(Path.Combine(testAwareSlnxWks, "a_build", "Build.slnx"), """
            <Solution>
              <Project Path="BuildTools\BuildTools.csproj" />
            </Solution>
            """);
        File.WriteAllText(Path.Combine(testAwareSlnxWks, "z_src", "App.slnx"), """
            <Solution>
              <Project Path="../test/App.Tests/App.Tests.csproj" />
            </Solution>
            """);

        var testAwareSlnxCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, testAwareSlnxWks, ExecutionMode.ContainerSandbox);
        Assert.Contains("z_src/App.slnx", testAwareSlnxCmd);
        Assert.DoesNotContain("a_build/Build.slnx", testAwareSlnxCmd);

        // Single-quoted Path attributes in .slnx are also recognized for test-project detection
        var singleQuoteSlnxWks = Path.Combine(_tempDir, "wks-single-quote-slnx");
        Directory.CreateDirectory(Path.Combine(singleQuoteSlnxWks, "a_build"));
        Directory.CreateDirectory(Path.Combine(singleQuoteSlnxWks, "z_src"));
        File.WriteAllText(Path.Combine(singleQuoteSlnxWks, "a_build", "Build.slnx"), """
            <Solution>
              <Project Path='BuildTools\BuildTools.csproj' />
            </Solution>
            """);
        File.WriteAllText(Path.Combine(singleQuoteSlnxWks, "z_src", "App.slnx"), """
            <Solution>
              <Project Path='../test/App.Tests/App.Tests.csproj' />
            </Solution>
            """);

        var singleQuoteSlnxCmd = BenchmarkTestCommandResolver.ResolveTestCommand(csharpInstance, singleQuoteSlnxWks, ExecutionMode.ContainerSandbox);
        Assert.Contains("z_src/App.slnx", singleQuoteSlnxCmd);
        Assert.DoesNotContain("a_build/Build.slnx", singleQuoteSlnxCmd);
    }

    [Fact]
    public void BenchmarkTestCommandResolver_ResolvesMochaJUnitAndPythonPinnedRequirementsBeforePytestFallback()
    {
        // Default Mocha command resolution (test:mocha script vs bare mocha)
        var mochaScriptWks = Path.Combine(_tempDir, "wks-mocha-script");
        Directory.CreateDirectory(mochaScriptWks);
        File.WriteAllText(Path.Combine(mochaScriptWks, "package.json"), """{"scripts":{"test:mocha":"mocha test/unit/**/*.js"},"devDependencies":{"mocha":"^10.0.0"}}""");

        var jsDefaultInstance = new BenchmarkInstance
        {
            InstanceId = "axios__axios-5892",
            Language = BenchmarkLanguages.JavaScript,
            Repo = "axios/axios",
            BaseCommit = "abc",
            ProblemStatement = "Fix"
        };

        var defaultMochaCmd = BenchmarkTestCommandResolver.ResolveTestCommand(jsDefaultInstance, mochaScriptWks, ExecutionMode.ContainerSandbox);
        Assert.Equal("MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml npm run test:mocha -- --reporter mocha-junit-reporter", defaultMochaCmd);

        var bareMochaWks = Path.Combine(_tempDir, "wks-mocha-bare");
        Directory.CreateDirectory(bareMochaWks);
        File.WriteAllText(Path.Combine(bareMochaWks, "package.json"), """{"devDependencies":{"mocha":"^10.0.0"}}""");
        Assert.Equal(
            "MOCHA_FILE=.ncf-bench-results/junit.xml npx mocha --reporter mocha-junit-reporter",
            BenchmarkTestCommandResolver.ResolveTestCommand(jsDefaultInstance, bareMochaWks, ExecutionMode.BareHost));

        // Custom Mocha command rewritten only when format is JUnitXml; ExitCode preserved
        var junitCustom = jsDefaultInstance with
        {
            TestResultFormat = TestResultFormat.JUnitXml,
            CustomTestCommand = "npm run test:mocha"
        };
        Assert.Equal(
            "MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml npm run test:mocha -- --reporter mocha-junit-reporter",
            BenchmarkTestCommandResolver.ResolveTestCommand(junitCustom, mochaScriptWks, ExecutionMode.ContainerSandbox));

        var exitCodeCustom = jsDefaultInstance with
        {
            TestResultFormat = TestResultFormat.ExitCode,
            CustomTestCommand = "npm run test:mocha"
        };
        Assert.Equal(
            "npm run test:mocha",
            BenchmarkTestCommandResolver.ResolveTestCommand(exitCodeCustom, mochaScriptWks, ExecutionMode.ContainerSandbox));

        // Custom Mocha commands with leading env vars preserve existing MOCHA_FILE or prepend default
        var customMochaFileCmd = jsDefaultInstance with
        {
            TestResultFormat = TestResultFormat.JUnitXml,
            CustomTestCommand = "MOCHA_FILE=/custom/junit.xml npm run test:mocha"
        };
        Assert.Equal(
            "MOCHA_FILE=/custom/junit.xml npm run test:mocha -- --reporter mocha-junit-reporter",
            BenchmarkTestCommandResolver.ResolveTestCommand(customMochaFileCmd, mochaScriptWks, ExecutionMode.ContainerSandbox));

        var customNodeEnvCmd = jsDefaultInstance with
        {
            TestResultFormat = TestResultFormat.JUnitXml,
            CustomTestCommand = "NODE_ENV=test npm run test:mocha -- --timeout 5000"
        };
        Assert.Equal(
            "MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml NODE_ENV=test npm run test:mocha -- --timeout 5000 --reporter mocha-junit-reporter",
            BenchmarkTestCommandResolver.ResolveTestCommand(customNodeEnvCmd, mochaScriptWks, ExecutionMode.ContainerSandbox));

        var customNpxMochaFileCmd = jsDefaultInstance with
        {
            TestResultFormat = TestResultFormat.JUnitXml,
            CustomTestCommand = "MOCHA_FILE=/custom/junit.xml npx mocha test/unit"
        };
        Assert.Equal(
            "MOCHA_FILE=/custom/junit.xml npx mocha test/unit --reporter mocha-junit-reporter",
            BenchmarkTestCommandResolver.ResolveTestCommand(customNpxMochaFileCmd, mochaScriptWks, ExecutionMode.ContainerSandbox));

        // Container wrapping for mocha-junit-reporter provisions isolated prefix & NODE_PATH
        var wrappedMocha = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            defaultMochaCmd,
            BenchmarkLanguages.JavaScript,
            ExecutionMode.ContainerSandbox);
        Assert.Contains("if [ -z \"${npm_config_cache:-}\" ] || [ ! -w \"${npm_config_cache}\" ]; then mkdir -p /tmp/ncf-npm-cache && export npm_config_cache=\"/tmp/ncf-npm-cache\"; fi", wrappedMocha);
        Assert.Contains("npm install --prefix /tmp/ncf-mocha-junit --legacy-peer-deps --omit=peer --ignore-scripts --no-audit --no-fund --no-save mocha-junit-reporter", wrappedMocha);
        Assert.DoesNotContain("--cache /tmp/ncf-npm-cache", wrappedMocha);
        Assert.Contains("NODE_PATH=\"$PWD/node_modules:/tmp/ncf-mocha-junit/node_modules", wrappedMocha);

        // Python container wrapping installs pinned requirement files before conditional unpinned pytest fallback
        var wrappedPy = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "pytest --junitxml=/workspace/.ncf-bench-results/junit.xml",
            BenchmarkLanguages.Python,
            ExecutionMode.ContainerSandbox);
        int reqIdx = wrappedPy.IndexOf("requirements/tests.txt", StringComparison.Ordinal);
        int editableIdx = wrappedPy.IndexOf("pip install -q -e .", StringComparison.Ordinal);
        int fallbackIdx = wrappedPy.IndexOf("if [ ! -x /tmp/ncf-bench-venv/bin/pytest ]; then /tmp/ncf-bench-venv/bin/pip install -q pytest; fi", StringComparison.Ordinal);
        Assert.True(reqIdx > 0 && editableIdx > reqIdx && fallbackIdx > editableIdx,
            $"Expected requirements -> editable -> conditional pytest fallback ordering in: {wrappedPy}");
    }

    [Fact]
    public void DotNetGlobalJsonResilienceScope_HidesModifiedTrackedFileFromGitStatusAndDiffAndRestoresCleanIndex()
    {
        var repoDir = Path.Combine(_tempDir, "wks-git-global-json");
        Directory.CreateDirectory(repoDir);

        const string originalGlobalJson = "{\n  \"sdk\": {\n    \"version\": \"7.0.102\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\n";
        var globalJsonPath = Path.Combine(repoDir, "global.json");
        File.WriteAllText(globalJsonPath, originalGlobalJson);

        RunGit(repoDir, "init");
        RunGit(repoDir, "config", "user.email", "test@example.com");
        RunGit(repoDir, "config", "user.name", "Test User");
        RunGit(repoDir, "add", "global.json");
        RunGit(repoDir, "commit", "-m", "Initial commit");

        using (var scope = DotNetGlobalJsonResilienceScope.Apply(repoDir, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(1, scope.ModifiedFileCount);
            Assert.Contains("\"latestMajor\"", File.ReadAllText(globalJsonPath));
            Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "status", "--porcelain")));
            Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "diff")));
        }

        Assert.Equal(originalGlobalJson, File.ReadAllText(globalJsonPath));
        Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "status", "--porcelain")));
        Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "diff")));
        var lsFiles = RunGit(repoDir, "ls-files", "-v", "global.json").Trim();
        Assert.StartsWith("H ", lsFiles, StringComparison.Ordinal);

        // If the agent edits global.json during the scope, disposal must clear assume-unchanged so the edit is visible
        const string agentEdit = "{\n  \"sdk\": {\n    \"version\": \"9.0.100\",\n    \"rollForward\": \"minor\"\n  }\n}\n";
        using (var editScope = DotNetGlobalJsonResilienceScope.Apply(repoDir, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(1, editScope.ModifiedFileCount);
            File.WriteAllText(globalJsonPath, agentEdit);
        }

        Assert.Equal(agentEdit, File.ReadAllText(globalJsonPath));
        Assert.Contains("global.json", RunGit(repoDir, "status", "--porcelain"));
        Assert.Contains("9.0.100", RunGit(repoDir, "diff"));
        var lsFilesAfterEdit = RunGit(repoDir, "ls-files", "-v", "global.json").Trim();
        Assert.StartsWith("H ", lsFilesAfterEdit, StringComparison.Ordinal);

        // Nested tracked global.json should also be hidden during scope and restored cleanly
        var nestedDir = Path.Combine(repoDir, "src");
        Directory.CreateDirectory(nestedDir);
        var nestedGlobalJsonPath = Path.Combine(nestedDir, "global.json");
        File.WriteAllText(nestedGlobalJsonPath, originalGlobalJson);
        File.WriteAllText(globalJsonPath, originalGlobalJson);
        RunGit(repoDir, "add", "global.json", "src/global.json");
        RunGit(repoDir, "commit", "-m", "Add nested global.json");

        using (var nestedScope = DotNetGlobalJsonResilienceScope.Apply(repoDir, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(2, nestedScope.ModifiedFileCount);
            Assert.Contains("\"latestMajor\"", File.ReadAllText(nestedGlobalJsonPath));
            Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "status", "--porcelain")));
            Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "diff")));
        }

        Assert.Equal(originalGlobalJson, File.ReadAllText(nestedGlobalJsonPath));
        Assert.True(string.IsNullOrWhiteSpace(RunGit(repoDir, "status", "--porcelain")));
        Assert.StartsWith("H ", RunGit(repoDir, "ls-files", "-v", "src/global.json").Trim(), StringComparison.Ordinal);

        // Untracked global.json in a git repo relaxes and restores cleanly without throwing
        var untrackedDir = Path.Combine(repoDir, "untracked");
        Directory.CreateDirectory(untrackedDir);
        var untrackedGlobalJsonPath = Path.Combine(untrackedDir, "global.json");
        File.WriteAllText(untrackedGlobalJsonPath, originalGlobalJson);

        using (var untrackedScope = DotNetGlobalJsonResilienceScope.Apply(repoDir, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(3, untrackedScope.ModifiedFileCount);
            Assert.Contains("\"latestMajor\"", File.ReadAllText(untrackedGlobalJsonPath));
        }

        Assert.Equal(originalGlobalJson, File.ReadAllText(untrackedGlobalJsonPath));
        File.Delete(untrackedGlobalJsonPath);

        // Deleted tracked global.json during scope stays deleted on dispose and clears assume-unchanged
        using (var deleteScope = DotNetGlobalJsonResilienceScope.Apply(repoDir, BenchmarkLanguages.CSharp, NullLogger.Instance))
        {
            Assert.Equal(2, deleteScope.ModifiedFileCount);
            File.Delete(globalJsonPath);
        }

        Assert.False(File.Exists(globalJsonPath));
        Assert.Contains(" D global.json", RunGit(repoDir, "status", "--porcelain"));
        Assert.StartsWith("H ", RunGit(repoDir, "ls-files", "-v", "global.json").Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyCodeReviewVerdict_TreatsWhitespaceOnlyPatchesAsApprove_AndSubstantiveOrRejectionAppropriately()
    {
        const string lineWrapPatch = """
            diff --git a/test/Serilog.Tests/Capturing/PropertyValueConverterTests.cs b/test/Serilog.Tests/Capturing/PropertyValueConverterTests.cs
            index b863df7a9..123456789 100644
            --- a/test/Serilog.Tests/Capturing/PropertyValueConverterTests.cs
            +++ b/test/Serilog.Tests/Capturing/PropertyValueConverterTests.cs
            @@ -492,4 +492,6 @@ public class PropertyValueConverterTests
                 [Fact]
                 public void PrivateGettersAreIgnoredWhenCapturing()
                 {
            -        var structure = _converter.CreateStructureValue(new HasPropertyWithPrivateGetter(), typeof(HasPropertyWithPrivateGetter), false);
            +        var structure = _converter.CreateStructureValue(
            +            new HasPropertyWithPrivateGetter(),
            +            typeof(HasPropertyWithPrivateGetter),
            +            false);
                     Assert.Contains(structure.Properties, p => p.Name == "A");
                 }
            """;

        const string trailingNewlineOnlyPatch = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 1111111..2222222 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,2 +1,3 @@
             class Foo
            -}
            \ No newline at end of file
            +}
            +
            """;

        const string substantivePatch = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 1111111..3333333 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,2 +1,2 @@
             class Foo
            -{ return 1; }
            +{ return 2; }
            """;

        const string newFileOnlyPatch = """
            diff --git a/src/Empty.cs b/src/Empty.cs
            new file mode 100644
            index 0000000..e69de29
            --- /dev/null
            +++ b/src/Empty.cs
            """;

        const string reorderedAcrossContextPatch = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 1111111..2222222 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -1,5 +1,5 @@
            -StepOne();
             Middle();
            +StepOne();
            """;

        const string tripleDashAndPlusInsideHunkPatch = """
            diff --git a/README.md b/README.md
            index 1111111..2222222 100644
            --- a/README.md
            +++ b/README.md
            @@ -1,3 +1,3 @@
             Title
            --- old horizontal rule
            +++ new heading
            """;

        const string copyFilePatch = """
            diff --git a/src/A.cs b/src/B.cs
            similarity index 100%
            copy from src/A.cs
            copy to src/B.cs
            """;

        const string stringLiteralWhitespacePatch = """
            diff --git a/src/Strings.cs b/src/Strings.cs
            index 1111111..2222222 100644
            --- a/src/Strings.cs
            +++ b/src/Strings.cs
            @@ -1,3 +1,3 @@
            -var a = "hello world";
            -var b = 'a b';
            +var a = "helloworld";
            +var b = 'a  b';
            """;

        const string identifierMergePatch = """
            diff --git a/src/Tokens.cs b/src/Tokens.cs
            index 1111111..2222222 100644
            --- a/src/Tokens.cs
            +++ b/src/Tokens.cs
            @@ -1,2 +1,2 @@
            -return x;
            +returnx;
            """;

        const string rustLifetimeWrapPatch = """
            diff --git a/src/lib.rs b/src/lib.rs
            index 1111111..2222222 100644
            --- a/src/lib.rs
            +++ b/src/lib.rs
            @@ -1,2 +1,4 @@
            -fn foo<'a>(x: &'a str) -> &'a str { x }
            +fn foo<'a>(
            +    x: &'a str
            +) -> &'a str { x }
            """;

        const string rustCharLiteralChangePatch = """
            diff --git a/src/lib.rs b/src/lib.rs
            index 1111111..2222222 100644
            --- a/src/lib.rs
            +++ b/src/lib.rs
            @@ -1,2 +1,2 @@
            -let sep = ' ';
            +let sep = '\t';
            """;

        const string pythonIndentChangePatch = """
            diff --git a/src/app.py b/src/app.py
            index 1111111..2222222 100644
            --- a/src/app.py
            +++ b/src/app.py
            @@ -1,3 +1,3 @@
             if cond:
            -    return x
            +return x
            """;

        const string pythonWhitespaceOnlyPatch = """
            diff --git a/src/app.py b/src/app.py
            index 1111111..2222222 100644
            --- a/src/app.py
            +++ b/src/app.py
            @@ -1,3 +1,4 @@
             if cond:
            -    x=1   
            +    x = 1
            +
            """;

        Assert.True(BenchmarkVerifier.IsWhitespaceOnlyPatch(lineWrapPatch));
        Assert.Equal(CodeReviewVerdict.Approve, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = lineWrapPatch
        }));

        Assert.True(BenchmarkVerifier.IsWhitespaceOnlyPatch(trailingNewlineOnlyPatch));
        Assert.Equal(CodeReviewVerdict.Approve, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = trailingNewlineOnlyPatch
        }));

        Assert.True(BenchmarkVerifier.IsWhitespaceOnlyPatch(rustLifetimeWrapPatch));
        Assert.Equal(CodeReviewVerdict.Approve, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = rustLifetimeWrapPatch
        }));

        Assert.True(BenchmarkVerifier.IsWhitespaceOnlyPatch(pythonWhitespaceOnlyPatch));
        Assert.Equal(CodeReviewVerdict.Approve, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = pythonWhitespaceOnlyPatch
        }));

        foreach (var substantive in new[]
        {
            substantivePatch,
            newFileOnlyPatch,
            reorderedAcrossContextPatch,
            tripleDashAndPlusInsideHunkPatch,
            copyFilePatch,
            stringLiteralWhitespacePatch,
            identifierMergePatch,
            rustCharLiteralChangePatch,
            pythonIndentChangePatch
        })
        {
            Assert.False(BenchmarkVerifier.IsWhitespaceOnlyPatch(substantive));
            Assert.Equal(CodeReviewVerdict.MinorFix, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
            {
                RunStatus = AgentRunStatus.Completed,
                ModelPatch = substantive
            }));
        }

        Assert.Equal(CodeReviewVerdict.RequestChanges, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = lineWrapPatch,
            ReviewerFeedback = "Missing regression tests."
        }));

        Assert.Equal(CodeReviewVerdict.RequestChanges, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = lineWrapPatch,
            TargetRouting = StandardStageIds.Implementation
        }));

        Assert.Equal(CodeReviewVerdict.RequestChanges, BenchmarkVerifier.ClassifyCodeReviewVerdict(new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ModelPatch = lineWrapPatch,
            ReviewerRejectionReason = "Missing regression tests."
        }));
    }

    [Fact]
    public void DiscoverDotNetTargets_PrefersMainSolutionOverAuxiliarySolutions_AndPreservesAnalyzerCsprojFallback()
    {
        var spectreDir = Path.Combine(_tempDir, "spectre");
        Directory.CreateDirectory(Path.Combine(spectreDir, "src", "Spectre.Console"));
        Directory.CreateDirectory(Path.Combine(spectreDir, "src", "Spectre.Console.Tests"));
        Directory.CreateDirectory(Path.Combine(spectreDir, "src", "Spectre.Console.Analyzer"));
        Directory.CreateDirectory(Path.Combine(spectreDir, "src", "Spectre.Console.Analyzer.Tests"));

        File.WriteAllText(Path.Combine(spectreDir, "src", "Spectre.Console.Analyzer.sln"),
            """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console.Analyzer", "Spectre.Console.Analyzer\Spectre.Console.Analyzer.csproj", "{11111111-1111-1111-1111-111111111111}"
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console.Analyzer.Tests", "Spectre.Console.Analyzer.Tests\Spectre.Console.Analyzer.Tests.csproj", "{22222222-2222-2222-2222-222222222222}"
            """);
        File.WriteAllText(Path.Combine(spectreDir, "src", "Spectre.Console.sln"),
            """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console", "Spectre.Console\Spectre.Console.csproj", "{33333333-3333-3333-3333-333333333333}"
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Spectre.Console.Tests", "Spectre.Console.Tests\Spectre.Console.Tests.csproj", "{44444444-4444-4444-4444-444444444444}"
            """);
        File.WriteAllText(Path.Combine(spectreDir, "src", "Spectre.Console.Sandbox.sln"),
            """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sandbox.Tests", "Sandbox.Tests\Sandbox.Tests.csproj", "{55555555-5555-5555-5555-555555555555}"
            """);

        var targets = BenchmarkTestCommandResolver.DiscoverDotNetTargets(spectreDir);
        Assert.Equal(["src/Spectre.Console.sln"], targets);

        // Tie-breaker by test project count then total project count when neither is auxiliary
        var countDir = Path.Combine(_tempDir, "counts");
        Directory.CreateDirectory(Path.Combine(countDir, "sub"));
        File.WriteAllText(Path.Combine(countDir, "sub", "A.Small.sln"),
            """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core", "Core.csproj", "{1}"
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core.Tests", "Core.Tests.csproj", "{2}"
            """);
        File.WriteAllText(Path.Combine(countDir, "sub", "Z.Full.sln"),
            """
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core", "Core.csproj", "{1}"
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Extra", "Extra.csproj", "{2}"
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Core.Tests", "Core.Tests.csproj", "{3}"
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Extra.Tests", "Extra.Tests.csproj", "{4}"
            """);
        Assert.Equal(["sub/Z.Full.sln"], BenchmarkTestCommandResolver.DiscoverDotNetTargets(countDir));

        // No .sln/.slnx fallback must preserve analyzer test .csproj projects
        var csprojOnlyDir = Path.Combine(_tempDir, "analyzer-csproj-only");
        Directory.CreateDirectory(Path.Combine(csprojOnlyDir, "tests", "Spectre.Console.Analyzer.Tests"));
        File.WriteAllText(Path.Combine(csprojOnlyDir, "tests", "Spectre.Console.Analyzer.Tests", "Spectre.Console.Analyzer.Tests.csproj"), "<Project />");
        Assert.Equal(["tests/Spectre.Console.Analyzer.Tests/Spectre.Console.Analyzer.Tests.csproj"],
            BenchmarkTestCommandResolver.DiscoverDotNetTargets(csprojOnlyDir));
    }

    [Fact]
    public void WrapWithContainerDependencySetup_Node_UsesIgnoreScriptsFallbackAndDetectsPartialNodeModules()
    {
        var wrappedYarn = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "JEST_JUNIT_OUTPUT_DIR=/workspace/.ncf-bench-results npx jest --ci --reporters=jest-junit",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);

        Assert.Contains("(yarn install --frozen-lockfile || yarn install --frozen-lockfile --ignore-scripts || yarn install --ignore-scripts)", wrappedYarn);
        Assert.Contains("(pnpm install --frozen-lockfile || pnpm install --frozen-lockfile --ignore-scripts || pnpm install --ignore-scripts)", wrappedYarn);
        Assert.Contains("(npm ci || npm ci --ignore-scripts || npm install --ignore-scripts)", wrappedYarn);
        Assert.Contains("(npm install || npm install --ignore-scripts)", wrappedYarn);
        Assert.Contains("if [ -z \"${npm_config_cache:-}\" ] || [ ! -w \"${npm_config_cache}\" ]; then mkdir -p /tmp/ncf-npm-cache && export npm_config_cache=\"/tmp/ncf-npm-cache\"; fi", wrappedYarn);
        Assert.Contains("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund yarn", wrappedYarn);
        Assert.Contains("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund pnpm", wrappedYarn);
        Assert.Contains("npm install --prefix /tmp/ncf-jest-junit --legacy-peer-deps --omit=peer --ignore-scripts --no-audit --no-fund --no-save jest-junit", wrappedYarn);
        Assert.DoesNotContain("--cache /tmp/ncf-npm-cache", wrappedYarn);
        Assert.Contains("ncf_node_deps_ready=0 && if [ -d node_modules ]; then", wrappedYarn);
        Assert.DoesNotContain("ncf_node_deps_ready=0;", wrappedYarn);
        Assert.Contains("node_modules/.ncf-bench-deps-ok", wrappedYarn);
        Assert.Contains("node_modules/.bin", wrappedYarn);
        Assert.Contains("node_modules/.yarn-integrity", wrappedYarn);
        Assert.Contains("node_modules/.yarn-state.yml", wrappedYarn);
        Assert.Contains("node_modules/.modules.yaml", wrappedYarn);
        Assert.Contains("node_modules/.package-lock.json", wrappedYarn);
        Assert.DoesNotContain("[ ! -d node_modules ]", wrappedYarn);
    }

    [Fact]
    public async Task EvaluateExpectedFindings_AndVerifyCodeReview_HandleSymbolCorroborationRejectionReasonAndApproveTotals()
    {
        // ncf-cr-csharp-008 regression: omits literal filename RetryHelper.cs, but matches DecorrelatedJitterBackoffV2 + majority keywords
        var pollyFindings = new[]
        {
            new BenchmarkExpectedFinding(
                "src/Polly.Core/Retry/RetryHelper.cs",
                "DecorrelatedJitterBackoffV2",
                "high",
                "Checks double.IsNaN instead of double.IsInfinity so +Infinity overflows to long.MinValue where Math.Abs throws OverflowException, and PR omits tests",
                ["IsInfinity", "IsNaN", "Math.Abs", "OverflowException", "test"])
        };

        var qwenCorpus = "DecorrelatedJitterBackoffV2 checks double.IsNaN instead of double.IsInfinity, causing Inf - Inf = NaN and (long)NaN = long.MinValue where Math.Abs throws OverflowException. Missing RetryHelperTests.cs unit tests.";
        var (score, missing) = BenchmarkVerifier.EvaluateExpectedFindings(pollyFindings, qwenCorpus);
        Assert.Equal(1.0, score);
        Assert.Empty(missing);

        // Single weak generic keyword + symbol without filename must NOT pass corroboration
        var weakCorpus = "DecorrelatedJitterBackoffV2 needs a test.";
        var (weakScore, weakMissing) = BenchmarkVerifier.EvaluateExpectedFindings(pollyFindings, weakCorpus);
        Assert.Equal(0.0, weakScore);
        Assert.Single(weakMissing);

        // Findings without symbol_or_line_hint (e.g. TaskReview) still strictly require file_path
        var taskFindings = new[]
        {
            new BenchmarkExpectedFinding("src/CacheService.cs", Keywords: ["TTL", "eviction", "sliding"])
        };
        var (noSymbolScore, _) = BenchmarkVerifier.EvaluateExpectedFindings(taskFindings, "Add sliding TTL eviction policy.");
        Assert.Equal(0.0, noSymbolScore);

        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        // Approve review with non-empty FailToPass/PassToPass reports 0/0 when tests are skipped
        var approveInstance = new BenchmarkInstance
        {
            InstanceId = "ncf-cr-csharp-001",
            Category = BenchmarkProblemCategory.CodeReview,
            Repo = "serilog/serilog",
            BaseCommit = "abc",
            ProblemStatement = "Review PR",
            CandidatePatch = "diff",
            ExpectedVerdict = "Approve",
            FailToPass = ["TestA", "TestB"],
            PassToPass = ["TestC"]
        };
        var approveResult = await verifier.VerifyAsync(
            approveInstance,
            new BenchmarkWorkspaceHandle { InstanceId = "ncf-cr-csharp-001", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = _tempDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, Summary = "Looks good." },
            ExecutionMode.ContainerSandbox,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(approveResult.Passed);
        Assert.Equal(0, approveResult.FailToPassTotal);
        Assert.Equal(0, approveResult.FailToPassPassed);
        Assert.Equal(0, approveResult.PassToPassTotal);
        Assert.Equal(0, approveResult.PassToPassPassed);

        // ReviewerRejectionReason is propagated into evaluated corpus and triggers RequestChanges with 0/0 test totals
        var rejectInstance = new BenchmarkInstance
        {
            InstanceId = "ncf-cr-csharp-008",
            Category = BenchmarkProblemCategory.CodeReview,
            Repo = "App-vNext/Polly",
            BaseCommit = "abc",
            ProblemStatement = "Review PR",
            CandidatePatch = "diff",
            ExpectedVerdict = "RequestChanges",
            ExpectedFindings = pollyFindings,
            FailToPass = ["TestOverflow"]
        };
        var rejectResult = await verifier.VerifyAsync(
            rejectInstance,
            new BenchmarkWorkspaceHandle { InstanceId = "ncf-cr-csharp-008", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = _tempDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome
            {
                RunStatus = AgentRunStatus.Completed,
                ReviewerFeedback = "DecorrelatedJitterBackoffV2 fails on overflow.",
                ReviewerRejectionReason = "Checks double.IsNaN instead of double.IsInfinity; Math.Abs throws OverflowException and missing test.",
                TargetRouting = StandardStageIds.Implementation
            },
            ExecutionMode.ContainerSandbox,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(rejectResult.Passed);
        Assert.Equal("RequestChanges", rejectResult.ActualVerdict);
        Assert.Equal(1.0, rejectResult.DeterministicScore);
        Assert.Equal(0, rejectResult.FailToPassTotal);
        Assert.Equal(0, rejectResult.PassToPassTotal);
    }

    [Theory]
    [InlineData("expert", "expert", true, 1.0)]
    [InlineData("expert", "difficult", true, 0.95)]
    [InlineData("difficult", "expert", true, 0.95)]
    [InlineData("expert", "standard", false, 0.80)]
    [InlineData("difficult", "standard", false, 0.80)]
    [InlineData("standard", "difficult", false, 0.80)]
    [InlineData("standard", "expert", false, 0.80)]
    public async Task VerifyAsync_TaskReview_CalibratesDifficultyScoringAcrossExactAdjacentAndMismatchedTiers(
        string expectedDifficulty,
        string actualDifficulty,
        bool expectedPassed,
        double expectedScore)
    {
        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "tr-calib",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = "vuejs/core",
            BaseCommit = "abc",
            ProblemStatement = "Fix batchDepth",
            TaskSpecification = "---\nid: 202\nstatus: Open\ndifficulty: standard\n---\n\nOriginal task.",
            ExpectedVerdict = "Ready",
            ExpectedDifficulty = expectedDifficulty,
            ExpectedFindings = [new BenchmarkExpectedFinding("packages/reactivity/src/effect.ts", Keywords: ["endBatch"])]
        };

        var outcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            TaskMarkdownBefore = instance.TaskSpecification,
            UpdatedTaskMarkdown = $"---\nid: 202\nstatus: Open\ndifficulty: {actualDifficulty}\n---\n\nOriginal task.\n\n## Architecture & Plan\nUpdate endBatch in packages/reactivity/src/effect.ts so batchDepth decrements cleanly before draining queued effects.",
            ModifiedFilesOutsideDocsTasks = Array.Empty<string>()
        };

        var result = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "tr-calib", Category = BenchmarkProblemCategory.TaskReview, WorkspacePath = _tempDir, BaseCommit = "abc" },
            outcome,
            ExecutionMode.BareHost,
            verifyWithTests: false,
            judgeConfig: null);

        Assert.Equal(expectedPassed, result.Passed);
        Assert.Equal(expectedScore, result.DeterministicScore, 4);
    }

    [Fact]
    public void EvaluateTaskDifficulty_RejectsNumericEnumStrings()
    {
        Assert.Equal((false, 0.0), BenchmarkVerifier.EvaluateTaskDifficulty("expert", "2"));
        Assert.Equal((false, 0.0), BenchmarkVerifier.EvaluateTaskDifficulty("difficult", "1"));
        Assert.Equal((false, 0.0), BenchmarkVerifier.EvaluateTaskDifficulty("standard", "0"));
        Assert.Equal((false, 0.0), BenchmarkVerifier.EvaluateTaskDifficulty("expert", "99"));
    }

    [Fact]
    public void EvaluateExpectedFindings_DoesNotDoubleCountOverlappingSubstringKeywords()
    {
        var serilogFinding = new[]
        {
            new BenchmarkExpectedFinding(
                "src/Serilog/Log.cs",
                "CloseAndFlushAsync",
                "high",
                "Swallowed bare catch block hides disposal exceptions and PR omits regression tests",
                ["catch", "Dispose", "test", "tests"])
        };

        // Single word "tests" matches both "test" and "tests" as substrings; must NOT double-count as 2 independent keywords.
        var (singleWordScore, singleWordMissing) = BenchmarkVerifier.EvaluateExpectedFindings(
            serilogFinding,
            "CloseAndFlushAsync needs tests.");
        Assert.Equal(0.0, singleWordScore);
        Assert.Single(singleWordMissing);

        // Two independent keywords ("catch" and "tests" -> canonical "test") alongside matching symbol satisfy corroboration.
        var (corroboratedScore, corroboratedMissing) = BenchmarkVerifier.EvaluateExpectedFindings(
            serilogFinding,
            "CloseAndFlushAsync swallows exceptions in a catch block and needs tests.");
        Assert.Equal(1.0, corroboratedScore);
        Assert.Empty(corroboratedMissing);
    }

    [Fact]
    public async Task BenchmarkLlmJudge_EvaluateCodeReviewAsync_IncludesReviewerRejectionReasonInPrompt()
    {
        var factory = new Mock<IBenchmarkJudgeClientFactory>();
        var chatClient = new Mock<IChatClient>();
        string? capturedUserPrompt = null;

        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) =>
            {
                capturedUserPrompt = msgs.Last().Text;
            })
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"score": 0.95, "rationale": "Solid rejection."}""")));
        factory.Setup(f => f.CreateJudgeClient(It.IsAny<BenchmarkJudgeConfig>())).Returns(chatClient.Object);

        var judge = new BenchmarkLlmJudge(factory.Object, NullLogger<BenchmarkLlmJudge>.Instance);
        var instance = new BenchmarkInstance
        {
            InstanceId = "ncf-cr-csharp-008",
            Category = BenchmarkProblemCategory.CodeReview,
            Repo = "App-vNext/Polly",
            BaseCommit = "abc",
            ProblemStatement = "Review PR",
            CandidatePatch = "diff"
        };
        var outcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            ReviewerFeedback = "DecorrelatedJitterBackoffV2 fails on infinity.",
            ReviewerRejectionReason = "Checks double.IsNaN instead of double.IsInfinity causing OverflowException.",
            TargetRouting = StandardStageIds.Implementation
        };

        var result = await judge.EvaluateCodeReviewAsync(instance, outcome, new BenchmarkJudgeConfig { ModelName = "judge" });

        Assert.Equal(0.95, result.Score, 4);
        Assert.NotNull(capturedUserPrompt);
        Assert.Contains("DecorrelatedJitterBackoffV2 fails on infinity.", capturedUserPrompt, StringComparison.Ordinal);
        Assert.Contains("Checks double.IsNaN instead of double.IsInfinity causing OverflowException.", capturedUserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step1162_EvaluateExpectedFindings_AndTaskReview_PreservePreAskCorpusOptionsAndIdentifierBoundedFileStem()
    {
        // 1. ncf-cr-csharp-008: Qwen review names RetryHelper + 4/5 independent keywords without literal RetryHelper.cs or DecorrelatedJitterBackoffV2
        var pollyFindings = new[]
        {
            new BenchmarkExpectedFinding(
                "src/Polly.Core/Retry/RetryHelper.cs",
                "DecorrelatedJitterBackoffV2",
                "high",
                "Checks double.IsNaN instead of double.IsInfinity so +Infinity overflows to long.MinValue where Math.Abs throws OverflowException, and PR omits tests",
                ["IsInfinity", "IsNaN", "Math.Abs", "OverflowException", "test"])
        };

        var qwenPollyCorpus = "In RetryHelper, checking double.IsNaN still overflows to long.MinValue where Math.Abs throws OverflowException; missing unit test coverage in RetryHelperTests.cs.";
        var (pollyScore, pollyMissing) = BenchmarkVerifier.EvaluateExpectedFindings(pollyFindings, qwenPollyCorpus);
        Assert.Equal(1.0, pollyScore);
        Assert.Empty(pollyMissing);

        // Reject short or generic stems and substring-only collisions
        var shortStemFindings = new[]
        {
            new BenchmarkExpectedFinding("src/Serilog/Log.cs", "CloseAndFlushAsync", "high", "Swallowed exception", ["catch", "Dispose", "test"]),
            new BenchmarkExpectedFinding("src/http.py", "send_request", "high", "Timeout handling", ["timeout", "retry", "socket"]),
            new BenchmarkExpectedFinding("src/index.ts", "createRouter", "high", "Route guard", ["route", "guard", "fallback"])
        };
        var substringCollisionCorpus = "The catalog service handles https requests at index 0 with catch, Dispose, test, timeout, retry, socket, route, guard, fallback.";
        var (collisionScore, collisionMissing) = BenchmarkVerifier.EvaluateExpectedFindings(shortStemFindings, substringCollisionCorpus);
        Assert.Equal(0.0, collisionScore);
        Assert.Equal(3, collisionMissing.Count);

        // RetryHelperTests.cs alone must NOT satisfy identifier-bounded stem RetryHelper
        var testFileOnlyCorpus = "RetryHelperTests.cs is missing IsNaN, Math.Abs, OverflowException, test coverage.";
        var (testFileScore, _) = BenchmarkVerifier.EvaluateExpectedFindings(pollyFindings, testFileOnlyCorpus);
        Assert.Equal(0.0, testFileScore);

        // 2. ncf-tr-rust-002: pre-ask visible assistant analysis (Summary) + Question.OptionsJson evaluated by VerifyTaskReviewAsync & BenchmarkLlmJudge
        var trInstance = new BenchmarkInstance
        {
            InstanceId = "ncf-tr-rust-002",
            Category = BenchmarkProblemCategory.TaskReview,
            Language = BenchmarkLanguages.Rust,
            Repo = "tokio-rs/tokio",
            BaseCommit = "ab53bf",
            ProblemStatement = "Clarify DelayQueue::remove wakeup semantics",
            TaskSpecification = "---\nid: 402\nstatus: Open\ndifficulty: standard\n---\n\nSpecify remove wakeup behavior.",
            ExpectedVerdict = "NeedsClarification",
            ExpectedFindings =
            [
                new BenchmarkExpectedFinding("tokio-util/src/time/delay_queue.rs", Keywords: ["DelayQueue", "waker", "slab", "deadline"])
            ]
        };

        var trOutcome = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.WaitingForHuman,
            Summary = "Inspected tokio-util/src/time/delay_queue.rs:579 and saw DelayQueue::remove mutates slab entries without waking stored wakers.",
            PendingQuestions =
            [
                new Question
                {
                    Prompt = "When should stored wakers be notified?",
                    Rationale = "Unclear if notification applies only when empty or when earliest deadline shifts.",
                    OptionsJson = """[{"label":"Empty only","description":"Wake only on empty transition"},{"label":"Deadline shift","description":"Wake whenever earliest deadline moves"}]"""
                }
            ]
        };

        var factory = new Mock<IBenchmarkJudgeClientFactory>();
        var chatClient = new Mock<IChatClient>();
        string? capturedJudgePrompt = null;
        chatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedJudgePrompt = msgs.Last().Text)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"score": 0.91, "rationale": "Grounded question."}""")));
        factory.Setup(f => f.CreateJudgeClient(It.IsAny<BenchmarkJudgeConfig>())).Returns(chatClient.Object);

        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new BenchmarkLlmJudge(factory.Object, NullLogger<BenchmarkLlmJudge>.Instance),
            NullLogger<BenchmarkVerifier>.Instance);

        var trResult = await verifier.VerifyAsync(
            trInstance,
            new BenchmarkWorkspaceHandle { InstanceId = "ncf-tr-rust-002", Category = BenchmarkProblemCategory.TaskReview, WorkspacePath = _tempDir, BaseCommit = "ab53bf" },
            trOutcome,
            ExecutionMode.BareHost,
            verifyWithTests: false,
            judgeConfig: new BenchmarkJudgeConfig { ModelName = "judge" });

        Assert.True(trResult.Passed);
        Assert.Equal(1.0, trResult.DeterministicScore);
        Assert.NotNull(capturedJudgePrompt);
        Assert.Contains("tokio-util/src/time/delay_queue.rs:579", capturedJudgePrompt);
        Assert.Contains("Empty only", capturedJudgePrompt);
        Assert.Contains("Wake whenever earliest deadline moves", capturedJudgePrompt);

        // OptionsJson fallback: string array & non-JSON raw text
        Assert.Equal("Option A, Option B", BenchmarkVerifier.ExtractQuestionOptionsText("""["Option A", "Option B"]"""));
        Assert.Equal("plain raw fallback", BenchmarkVerifier.ExtractQuestionOptionsText("plain raw fallback"));
    }

    [Fact]
    public void Step1162_ResolveAgentTestCommand_StripsVerifierFlagsAndEnvVars_WhileKeepingResolveTestCommandIntact()
    {
        var csSubDir = Path.Combine(_tempDir, "cs-strip");
        Directory.CreateDirectory(Path.Combine(csSubDir, "src"));
        File.WriteAllText(Path.Combine(csSubDir, "src", "App.sln"), "<Project />");

        var csInstance = new BenchmarkInstance
        {
            InstanceId = "cs-strip",
            Language = BenchmarkLanguages.CSharp,
            Repo = "owner/app",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "dotnet test --nologo --logger trx --results-directory /workspace/.ncf-bench-results"
        };
        Assert.Equal("dotnet test src/App.sln --nologo --no-build", BenchmarkTestCommandResolver.ResolveAgentTestCommand(csInstance, csSubDir));
        Assert.Contains("--logger trx", BenchmarkTestCommandResolver.ResolveTestCommand(csInstance, csSubDir));
        Assert.Contains("--results-directory /workspace/.ncf-bench-results", BenchmarkTestCommandResolver.ResolveTestCommand(csInstance, csSubDir));

        var csColonInstance = csInstance with
        {
            CustomTestCommand = "dotnet test -r linux-x64 --logger:trx --results-directory:/workspace/.ncf-bench-results -l console"
        };
        Assert.Equal("dotnet test src/App.sln -r linux-x64 --no-build", BenchmarkTestCommandResolver.ResolveAgentTestCommand(csColonInstance, csSubDir));

        var jestInstance = new BenchmarkInstance
        {
            InstanceId = "jest-strip",
            Language = BenchmarkLanguages.TypeScript,
            Repo = "facebook/docusaurus",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "JEST_JUNIT_OUTPUT_DIR=/workspace/.ncf-bench-results JEST_JUNIT_OUTPUT_NAME=junit.xml npx jest --ci --reporters=default --reporters=jest-junit"
        };
        Assert.Equal("npx jest --ci --reporters=default", BenchmarkTestCommandResolver.ResolveAgentTestCommand(jestInstance, _tempDir));
        Assert.Contains("--reporters=jest-junit", BenchmarkTestCommandResolver.ResolveTestCommand(jestInstance, _tempDir));

        var jestTrailingDashInstance = jestInstance with
        {
            CustomTestCommand = "JEST_JUNIT_OUTPUT_DIR=/tmp npm test -- --reporters=jest-junit"
        };
        Assert.Equal("npm test", BenchmarkTestCommandResolver.ResolveAgentTestCommand(jestTrailingDashInstance, _tempDir));

        var jestRemainingFlagInstance = jestInstance with
        {
            CustomTestCommand = "JEST_JUNIT_OUTPUT_DIR=/tmp npm test -- --watchAll=false --reporters=jest-junit"
        };
        Assert.Equal("npm test -- --watchAll=false", BenchmarkTestCommandResolver.ResolveAgentTestCommand(jestRemainingFlagInstance, _tempDir));

        var envOnlyTrailingDashInstance = jestInstance with
        {
            CustomTestCommand = "MOCHA_FILE=/tmp/junit.xml npm run test:mocha --"
        };
        Assert.Equal("npm run test:mocha --", BenchmarkTestCommandResolver.ResolveAgentTestCommand(envOnlyTrailingDashInstance, _tempDir));

        var mochaTrailingDashInstance = new BenchmarkInstance
        {
            InstanceId = "mocha-strip-1",
            Language = BenchmarkLanguages.JavaScript,
            Repo = "axios/axios",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml npm run test:mocha -- --reporter mocha-junit-reporter"
        };
        Assert.Equal("npm run test:mocha", BenchmarkTestCommandResolver.ResolveAgentTestCommand(mochaTrailingDashInstance, _tempDir));
        Assert.Contains("mocha-junit-reporter", BenchmarkTestCommandResolver.ResolveTestCommand(mochaTrailingDashInstance, _tempDir));

        var mochaRemainingFlagInstance = mochaTrailingDashInstance with
        {
            CustomTestCommand = "MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml npm run test:mocha -- --grep foo -R mocha-junit-reporter"
        };
        Assert.Equal("npm run test:mocha -- --grep foo", BenchmarkTestCommandResolver.ResolveAgentTestCommand(mochaRemainingFlagInstance, _tempDir));

        var vitestDir = Path.Combine(_tempDir, "vitest-strip");
        Directory.CreateDirectory(vitestDir);
        File.WriteAllText(Path.Combine(vitestDir, "vitest.unit.config.ts"), "export default {}");
        var vitestInstance = new BenchmarkInstance
        {
            InstanceId = "vitest-strip",
            Language = BenchmarkLanguages.TypeScript,
            Repo = "vuejs/core",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "npx vitest run -c vitest.unit.config.ts --reporter=junit --outputFile=/workspace/.ncf-bench-results/junit.xml"
        };
        Assert.Equal("npx vitest run -c vitest.unit.config.ts", BenchmarkTestCommandResolver.ResolveAgentTestCommand(vitestInstance, vitestDir));
        Assert.Contains("--reporter=junit", BenchmarkTestCommandResolver.ResolveTestCommand(vitestInstance, vitestDir));

        var vitestTrailingDashAndEmptyInstance = vitestInstance with
        {
            CustomTestCommand = "npm run test:unit -- --reporter=junit --outputFile="
        };
        Assert.Equal("npm run test:unit", BenchmarkTestCommandResolver.ResolveAgentTestCommand(vitestTrailingDashAndEmptyInstance, _tempDir));

        var pytestInstance = new BenchmarkInstance
        {
            InstanceId = "pytest-strip",
            Language = BenchmarkLanguages.Python,
            Repo = "pallets/flask",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "pytest -q --junitxml=/workspace/.ncf-bench-results/junit.xml --junit-xml /tmp/other.xml --junitxml="
        };
        Assert.Equal("pytest -q", BenchmarkTestCommandResolver.ResolveAgentTestCommand(pytestInstance, _tempDir));
        Assert.Contains("--junitxml=/workspace/.ncf-bench-results/junit.xml", BenchmarkTestCommandResolver.ResolveTestCommand(pytestInstance, _tempDir));
    }

    [Fact]
    public void Step1162_WrapWithContainerDependencySetup_Node_IsolatesPeerDepsWiresWorkspaceNodePathAndPrunesStaleRunners()
    {
        var wrappedMocha = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "MOCHA_FILE=/workspace/.ncf-bench-results/junit.xml npm run test:mocha -- --reporter mocha-junit-reporter",
            BenchmarkLanguages.JavaScript,
            ExecutionMode.ContainerSandbox);

        Assert.Contains("if [ -d /tmp/ncf-mocha-junit/node_modules/mocha ]; then rm -rf /tmp/ncf-mocha-junit; fi;", wrappedMocha);
        Assert.Contains("npm install --prefix /tmp/ncf-mocha-junit --legacy-peer-deps --omit=peer --ignore-scripts --no-audit --no-fund --no-save mocha-junit-reporter", wrappedMocha);
        Assert.Contains("rm -rf /tmp/ncf-mocha-junit/node_modules/mocha", wrappedMocha);
        Assert.Contains("export NODE_PATH=\"$PWD/node_modules:/tmp/ncf-mocha-junit/node_modules${NODE_PATH:+:$NODE_PATH}\"", wrappedMocha);

        var wrappedJest = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "JEST_JUNIT_OUTPUT_DIR=/workspace/.ncf-bench-results npx jest --ci --reporters=jest-junit",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);

        Assert.Contains("if [ -d /tmp/ncf-jest-junit/node_modules/jest ]; then rm -rf /tmp/ncf-jest-junit; fi;", wrappedJest);
        Assert.Contains("npm install --prefix /tmp/ncf-jest-junit --legacy-peer-deps --omit=peer --ignore-scripts --no-audit --no-fund --no-save jest-junit", wrappedJest);
        Assert.Contains("rm -rf /tmp/ncf-jest-junit/node_modules/jest", wrappedJest);
        Assert.Contains("export NODE_PATH=\"$PWD/node_modules:/tmp/ncf-jest-junit/node_modules${NODE_PATH:+:$NODE_PATH}\"", wrappedJest);
    }

    [Fact]
    public void Step1163_WrapWithContainerDependencySetup_Python_ExportsCaBundleAndAppliesPipConstraints()
    {
        var wrappedDefault = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "pytest --junitxml=/workspace/.ncf-bench-results/junit.xml",
            BenchmarkLanguages.Python,
            ExecutionMode.ContainerSandbox);

        Assert.Contains("REQUESTS_CA_BUNDLE", wrappedDefault);
        Assert.Contains("/etc/ssl/certs/ca-certificates.crt", wrappedDefault);
        Assert.Contains("SETUPTOOLS_SCM_PRETEND_VERSION", wrappedDefault);
        Assert.DoesNotContain("ncf-pip-constraints.txt\"", wrappedDefault);
        Assert.Contains("if [ -f /tmp/ncf-bench-venv/.ncf-pip-constraints ]; then rm -rf /tmp/ncf-bench-venv /tmp/ncf-pip-constraints.txt; fi", wrappedDefault);

        var wrappedNode = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "npx vitest run -c vitest.unit.config.ts --reporter=junit --outputFile=/workspace/.ncf-bench-results/junit.xml",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int npmCacheGuardIdx = wrappedNode.IndexOf("export npm_config_cache=", StringComparison.Ordinal);
        int pnpmStoreGuardIdx = wrappedNode.IndexOf("export npm_config_store_dir=\"/tmp/ncf-pnpm-store\"", StringComparison.Ordinal);
        int depsReadyCheckIdx = wrappedNode.IndexOf("ncf_node_deps_ready=0", StringComparison.Ordinal);
        Assert.True(npmCacheGuardIdx >= 0 && pnpmStoreGuardIdx > npmCacheGuardIdx && depsReadyCheckIdx > pnpmStoreGuardIdx,
            $"Expected writable npm_config_cache and npm_config_store_dir guards before ncf_node_deps_ready check in: {wrappedNode}");

        var wrappedYarnCmd = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "yarn build:packages && npx jest --ci --reporters=jest-junit -- foo.test.ts",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int installEndIdx = wrappedYarnCmd.IndexOf($"touch node_modules/.ncf-bench-deps-ok", StringComparison.Ordinal);
        int yarnGuardAfterInstallIdx = wrappedYarnCmd.IndexOf("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund yarn", installEndIdx, StringComparison.Ordinal);
        Assert.True(installEndIdx > 0 && yarnGuardAfterInstallIdx > installEndIdx,
            $"Expected standalone yarn guard after cached-node_modules check in: {wrappedYarnCmd}");

        var wrappedPnpmCmd = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "cd sub && pnpm test",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int pnpmInstallEndIdx = wrappedPnpmCmd.IndexOf("touch node_modules/.ncf-bench-deps-ok", StringComparison.Ordinal);
        int pnpmGuardAfterInstallIdx = wrappedPnpmCmd.IndexOf("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund pnpm", pnpmInstallEndIdx, StringComparison.Ordinal);
        Assert.True(pnpmInstallEndIdx > 0 && pnpmGuardAfterInstallIdx > pnpmInstallEndIdx,
            $"Expected standalone pnpm guard after cached-node_modules check in: {wrappedPnpmCmd}");

        var wrappedBareYarn = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "yarn",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int bareYarnInstallEndIdx = wrappedBareYarn.IndexOf("touch node_modules/.ncf-bench-deps-ok", StringComparison.Ordinal);
        Assert.True(bareYarnInstallEndIdx > 0 &&
                    wrappedBareYarn.IndexOf("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund yarn", bareYarnInstallEndIdx, StringComparison.Ordinal) > bareYarnInstallEndIdx);

        var wrappedBarePnpm = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "pnpm",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int barePnpmInstallEndIdx = wrappedBarePnpm.IndexOf("touch node_modules/.ncf-bench-deps-ok", StringComparison.Ordinal);
        Assert.True(barePnpmInstallEndIdx > 0 &&
                    wrappedBarePnpm.IndexOf("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund pnpm", barePnpmInstallEndIdx, StringComparison.Ordinal) > barePnpmInstallEndIdx);

        var wrappedYarnPathArg = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "npx jest packages/yarn/foo.test.ts",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int pathArgInstallEndIdx = wrappedYarnPathArg.IndexOf("touch node_modules/.ncf-bench-deps-ok", StringComparison.Ordinal);
        Assert.Equal(-1, wrappedYarnPathArg.IndexOf("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund yarn", pathArgInstallEndIdx, StringComparison.Ordinal));

        var wrappedPnpmPathArg = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "npx vitest run tests/pnpm-helper.spec.ts",
            BenchmarkLanguages.TypeScript,
            ExecutionMode.ContainerSandbox);
        int pnpmPathArgInstallEndIdx = wrappedPnpmPathArg.IndexOf("touch node_modules/.ncf-bench-deps-ok", StringComparison.Ordinal);
        Assert.Equal(-1, wrappedPnpmPathArg.IndexOf("npm install -g --prefix /tmp/ncf-npm-global --ignore-scripts --no-audit --no-fund pnpm", pnpmPathArgInstallEndIdx, StringComparison.Ordinal));

        var wrappedPinned = BenchmarkTestCommandResolver.WrapWithContainerDependencySetup(
            "pytest --junitxml=/workspace/.ncf-bench-results/junit.xml",
            BenchmarkLanguages.Python,
            ExecutionMode.ContainerSandbox,
            ["Werkzeug<3.0", "click>=8.0"]);

        Assert.Contains("REQUESTS_CA_BUNDLE", wrappedPinned);
        Assert.Contains("/tmp/ncf-pip-constraints.txt", wrappedPinned);
        Assert.Contains("export PIP_CONSTRAINT=/tmp/ncf-pip-constraints.txt", wrappedPinned);
        Assert.Contains("-c /tmp/ncf-pip-constraints.txt", wrappedPinned);
        Assert.Contains("Werkzeug<3.0", wrappedPinned);
        Assert.Contains("click>=8.0", wrappedPinned);
        Assert.Contains(".ncf-pip-constraints", wrappedPinned);
    }

    [Fact]
    public void Step1163_EvaluateExpectedFindings_MatchesSnakeCaseFileStemAgainstPascalCaseTypeWithoutSymbolHint()
    {
        var delayQueueFindings = new[]
        {
            new BenchmarkExpectedFinding(
                "tokio-util/src/time/delay_queue.rs",
                Keywords: ["DelayQueue", "waker", "slab", "deadline"])
        };

        var flintAskCorpus = "Clarify DelayQueue::remove wakeup behavior: should remove wake the stored waker whenever an entry changes the earliest deadline in the slab?";
        var (score, missing) = BenchmarkVerifier.EvaluateExpectedFindings(delayQueueFindings, flintAskCorpus);
        Assert.Equal(1.0, score);
        Assert.Empty(missing);

        var testFileOnlyCorpus = "Check DelayQueueTests for waker and deadline in slab.";
        var (testScore, testMissing) = BenchmarkVerifier.EvaluateExpectedFindings(delayQueueFindings, testFileOnlyCorpus);
        Assert.Equal(0.0, testScore);
        Assert.Single(testMissing);
    }

    [Fact]
    public void ResolveTestCommand_FailToPassOnly_FiltersCSharpPythonRustAndVitest_WhileKeepingAgentUnfiltered()
    {
        var csDir = Path.Combine(_tempDir, "cs-filter");
        Directory.CreateDirectory(csDir);

        var csCustomInstance = new BenchmarkInstance
        {
            InstanceId = "serilog-test",
            Language = BenchmarkLanguages.CSharp,
            Repo = "serilog/serilog",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "dotnet test test/Serilog.Tests/Serilog.Tests.csproj --nologo --logger trx --results-directory /workspace/.ncf-bench-results",
            FailToPass =
            [
                "Serilog.Tests.Unit.Cli.CommandAppTests+Help.Should_Output_Root_Correctly_QuestionMark",
                "Serilog.Tests.TheoryTests.Works(arg: 1)"
            ]
        };

        var csFiltered = BenchmarkTestCommandResolver.ResolveTestCommand(csCustomInstance, csDir, ExecutionMode.ContainerSandbox, failToPassOnly: true);
        Assert.Contains("--filter 'FullyQualifiedName~Serilog.Tests.Unit.Cli.CommandAppTests+Help.Should_Output_Root_Correctly_QuestionMark|FullyQualifiedName~Serilog.Tests.TheoryTests.Works'", csFiltered);

        var csDefaultInstance = csCustomInstance with { CustomTestCommand = null };
        var csDefaultFiltered = BenchmarkTestCommandResolver.ResolveTestCommand(csDefaultInstance, csDir, ExecutionMode.ContainerSandbox, failToPassOnly: true);
        Assert.Contains("--filter 'FullyQualifiedName~Serilog.Tests.Unit.Cli.CommandAppTests+Help.Should_Output_Root_Correctly_QuestionMark|FullyQualifiedName~Serilog.Tests.TheoryTests.Works'", csDefaultFiltered);

        var agentCmd = BenchmarkTestCommandResolver.ResolveAgentTestCommand(csCustomInstance, csDir, ExecutionMode.ContainerSandbox);
        Assert.DoesNotContain("--filter", agentCmd);

        var csAlreadyFiltered = csCustomInstance with
        {
            CustomTestCommand = "dotnet test test/Serilog.Tests/Serilog.Tests.csproj --filter FullyQualifiedName~Existing"
        };
        Assert.Equal(
            "dotnet test test/Serilog.Tests/Serilog.Tests.csproj --filter FullyQualifiedName~Existing",
            BenchmarkTestCommandResolver.ResolveTestCommand(csAlreadyFiltered, csDir, ExecutionMode.ContainerSandbox, failToPassOnly: true));

        var pyInstance = new BenchmarkInstance
        {
            InstanceId = "py-filter",
            Language = BenchmarkLanguages.Python,
            Repo = "psf/requests",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "pytest tests/test_requests.py --junitxml=/workspace/.ncf-bench-results/junit.xml",
            FailToPass =
            [
                "tests/test_requests.py::TestRequests::test_invalid_url[InvalidURL-http://.example.com]",
                "tests/test_requests.py::test_other"
            ]
        };
        var pyFiltered = BenchmarkTestCommandResolver.ResolveTestCommand(pyInstance, csDir, ExecutionMode.ContainerSandbox, failToPassOnly: true);
        Assert.Equal(
            "pytest tests/test_requests.py --junitxml=/workspace/.ncf-bench-results/junit.xml -k 'test_invalid_url or test_other'",
            pyFiltered);

        var rustInstance = new BenchmarkInstance
        {
            InstanceId = "rust-filter",
            Language = BenchmarkLanguages.Rust,
            Repo = "tokio-rs/tokio",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "cargo test -p tokio-util --test time_delay_queue --no-fail-fast",
            FailToPass = ["wake_after_remove_last"]
        };
        var rustFiltered = BenchmarkTestCommandResolver.ResolveTestCommand(rustInstance, csDir, ExecutionMode.ContainerSandbox, failToPassOnly: true);
        Assert.Equal(
            "cargo test -p tokio-util --test time_delay_queue --no-fail-fast -- wake_after_remove_last",
            rustFiltered);

        var vitestDir = Path.Combine(_tempDir, "vitest-filter");
        var specRel = Path.Combine("packages", "compiler-core", "__tests__", "parse.spec.ts");
        Directory.CreateDirectory(Path.Combine(vitestDir, "packages", "compiler-core", "__tests__"));
        File.WriteAllText(Path.Combine(vitestDir, specRel), "// spec");
        var vitestInstance = new BenchmarkInstance
        {
            InstanceId = "ts-filter",
            Language = BenchmarkLanguages.TypeScript,
            Repo = "vuejs/core",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            CustomTestCommand = "npx vitest run -c vitest.unit.config.ts --reporter=junit --outputFile=/workspace/.ncf-bench-results/junit.xml",
            FailToPass = ["packages/compiler-core/__tests__/parse.spec.ts > compiler: parse > Element > v-pre"]
        };
        var vitestFiltered = BenchmarkTestCommandResolver.ResolveTestCommand(vitestInstance, vitestDir, ExecutionMode.ContainerSandbox, failToPassOnly: true);
        Assert.EndsWith("packages/compiler-core/__tests__/parse.spec.ts", vitestFiltered, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateVerificationScope_WithWslUncWorkspace_EnablesWslContainersAndLinuxMountPath()
    {
        const string uncWorkspace = @"\\wsl.localhost\Ubuntu\home\gruearbre\.nightcodeforge\benchmarks\workspaces\serilog-123";
        var scope = BenchmarkVerifier.CreateVerificationScope(uncWorkspace, customContainerImage: null, ExecutionMode.ContainerSandbox, "ncf-bench-verify-test");

        Assert.True(scope.UseWslForContainers);
        Assert.Equal("Ubuntu", scope.WslDistribution);
        Assert.Equal("/home/gruearbre/.nightcodeforge/benchmarks/workspaces/serilog-123", scope.Ext4MirrorPath);
        Assert.Equal("/home/gruearbre/.nightcodeforge/benchmarks/workspaces/serilog-123", scope.MountTable.Entries.First(e => e.VirtualMountPath == "/workspace").WslTargetDirectoryPath);
    }

    [Fact]
    public async Task VerifyAsync_Implementation_ZeroParsedResultsWithNonZeroExit_IncludesSanitizedOutputTail()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-zero-parsed");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(
                ExitCode: 139,
                StandardOutput: "line 1\nline 2\ncollecting ... 90 items",
                StandardError: "bash: line 1: 49 Segmentation fault (core dumped)\n",
                Duration: TimeSpan.FromSeconds(2)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "py-crash",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.Python,
            Repo = "pytest-dev/pytest",
            BaseCommit = "abc",
            ProblemStatement = "Fix crash",
            TestResultFormat = TestResultFormat.JUnitXml,
            FailToPass = ["testing/test_mark.py::test_mark_mro"]
        };

        var result = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "py-crash", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(result.Passed);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("exited with code 139", result.ErrorMessage);
        Assert.Contains("Output tail:", result.ErrorMessage);
        Assert.Contains("Segmentation fault", result.ErrorMessage);
        Assert.DoesNotContain("\n", result.ErrorMessage);
        Assert.DoesNotContain("\r", result.ErrorMessage);
    }

    [Fact]
    public void Step1164_ApplyMsBuildSingleNodeFlag_And_EnsureDotNetTestNoBuildFlag_HandleChainedAndSeparatedCommands()
    {
        Assert.Equal(
            "dotnet build --nologo -m:1 && dotnet test --no-build --logger trx -m:1",
            BenchmarkTestCommandResolver.ApplyMsBuildSingleNodeFlag("dotnet build --nologo && dotnet test --no-build --logger trx"));

        Assert.Equal(
            "dotnet test App.sln --no-build -m:1 -- --filter Foo",
            BenchmarkTestCommandResolver.ApplyMsBuildSingleNodeFlag("dotnet test App.sln --no-build -- --filter Foo"));

        Assert.Equal(
            "dotnet build -m:1 && dotnet test --no-build -m:1",
            BenchmarkTestCommandResolver.ApplyMsBuildSingleNodeFlag("dotnet build -m:1 && dotnet test --no-build"));

        Assert.Equal(
            "dotnet test App.sln --no-build -- --filter Foo",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("dotnet test App.sln -- --filter Foo"));

        Assert.Equal(
            "dotnet test App.sln --no-build",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("dotnet test App.sln --no-build"));

        Assert.Equal(
            "pytest -q",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("pytest -q"));

        Assert.Equal(
            "( dotnet build -m:1 && dotnet test -m:1 )",
            BenchmarkTestCommandResolver.ApplyMsBuildSingleNodeFlag("(dotnet build && dotnet test)"));

        Assert.Equal(
            "( ncf_rc=0 ; { dotnet build A.csproj --nologo -m:1 && dotnet test A.csproj --no-build -m:1 ; } || ncf_rc=$? ; { dotnet build B.csproj --nologo -m:1 && dotnet test B.csproj --no-build -m:1 ; } || ncf_rc=$? ; exit $ncf_rc )",
            BenchmarkTestCommandResolver.ApplyMsBuildSingleNodeFlag(
                "( ncf_rc=0; { dotnet build A.csproj --nologo && dotnet test A.csproj --no-build; } || ncf_rc=$?; { dotnet build B.csproj --nologo && dotnet test B.csproj --no-build; } || ncf_rc=$?; exit $ncf_rc )"));

        Assert.Equal(
            "dotnet test App.sln --no-build 2>&1",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("dotnet test App.sln 2>&1"));

        Assert.Equal(
            "dotnet test App.sln --no-build > out.log",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("dotnet test App.sln > out.log"));

        Assert.Equal(
            "FOO=$(echo bar) dotnet test App.sln --no-build",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("FOO=$(echo bar) dotnet test App.sln"));

        Assert.Equal(
            "( dotnet test --filter \"A(1)\" --no-build )",
            BenchmarkTestCommandResolver.EnsureDotNetTestNoBuildFlag("(dotnet test --filter \"A(1)\")"));
    }

    [Fact]
    public async Task Step1164_VerifyAsync_RetriesMsBuildChildNodeCrashWithSingleNode_AndRecoversOnRetry()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-msb4166-recover");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        var capturedCommands = new List<string>();
        var capturedEnvs = new List<IReadOnlyDictionary<string, string>?>();
        int attempt = 0;

        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, _, _, env, _) =>
            {
                attempt++;
                capturedCommands.Add(cmd);
                capturedEnvs.Add(env);
                var resultsDir = Path.Combine(workspaceDir, BenchmarkPaths.BenchmarkResultDirRelative);
                if (attempt == 1)
                {
                    File.WriteAllText(Path.Combine(resultsDir, "stale-attempt1.trx"), "<TestRun><broken");
                }
                else
                {
                    Assert.False(File.Exists(Path.Combine(resultsDir, "stale-attempt1.trx")));
                    File.WriteAllText(Path.Combine(resultsDir, "ok.trx"), """
                        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                          <Results><UnitTestResult testId="1" testName="BreakDurationTest" outcome="Passed" /></Results>
                        </TestRun>
                        """);
                }
            })
            .ReturnsAsync(() => attempt == 1
                ? new CommandResult(1, "MSBUILD : error MSB4166: Child node \"2\" exited prematurely. Shutting down.", string.Empty, TimeSpan.FromSeconds(1))
                : new CommandResult(0, "Passed!", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "polly-1852",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "App-vNext/Polly",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            FailToPass = ["BreakDurationTest"]
        };

        var result = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "polly-1852", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.True(result.Passed);
        Assert.False(result.IsInfrastructureError);
        Assert.Equal(2, capturedCommands.Count);
        Assert.DoesNotContain("-m:1", capturedCommands[0]);
        Assert.Contains("-m:1", capturedCommands[1]);
        Assert.Equal("1", capturedEnvs[1]![BenchmarkDefaults.DotNetCliDoNotUseMsBuildServerEnvVar]);
        Assert.Equal("1", capturedEnvs[1]![BenchmarkDefaults.MsBuildDisableNodeReuseEnvVar]);
        Assert.Equal(BenchmarkDefaults.DotNetRollForwardLatestMajor, capturedEnvs[1]![BenchmarkDefaults.DotNetRollForwardEnvVar]);
        Assert.Contains("Attempt 1 - MSBuild node crash detected", result.VerificationLog);
        Assert.Contains("Attempt 2 - Single-node retry", result.VerificationLog);
    }

    [Fact]
    public async Task Step1164_VerifyAsync_MarksPersistentMsBuildCrashAsInfrastructureError_WhileCompilerErrorsDoNotRetry()
    {
        var workspaceDir = Path.Combine(_tempDir, "wks-msb5021-persist");
        Directory.CreateDirectory(workspaceDir);

        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var execEnv = new Mock<IExecutionEnvironment>();
        var judge = new Mock<IBenchmarkLlmJudge>();

        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, "MSBUILD : error MSB5021: Termination of node failed.", string.Empty, TimeSpan.FromSeconds(1)));

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            containerFactory.Object,
            envProvider.Object,
            judge.Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "polly-1852",
            Category = BenchmarkProblemCategory.Implementation,
            Language = BenchmarkLanguages.CSharp,
            Repo = "App-vNext/Polly",
            BaseCommit = "abc",
            ProblemStatement = "Fix",
            FailToPass = ["BreakDurationTest"]
        };

        var persistentCrash = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "polly-1852", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(persistentCrash.Passed);
        Assert.True(persistentCrash.IsInfrastructureError);
        Assert.NotNull(persistentCrash.VerificationLog);
        Assert.Contains("MSB5021", persistentCrash.VerificationLog);
        Assert.Contains("infrastructure error", persistentCrash.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        execEnv.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        // CodeReview MinorFix propagates IsInfrastructureError, forces zero DeterministicScore, and skips LLM judge
        var crInstance = instance with
        {
            Category = BenchmarkProblemCategory.CodeReview,
            CandidatePatch = "diff",
            ExpectedVerdict = "MinorFix"
        };
        var judgeConfig = new BenchmarkJudgeConfig { ModelName = "judge-model" };
        var crCrash = await verifier.VerifyAsync(
            crInstance,
            new BenchmarkWorkspaceHandle { InstanceId = "polly-1852", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: judgeConfig);
        Assert.False(crCrash.Passed);
        Assert.True(crCrash.IsInfrastructureError);
        Assert.Equal(0.0, crCrash.DeterministicScore);
        Assert.Null(crCrash.JudgeScore);
        Assert.Contains("MSB5021", crCrash.VerificationLog);
        judge.Verify(
            j => j.EvaluateCodeReviewAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkAgentOutcome>(), It.IsAny<BenchmarkJudgeConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Compiler error must NOT retry and must NOT be marked IsInfrastructureError
        execEnv.Invocations.Clear();
        execEnv.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, "Program.cs(1,1): error CS1002: ; expected", string.Empty, TimeSpan.FromSeconds(1)));

        var compileFail = await verifier.VerifyAsync(
            instance,
            new BenchmarkWorkspaceHandle { InstanceId = "polly-1852", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = workspaceDir, BaseCommit = "abc" },
            new BenchmarkAgentOutcome { RunStatus = AgentRunStatus.Completed, ModelPatch = "diff" },
            ExecutionMode.BareHost,
            verifyWithTests: true,
            judgeConfig: null);

        Assert.False(compileFail.Passed);
        Assert.False(compileFail.IsInfrastructureError);
        execEnv.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 1, 79, 79, 1, 0.0)]
    [InlineData(1, 1, 0, 1, 1, 0.0)]
    [InlineData(1, 2, 3, 4, 1, 0.375)]
    [InlineData(1, 1, 1, 1, 0, 1.0)]
    [InlineData(1, 2, 0, 0, 1, 0.5)]
    [InlineData(0, 0, 3, 4, 1, 0.75)]
    [InlineData(0, 0, 0, 0, 0, 1.0)]
    [InlineData(0, 0, 0, 0, 1, 0.0)]
    public void ComputeImplementationDeterministicScore_AppliesFailToPassGatingAcrossAllAssertionCases(
        int f2pPassed,
        int f2pTotal,
        int p2pPassed,
        int p2pTotal,
        int exitCode,
        double expectedScore)
    {
        var score = BenchmarkVerifier.ComputeImplementationDeterministicScore(
            f2pPassed,
            f2pTotal,
            p2pPassed,
            p2pTotal,
            exitCode);

        Assert.Equal(expectedScore, score, 4);
    }

    [Theory]
    [InlineData(true, 1.0, 0.5)]
    [InlineData(false, 1.0, 0.5)]
    [InlineData(true, 0.0, 0.0)]
    [InlineData(false, 0.0, 0.0)]
    public async Task VerifyAsync_TaskReview_WrongVerdictIsSymmetricAcrossBothDirections(
        bool expectedIsReady,
        double expectedAnchorScore,
        double expectedDeterministicScore)
    {
        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var spec = "---\nid: 42\nstatus: Open\ndifficulty: standard\n---\n\nInitial spec.";
        var instance = new BenchmarkInstance
        {
            InstanceId = "tr-sym",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = "owner/repo",
            BaseCommit = "abc",
            ProblemStatement = "Clarify or plan",
            TaskSpecification = spec,
            ExpectedVerdict = expectedIsReady ? "Ready" : "NeedsClarification",
            ExpectedDifficulty = expectedIsReady ? "standard" : null,
            ExpectedFindings = [new BenchmarkExpectedFinding("src/Target.cs", Keywords: ["Alpha", "Beta"])]
        };

        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "tr-sym",
            Category = BenchmarkProblemCategory.TaskReview,
            WorkspacePath = _tempDir,
            BaseCommit = "abc"
        };

        var anchorText = expectedAnchorScore > 0
            ? "Updated src/Target.cs with Alpha and Beta architectural details and regression unit tests."
            : "Unrelated text with no matching terms at all for testing zero anchor coverage cleanly.";

        BenchmarkAgentOutcome wrongOutcome = expectedIsReady
            ? new BenchmarkAgentOutcome
            {
                RunStatus = AgentRunStatus.WaitingForHuman,
                PendingQuestions = [new Question { Id = 1, Prompt = "Q", Rationale = anchorText, OptionsJson = "[]" }],
                ModifiedFilesOutsideDocsTasks = Array.Empty<string>()
            }
            : new BenchmarkAgentOutcome
            {
                RunStatus = AgentRunStatus.Completed,
                TaskMarkdownBefore = spec,
                UpdatedTaskMarkdown = $"---\nid: 42\nstatus: Open\ndifficulty: standard\n---\n\nInitial spec.\n\n## Architecture & Plan\n{anchorText} Extra details so enrichment length threshold passes reliably.",
                ModifiedFilesOutsideDocsTasks = Array.Empty<string>()
            };

        var result = await verifier.VerifyAsync(instance, handle, wrongOutcome, ExecutionMode.BareHost, false, null);

        Assert.False(result.Passed);
        Assert.Equal(expectedDeterministicScore, result.DeterministicScore, 4);
        Assert.True(result.DeterministicScore <= 0.50);
    }

    [Fact]
    public async Task VerifyAsync_TaskReview_WrongVerdictWithoutExpectedFindingsScoresZeroSymmetrically()
    {
        var verifier = new BenchmarkVerifier(
            new Mock<IBenchmarkWorkspaceManager>().Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var spec = "---\nid: 43\nstatus: Open\ndifficulty: standard\n---\n\nInitial spec.";
        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "tr-sym-empty",
            Category = BenchmarkProblemCategory.TaskReview,
            WorkspacePath = _tempDir,
            BaseCommit = "abc"
        };

        var readyExpected = new BenchmarkInstance
        {
            InstanceId = "tr-sym-empty",
            Category = BenchmarkProblemCategory.TaskReview,
            Repo = "o/r",
            BaseCommit = "abc",
            ProblemStatement = "p",
            TaskSpecification = spec,
            ExpectedVerdict = "Ready"
        };
        var needsExpected = readyExpected with { ExpectedVerdict = "NeedsClarification" };

        var gotNeeds = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.WaitingForHuman,
            PendingQuestions = [new Question { Id = 1, Prompt = "Q", Rationale = "R", OptionsJson = "[]" }]
        };
        var gotReady = new BenchmarkAgentOutcome
        {
            RunStatus = AgentRunStatus.Completed,
            TaskMarkdownBefore = spec,
            UpdatedTaskMarkdown = spec + "\n\n## Plan\nLong enough enrichment text to satisfy minimum body delta and ready body length checks."
        };

        var r1 = await verifier.VerifyAsync(readyExpected, handle, gotNeeds, ExecutionMode.BareHost, false, null);
        var r2 = await verifier.VerifyAsync(needsExpected, handle, gotReady, ExecutionMode.BareHost, false, null);

        Assert.False(r1.Passed);
        Assert.False(r2.Passed);
        Assert.Equal(0.0, r1.DeterministicScore, 4);
        Assert.Equal(0.0, r2.DeterministicScore, 4);
    }

    [Fact]
    public async Task RunImplementationTestsAsync_PatchApplyFailure_SetsIsPatchApplicationErrorAndSkipsTestExecution()
    {
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ApplyPatchAsync(_tempDir, "bad candidate patch", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("git apply rejected hunk", workspacePath: _tempDir));

        var execEnv = new Mock<IExecutionEnvironment>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            envProvider.Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "cr-bad-cand",
            Category = BenchmarkProblemCategory.CodeReview,
            Language = BenchmarkLanguages.CSharp,
            Repo = "o/r",
            BaseCommit = "abc",
            ProblemStatement = "Review",
            FailToPass = ["BugTest"],
            PassToPass = ["RegTest"]
        };
        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "cr-bad-cand",
            Category = BenchmarkProblemCategory.CodeReview,
            WorkspacePath = _tempDir,
            BaseCommit = "abc"
        };

        var result = await verifier.RunImplementationTestsAsync(
            instance,
            handle,
            "bad candidate patch",
            ExecutionMode.BareHost,
            failToPassOnly: true);

        Assert.False(result.Passed);
        Assert.True(result.IsPatchApplicationError);
        Assert.False(result.IsInfrastructureError);
        Assert.Contains("Patch failed to apply cleanly", result.ErrorMessage);
        execEnv.Verify(e => e.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunImplementationTestsAsync_TestPatchApplyFailure_SetsIsPatchApplicationErrorAndSkipsTestExecution()
    {
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(m => m.ApplyPatchAsync(_tempDir, "bad test patch", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkWorkspaceException("test_patch conflict", workspacePath: _tempDir));

        var execEnv = new Mock<IExecutionEnvironment>();
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(execEnv.Object);

        var verifier = new BenchmarkVerifier(
            workspaceManager.Object,
            new Mock<IContainerEnvironmentFactory>().Object,
            envProvider.Object,
            new Mock<IBenchmarkLlmJudge>().Object,
            NullLogger<BenchmarkVerifier>.Instance);

        var instance = new BenchmarkInstance
        {
            InstanceId = "cr-bad-test-patch",
            Category = BenchmarkProblemCategory.CodeReview,
            Language = BenchmarkLanguages.CSharp,
            Repo = "o/r",
            BaseCommit = "abc",
            ProblemStatement = "Review",
            TestPatch = "bad test patch",
            FailToPass = ["BugTest"]
        };
        var handle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "cr-bad-test-patch",
            Category = BenchmarkProblemCategory.CodeReview,
            WorkspacePath = _tempDir,
            BaseCommit = "abc"
        };

        var result = await verifier.RunImplementationTestsAsync(
            instance,
            handle,
            "valid candidate patch",
            ExecutionMode.BareHost,
            failToPassOnly: true);

        Assert.False(result.Passed);
        Assert.True(result.IsPatchApplicationError);
        Assert.False(result.IsInfrastructureError);
        Assert.Contains("Evaluation TestPatch failed to apply cleanly", result.ErrorMessage);
        execEnv.Verify(e => e.ExecuteAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ResolveTestCommands_PreservesDotNetFilterAndXunitRunSettingsBeforeAndAfterDoubleDash()
    {
        var csDir = Path.Combine(_tempDir, "restsharp-1676-cmd");
        Directory.CreateDirectory(csDir);

        const string customCmd =
            "dotnet test test/RestSharp.IntegrationTests/RestSharp.IntegrationTests.csproj --nologo --filter FullyQualifiedName~StatusCodeTests --logger trx --results-directory /workspace/.ncf-bench-results -- xunit.parallelizeTestCollections=false";

        var instance = new BenchmarkInstance
        {
            InstanceId = "restsharp__restsharp-1676",
            Language = BenchmarkLanguages.CSharp,
            Repo = "restsharp/RestSharp",
            BaseCommit = "49ce0280",
            ProblemStatement = "Fix IsSuccessful propagation",
            CustomTestCommand = customCmd,
            FailToPass = ["RestSharp.IntegrationTests.StatusCodeTests.ContentType_Additional_Information"],
            PassToPass = ["RestSharp.IntegrationTests.StatusCodeTests.Reports_1xx_Status_Code_Success_Accurately"]
        };

        var agentCmd = BenchmarkTestCommandResolver.ResolveAgentTestCommand(instance, csDir, ExecutionMode.ContainerSandbox);
        Assert.Equal(
            "dotnet test test/RestSharp.IntegrationTests/RestSharp.IntegrationTests.csproj --nologo --filter FullyQualifiedName~StatusCodeTests --no-build -- xunit.parallelizeTestCollections=false",
            agentCmd);

        var verifyF2pCmd = BenchmarkTestCommandResolver.ResolveTestCommand(instance, csDir, ExecutionMode.ContainerSandbox, failToPassOnly: true);
        Assert.Equal(customCmd, verifyF2pCmd);
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), $"git {string.Join(' ', args)} timed out.");
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        return stdout;
    }
}
