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
        Assert.Equal(0.5, result.DeterministicScore, 2);
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
}
