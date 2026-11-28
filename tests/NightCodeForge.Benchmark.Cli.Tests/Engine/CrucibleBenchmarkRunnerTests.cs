using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow.Filtering;
using NightCodeForge.Infrastructure.Execution;
using Pmad.AI.Tools;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class CrucibleBenchmarkRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public CrucibleBenchmarkRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-runner-{Guid.NewGuid():N}");
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
    public async Task RunAsync_CapturesTelemetryExportsPredictionsAndAggregatesSummary()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"inst-cs","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Fix C# bug","fail_to_pass":["T1"]}""",
            """{"instance_id":"inst-py","language":"python","repo":"owner/py","base_commit":"c2","problem_statement":"Fix Py bug","fail_to_pass":["t2"]}"""
        ]));

        var outDir = Path.Combine(_tempDir, "out");
        var wks1 = Path.Combine(_tempDir, "wks-cs");
        var wks2 = Path.Combine(_tempDir, "wks-py");
        Directory.CreateDirectory(wks1);
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-cs"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-cs", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks1, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-py"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-py", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c2" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks1, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff --git a/A.cs b/A.cs\n+fix");
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks2, "c2", It.IsAny<CancellationToken>())).ReturnsAsync("diff --git a/a.py b/a.py\n+fix");

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (_, ct) =>
            {
                CrucibleDispatchTelemetryScope.Current?.Record(LenientDispatchEventType.ToolAliasResolved, "run_command", CanonicalToolNames.Bash);
                CrucibleDispatchTelemetryScope.Current?.Record(LenientDispatchEventType.ParameterAliasResolved, "file_path", "path");

                var sink = InMemoryCrucibleConversationCoordinator.CurrentCapture!.TurnSink;
                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Bash,
                    toolArguments: """{"command":"csnav find --query Foo"}""",
                    metadataJson: """{"CallId":"call-b1"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.ToolResult, toolName: CanonicalToolNames.Bash,
                    metadataJson: JsonSerializer.Serialize(new
                    {
                        CallId = "call-b1",
                        BashAudit = new BashExecutionAuditEntry("csnav find --query Foo", 0, null, "ok", 2, 2, false, false)
                    }), cancellationToken: ct);

                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Bash,
                    toolArguments: """{"command":"codecondenser analyze"}""",
                    metadataJson: """{"CallId":"call-b2"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.ToolResult, toolName: CanonicalToolNames.Bash,
                    metadataJson: JsonSerializer.Serialize(new
                    {
                        CallId = "call-b2",
                        BashAudit = new BashExecutionAuditEntry("codecondenser analyze", 0, null, "ok", 2, 2, false, false)
                    }), cancellationToken: ct);

                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Edit,
                    metadataJson: """{"CallId":"call-e1"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.ToolResult, toolName: CanonicalToolNames.Edit,
                    isError: true, metadataJson: """{"CallId":"call-e1"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Edit,
                    metadataJson: """{"CallId":"call-e2"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.ToolResult, toolName: CanonicalToolNames.Edit,
                    isError: false, metadataJson: """{"CallId":"call-e2"}""", cancellationToken: ct);

                return AgentRunResult.Completed(
                    new AgentRunMetrics(DurationMs: 500, PromptTokens: 120, CompletionTokens: 60, TotalTokens: 180),
                    summary: "Fixed");
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-cs"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0, FailToPassPassed = 1, FailToPassTotal = 1 });
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-py"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = false, DeterministicScore = 0.0, FailToPassPassed = 0, FailToPassTotal = 1 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(LlmProviderType.Ollama, "http://localhost:11434", true, It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "deadbeef", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var envProvider = new Mock<IExecutionEnvironmentProvider>();

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            envProvider.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var candidate = new BenchmarkModelCandidate
        {
            CandidateId = "qwen3-coder",
            DisplayName = "Qwen3 Coder",
            ModelName = "qwen3-coder:30b",
            EnableStackSkills = true
        };

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "smoke",
            DatasetPath = datasetPath,
            OutputDirectory = outDir,
            Candidates = [candidate],
            ExecutionMode = ExecutionMode.BareHost,
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434"
        });

        Assert.Equal(2, report.Results.Count);
        Assert.Single(report.Summaries);

        var summary = report.Summaries[0];
        Assert.Equal(2, summary.TotalInstances);
        Assert.Equal(1, summary.PassedInstances);
        Assert.Equal(1, summary.FailedInstances);
        Assert.Equal(0.5, summary.PassRate, 2);
        Assert.Equal(0.5, summary.EditFailureRate, 2);
        Assert.Equal(2, summary.TotalCsNavBashCalls);
        Assert.Equal(2, summary.TotalCodeCondenserBashCalls);
        Assert.Equal(4, summary.TotalDispatcherRepairs);
        Assert.Equal(2, summary.ByLanguage.Count);

        var predictionsFile = Path.Combine(outDir, "qwen3-coder", BenchmarkPaths.PredictionsFileName);
        Assert.True(File.Exists(predictionsFile));
        var predictionLines = await File.ReadAllLinesAsync(predictionsFile);
        Assert.Equal(2, predictionLines.Length);
        Assert.Contains("\"instance_id\":\"inst-cs\"", predictionLines[0]);
        Assert.True(File.Exists(Path.Combine(outDir, BenchmarkPaths.ResultsFileName)));

        // Verify resume skips already completed (candidate, instance) pairs
        harness.Invocations.Clear();
        var resumedOutDir = Path.Combine(_tempDir, "out-resumed");
        var resumedReport = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "smoke",
            DatasetPath = datasetPath,
            OutputDirectory = resumedOutDir,
            Candidates = [candidate],
            ExecutionMode = ExecutionMode.BareHost,
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434",
            ResumeFromDir = outDir
        });

        Assert.Equal(2, resumedReport.Results.Count);
        harness.Verify(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(File.Exists(Path.Combine(resumedOutDir, "qwen3-coder", BenchmarkPaths.PredictionsFileName)));
    }

    [Fact]
    public async Task RunAsync_CleansUpScopeEnvironment_UsesAgentDurationForTokensPerSecond_AndFailsOnAbnormalCancelledStatus()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-edge.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"i-ok","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Fix 1"}""",
            """{"instance_id":"i-cancelled","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Fix 2"}"""
        ]));

        var wks1 = Path.Combine(_tempDir, "wks-ok");
        var wks2 = Path.Combine(_tempDir, "wks-cancel");
        Directory.CreateDirectory(wks1);
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "i-ok"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "i-ok", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks1, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "i-cancelled"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "i-cancelled", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks1, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff");

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>((ctx, _) =>
            {
                if (string.Equals(ctx.ForgeTask.Title, "Fix 2", StringComparison.Ordinal))
                {
                    return Task.FromResult(AgentRunResult.Cancelled("External abort", new AgentRunMetrics(DurationMs: 50)));
                }

                return Task.FromResult(AgentRunResult.Completed(
                    new AgentRunMetrics(DurationMs: 500, PromptTokens: 100, CompletionTokens: 100, TotalTokens: 200),
                    summary: "Done"));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.ContainerSandbox, true, null, It.IsAny<CancellationToken>()))
            .Returns<BenchmarkInstance, BenchmarkWorkspaceHandle, BenchmarkAgentOutcome, ExecutionMode, bool, BenchmarkJudgeConfig?, CancellationToken>(async (_, _, _, _, _, _, ct) =>
            {
                await Task.Delay(250, ct);
                return new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 };
            });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            envProvider.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "edge",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-edge"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m" }],
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        envProvider.Verify(p => p.CleanupScopeEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var okRes = Assert.Single(report.Results, r => r.InstanceId == "i-ok");
        // 100 completion tokens / 0.5s (agent duration from metrics) = 200 tok/s regardless of verification delay
        Assert.Equal(200.0, okRes.TokensPerSecond, 1);

        var cancelledRes = Assert.Single(report.Results, r => r.InstanceId == "i-cancelled");
        Assert.Equal(BenchmarkInstanceOutcome.Error, cancelledRes.Outcome);
        Assert.Contains("External abort", cancelledRes.ErrorMessage);
        verifier.Verify(v => v.VerifyAsync(
            It.Is<BenchmarkInstance>(i => i.InstanceId == "i-cancelled"),
            It.IsAny<BenchmarkWorkspaceHandle>(),
            It.IsAny<BenchmarkAgentOutcome>(),
            It.IsAny<ExecutionMode>(),
            It.IsAny<bool>(),
            It.IsAny<BenchmarkJudgeConfig?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ValidatesCandidatesConcurrencyMaxInstances_CaseInsensitiveResume_AndUsesCommitRefsForReviews()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-reviews.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"cr-1","category":"code_review","repo":"a/b","base_commit":"base1","problem_statement":"Review","candidate_patch":"diff","expected_verdict":"Approve"}""",
            """{"instance_id":"tr-1","category":"task_review","repo":"a/b","base_commit":"base2","problem_statement":"Spec","task_specification":"# Spec","expected_verdict":"Ready"}"""
        ]));

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        var wksCr = Path.Combine(_tempDir, "wks-cr");
        var wksTr = Path.Combine(_tempDir, "wks-tr");
        Directory.CreateDirectory(wksCr);
        Directory.CreateDirectory(wksTr);

        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cr-1"), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cr-1", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = wksCr, BaseCommit = "base1", CandidateCommit = "cand-sha-1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "tr-1"), BenchmarkProblemCategory.TaskReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "tr-1", Category = BenchmarkProblemCategory.TaskReview, WorkspacePath = wksTr, BaseCommit = "base2", TaskMarkdownPath = "docs/tasks/tr-1.md" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wksCr, "cand-sha-1", It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        workspaceMgr.Setup(m => m.GetModifiedFilesAsync(wksTr, "base2", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRunResult.Completed(new AgentRunMetrics(100, 10, 10, 20), summary: "Ok"));

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), It.IsAny<ExecutionMode>(), It.IsAny<bool>(), It.IsAny<BenchmarkJudgeConfig?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        await Assert.ThrowsAsync<BenchmarkException>(() => runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "s",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "o1"),
            Concurrency = 0,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "c1", ModelName = "m1" }]
        }));
        await Assert.ThrowsAsync<BenchmarkException>(() => runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "s",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "o2"),
            MaxInstances = 0,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "c1", ModelName = "m1" }]
        }));
        await Assert.ThrowsAsync<BenchmarkException>(() => runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "s",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "o3"),
            Candidates =
            [
                new BenchmarkModelCandidate { CandidateId = "dup", ModelName = "m1" },
                new BenchmarkModelCandidate { CandidateId = "DUP", ModelName = "m2" }
            ]
        }));
        await Assert.ThrowsAsync<BenchmarkException>(() => runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "s",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "o4"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "c1", ModelName = "   " }]
        }));

        var outReviews = Path.Combine(_tempDir, "out-reviews");
        await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "reviews",
            DatasetPath = datasetPath,
            OutputDirectory = outReviews,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "Cand-A", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost
        });

        workspaceMgr.Verify(m => m.ExtractPatchAsync(wksCr, "cand-sha-1", It.IsAny<CancellationToken>()), Times.Once);
        workspaceMgr.Verify(m => m.GetModifiedFilesAsync(wksTr, "base2", It.IsAny<CancellationToken>()), Times.Once);

        // Case-insensitive resume skips already completed candidate even when casing differs
        harness.Invocations.Clear();
        await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "reviews",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-reviews-resumed"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand-a", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost,
            ResumeFromDir = outReviews
        });
        harness.Verify(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void BuildTelemetryBreakdown_ExtractsToolCountsEditFailuresAndBashCompanionsWithoutDoubleCounting()
    {
        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Read },
            new() { TurnIndex = 1, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Read, IsError = false },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Edit },
            new() { TurnIndex = 3, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Edit, IsError = true },
            new() { TurnIndex = 4, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Write },
            new() { TurnIndex = 5, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.LoadSkill },
            new() { TurnIndex = 6, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Ask },
            new() { TurnIndex = 7, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.RequestChanges },
            // Audited bash turn (both Tool and ToolResult share CallId; must count csnav once)
            new()
            {
                TurnIndex = 8,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Bash,
                ToolArguments = """{"command":"csnav definition --symbol Foo"}""",
                MetadataJson = """{"CallId":"call-audited"}"""
            },
            new()
            {
                TurnIndex = 9,
                Role = StageTurnRole.ToolResult,
                ToolName = CanonicalToolNames.Bash,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    CallId = "call-audited",
                    BashAudit = new BashExecutionAuditEntry("csnav definition --symbol Foo", 0, null, "ok", 2, 2, false, false)
                })
            },
            // Unaudited bash turn (fallback inspects ToolArguments.command)
            new()
            {
                TurnIndex = 10,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Bash,
                ToolArguments = """{"command":"codecondenser analyze src/"}""",
                MetadataJson = """{"CallId":"call-unaudited"}"""
            }
        };

        var telemetry = new LenientDispatchTelemetry();
        telemetry.Record(LenientDispatchEventType.StringifiedJsonRepaired, CanonicalToolNames.Edit, "edits");

        var breakdown = CrucibleBenchmarkRunner.BuildTelemetryBreakdown(turns, telemetry.CreateSnapshot());

        Assert.Equal(8, breakdown.TotalToolCalls);
        Assert.Equal(1, breakdown.ReadCalls);
        Assert.Equal(1, breakdown.EditCalls);
        Assert.Equal(1, breakdown.EditFailures);
        Assert.Equal(1, breakdown.WriteCalls);
        Assert.Equal(2, breakdown.BashCalls);
        Assert.Equal(1, breakdown.LoadSkillCalls);
        Assert.Equal(1, breakdown.AskCalls);
        Assert.Equal(1, breakdown.RequestChangesCalls);
        Assert.Equal(1, breakdown.CsNavBashCalls);
        Assert.Equal(1, breakdown.CodeCondenserBashCalls);
        Assert.Equal(1, breakdown.DispatcherRepairs);
        Assert.Equal(1, breakdown.StringifiedJsonRepaired);
    }

    [Fact]
    public async Task RunAsync_ResumesFromSnakeCaseStringEnumReport_AndFailsOnMalformedResumeJson()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-resume-enum.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"inst-1","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"One"}""",
            """{"instance_id":"inst-2","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Two"}"""
        ]));

        var resumeDir = Path.Combine(_tempDir, "resume-snake");
        Directory.CreateDirectory(resumeDir);
        const string snakeCaseResults = """
            {
              "suite_name": "smoke",
              "dataset_path": "dataset.jsonl",
              "provenance": {
                "ncf_version": "0.1.0",
                "git_commit": "abc",
                "git_branch": "main",
                "run_timestamp_utc": "2026-11-22T00:00:00Z",
                "provider_type": "ollama",
                "endpoint": "http://localhost:11434",
                "enable_stack_skills": true
              },
              "summaries": [],
              "results": [
                {
                  "instance_id": "inst-1",
                  "candidate_id": "cand",
                  "model_name": "m1",
                  "category": "implementation",
                  "language": "csharp",
                  "outcome": "passed",
                  "duration_ms": 100,
                  "deterministic_score": 1.0,
                  "model_patch": "diff --git a/A.cs b/A.cs"
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(resumeDir, BenchmarkPaths.ResultsFileName), snakeCaseResults);

        var wks2 = Path.Combine(_tempDir, "wks-2");
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-2"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-2", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks2, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff2");

        var evaluated = new List<string>();
        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>((ctx, _) =>
            {
                evaluated.Add(ctx.ForgeTask.Title);
                return Task.FromResult(AgentRunResult.Completed(new AgentRunMetrics(50, 10, 10, 20), summary: "Ok"));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "smoke",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-snake"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost,
            ResumeFromDir = resumeDir
        });

        Assert.Single(evaluated);
        Assert.Equal("Two", evaluated[0]);
        Assert.Equal(2, report.Results.Count);

        var badResumeDir = Path.Combine(_tempDir, "resume-bad");
        Directory.CreateDirectory(badResumeDir);
        await File.WriteAllTextAsync(Path.Combine(badResumeDir, BenchmarkPaths.ResultsFileName), "{not-valid-json");

        await Assert.ThrowsAsync<BenchmarkException>(() => runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "smoke",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-bad"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost,
            ResumeFromDir = badResumeDir
        }));
    }

    [Fact]
    public async Task RunAsync_ResumeFromSameOutputDirectoryWithTrailingSlash_DoesNotDuplicatePredictions()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-dedup.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"inst-1","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"One"}""");

        var outDir = Path.Combine(_tempDir, "out-same");
        var wks1 = Path.Combine(_tempDir, "wks-dedup-1");
        var wks2 = Path.Combine(_tempDir, "wks-dedup-2");
        Directory.CreateDirectory(wks1);
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-1"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-1", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks1, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-2"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-2", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks1, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff1");
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks2, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff2");

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRunResult.Completed(new AgentRunMetrics(50, 10, 10, 20), summary: "Ok"));

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var candidate = new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" };
        await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "dedup",
            DatasetPath = datasetPath,
            OutputDirectory = outDir,
            Candidates = [candidate],
            ExecutionMode = ExecutionMode.BareHost
        });

        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"inst-1","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"One"}""",
            """{"instance_id":"inst-2","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Two"}"""
        ]));

        await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "dedup",
            DatasetPath = datasetPath,
            OutputDirectory = outDir,
            Candidates = [candidate],
            ExecutionMode = ExecutionMode.BareHost,
            ResumeFromDir = outDir + Path.DirectorySeparatorChar
        });

        var predictionsFile = Path.Combine(outDir, "cand", BenchmarkPaths.PredictionsFileName);
        var lines = await File.ReadAllLinesAsync(predictionsFile);
        Assert.Equal(2, lines.Length);
        Assert.Single(lines, l => l.Contains("\"inst-1\"", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("\"inst-2\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_AbnormalFailedAndTimeoutResults_ComputeTotalTokensAndMinimumDuration()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-abnormal.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"i-fail","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Fail"}""",
            """{"instance_id":"i-timeout","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Timeout"}"""
        ]));

        var wks1 = Path.Combine(_tempDir, "wks-ab-1");
        var wks2 = Path.Combine(_tempDir, "wks-ab-2");
        Directory.CreateDirectory(wks1);
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "i-fail"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "i-fail", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks1, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "i-timeout"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "i-timeout", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c1" });

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                if (string.Equals(ctx.ForgeTask.Title, "Timeout", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }

                return AgentRunResult.Failed(
                    "Model error",
                    new AgentRunMetrics(DurationMs: 0, PromptTokens: 40, CompletionTokens: 15, TotalTokens: 0));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            new Mock<IBenchmarkVerifier>().Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "abnormal",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-abnormal"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1", InstanceTimeoutSeconds = 1 }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var failed = Assert.Single(report.Results, r => r.InstanceId == "i-fail");
        Assert.Equal(BenchmarkInstanceOutcome.Error, failed.Outcome);
        Assert.Equal(55, failed.TotalTokens);
        Assert.True(failed.DurationMs >= 1);

        var timedOut = Assert.Single(report.Results, r => r.InstanceId == "i-timeout");
        Assert.Equal(BenchmarkInstanceOutcome.Timeout, timedOut.Outcome);
        Assert.True(timedOut.DurationMs >= 1);
    }

    [Fact]
    public async Task RunAsync_RedactsInstanceIdFromPromptsIsolatesUnhandledErrorsWritesIncrementalSnapshotsAndConfiguresAntiCheatScopes()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-isolation.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"secret__repo-2083","language":"csharp","repo":"secret/repo","base_commit":"c1","problem_statement":"Fix assembly versioning policy","fail_to_pass":["T1"]}""",
            """{"instance_id":"secret__repo-9999","language":"csharp","repo":"secret/repo","base_commit":"c2","problem_statement":"Unhandled verifier failure case","fail_to_pass":["T2"]}"""
        ]));

        var outDir = Path.Combine(_tempDir, "out-isolation");
        var resultsFilePath = Path.Combine(outDir, BenchmarkPaths.ResultsFileName);
        var wks1 = Path.Combine(_tempDir, "wks-iso-1");
        var wks2 = Path.Combine(_tempDir, "wks-iso-2");
        Directory.CreateDirectory(wks1);
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "secret__repo-2083"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "secret__repo-2083", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks1, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "secret__repo-9999"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "secret__repo-9999", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c2" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks1, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff-1");
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks2, "c2", It.IsAny<CancellationToken>())).ReturnsAsync("diff-2");

        var capturedPrompts = new List<(string? Title, string? Description, string? Bootstrap, SandboxScopeDescriptor? Scope)>();
        bool snapshotExistedBeforeSecondInstanceCompleted = false;

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>((ctx, _) =>
            {
                capturedPrompts.Add((ctx.ForgeTask.Title, ctx.ForgeTask.Description, ctx.BootstrapPrompt, ctx.SandboxScope));
                if (ctx.ForgeTask.Title.Contains("Unhandled verifier failure", StringComparison.Ordinal))
                {
                    snapshotExistedBeforeSecondInstanceCompleted = File.Exists(resultsFilePath);
                }

                return Task.FromResult(AgentRunResult.Completed(new AgentRunMetrics(DurationMs: 50, PromptTokens: 10, CompletionTokens: 10, TotalTokens: 20), summary: "Ok"));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "secret__repo-2083"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0, FailToPassPassed = 1, FailToPassTotal = 1 });
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "secret__repo-9999"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated verifier crash"));

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "iso",
            DatasetPath = datasetPath,
            OutputDirectory = outDir,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost,
            Concurrency = 1
        });

        Assert.True(snapshotExistedBeforeSecondInstanceCompleted);
        Assert.Equal(2, report.Results.Count);

        var ok = Assert.Single(report.Results, r => r.InstanceId == "secret__repo-2083");
        Assert.Equal(BenchmarkInstanceOutcome.Passed, ok.Outcome);

        var errored = Assert.Single(report.Results, r => r.InstanceId == "secret__repo-9999");
        Assert.Equal(BenchmarkInstanceOutcome.Error, errored.Outcome);
        Assert.Equal("Simulated verifier crash", errored.ErrorMessage);
        Assert.Equal("diff-2", errored.ModelPatch);

        Assert.All(capturedPrompts, p =>
        {
            Assert.DoesNotContain("secret__repo-", p.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret__repo-", p.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret__repo-", p.Bootstrap ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(p.Scope);
            Assert.False(p.Scope!.MountAcpHostPaths);
            Assert.False(p.Scope.DisableNetwork);
            Assert.Contains("api.github.com", p.Scope.BlockedHosts);
        });

        var trScope = CrucibleBenchmarkRunner.BuildExecutionScope(
            wks1,
            null,
            ExecutionMode.ContainerSandbox,
            "ncf-bench-tr",
            enableStackSkills: true,
            language: BenchmarkLanguages.CSharp,
            category: BenchmarkProblemCategory.TaskReview);
        Assert.False(trScope.DisableNetwork);
        Assert.Equal(BenchmarkDefaults.BlockedAntiCheatHosts, trScope.BlockedHosts);
        Assert.False(trScope.MountAcpHostPaths);
    }

    [Fact]
    public async Task RunAsync_CSharpGlobalJson_IsRelaxedDuringExecutionAndRestoredBeforeExtractPatch()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-globaljson.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cs-gj-1","language":"csharp","repo":"serilog/serilog","base_commit":"c1","problem_statement":"Fix bug"}""");

        var wks = Path.Combine(_tempDir, "wks-gj");
        Directory.CreateDirectory(wks);
        var globalJsonPath = Path.Combine(wks, "global.json");
        const string originalGlobalJson = "{\"sdk\":{\"version\":\"7.0.102\",\"rollForward\":\"latestFeature\"}}";
        await File.WriteAllTextAsync(globalJsonPath, originalGlobalJson);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-gj-1", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks, BaseCommit = "c1" });

        string? globalJsonDuringAgent = null;
        string? globalJsonAtExtract = null;
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks, "c1", It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, _, _) =>
            {
                globalJsonAtExtract = File.ReadAllText(globalJsonPath);
                return Task.FromResult("diff --git a/Fix.cs b/Fix.cs\n+fix");
            });

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>((_, _) =>
            {
                globalJsonDuringAgent = File.ReadAllText(globalJsonPath);
                return Task.FromResult(AgentRunResult.Completed(new AgentRunMetrics(DurationMs: 50, PromptTokens: 10, CompletionTokens: 10, TotalTokens: 20), summary: "Ok"));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "gj",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-gj"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost
        });

        Assert.Single(report.Results);
        Assert.NotNull(globalJsonDuringAgent);
        Assert.Contains("\"latestMajor\"", globalJsonDuringAgent);
        Assert.Equal(originalGlobalJson, globalJsonAtExtract);
    }

    [Fact]
    public async Task RunAsync_CSharpTimeout_RestoresGlobalJsonAndDrainsPendingConversationTurns()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-timeout-drain.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cs-to-1","language":"csharp","repo":"serilog/serilog","base_commit":"c1","problem_statement":"Fix bug"}""");

        var wks = Path.Combine(_tempDir, "wks-to");
        Directory.CreateDirectory(wks);
        var globalJsonPath = Path.Combine(wks, "global.json");
        const string originalGlobalJson = "{\"sdk\":{\"version\":\"7.0.102\",\"rollForward\":\"latestFeature\"}}";
        await File.WriteAllTextAsync(globalJsonPath, originalGlobalJson);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-to-1", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks, BaseCommit = "c1" });

        var coordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(_tempDir),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                var state = await coordinator.BeginAsync(ctx, StandardHarnessIds.Crucible, ct);
                var capture = InMemoryCrucibleConversationCoordinator.CurrentCapture!;
                state.EnqueueWrite(
                    async () =>
                    {
                        await Task.Delay(120);
                        await capture.TurnSink.AddTurnAsync(
                            state.Conversation.Id,
                            StageTurnRole.Tool,
                            toolName: CanonicalToolNames.Read,
                            toolArguments: """{"path":"Fix.cs"}""",
                            cancellationToken: CancellationToken.None);
                    },
                    "delayed test turn");

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "unreachable");
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            new Mock<IBenchmarkVerifier>().Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "to-drain",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-to"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1", InstanceTimeoutSeconds = 1 }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Timeout, result.Outcome);
        Assert.Equal(originalGlobalJson, await File.ReadAllTextAsync(globalJsonPath));
        Assert.Equal(1, result.Turns);
        Assert.Equal(1, result.Telemetry.ReadCalls);
    }

    [Fact]
    public async Task RunAsync_TimeoutAfterTranscriptSave_PreservesCapturedTokensAndTokensPerSecond()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-timeout-tokens.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cs-to-tokens","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Timeout with tokens"}""");

        var wks = Path.Combine(_tempDir, "wks-to-tokens");
        Directory.CreateDirectory(wks);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-to-tokens", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks, BaseCommit = "c1" });

        var coordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(_tempDir),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                var state = await coordinator.BeginAsync(ctx, StandardHarnessIds.Crucible, ct);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    await coordinator.SaveTranscriptAsync(
                        state,
                        new CrucibleTranscriptRequest(
                            ctx,
                            wks,
                            state.ChatHistory,
                            ElapsedMs: 2_000,
                            PromptTokens: 4_000,
                            CompletionTokens: 600,
                            Status: AgentRunStatus.Cancelled,
                            Summary: "Stage cancelled",
                            TracingClient: null,
                            HarnessOptions: null,
                            ActiveSkills: null),
                        CancellationToken.None);
                    throw;
                }

                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "unreachable");
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            new Mock<IBenchmarkVerifier>().Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "to-tokens",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-to-tokens"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1", InstanceTimeoutSeconds = 1 }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Timeout, result.Outcome);
        Assert.Equal(4_000, result.PromptTokens);
        Assert.Equal(600, result.CompletionTokens);
        Assert.Equal(4_600, result.TotalTokens);
        Assert.Equal(300.0, result.TokensPerSecond, 1);
    }

    [Fact]
    public void BuildTelemetryBreakdown_ReconcilesAllDispatcherRepairCategoriesWithTotalHits()
    {
        var telemetry = new LenientDispatchTelemetry();
        telemetry.Record(LenientDispatchEventType.ToolAliasResolved, "run_command", CanonicalToolNames.Bash);
        telemetry.Record(LenientDispatchEventType.ParameterAliasResolved, "file_path", "path");
        telemetry.Record(LenientDispatchEventType.NamespaceStripped, "tools.read", CanonicalToolNames.Read);
        telemetry.Record(LenientDispatchEventType.CasingNormalized, "READ", CanonicalToolNames.Read);
        telemetry.Record(LenientDispatchEventType.EnumValueSnapped, "FULL", "full");
        telemetry.Record(LenientDispatchEventType.StringifiedJsonRepaired, CanonicalToolNames.Edit, "edits");
        telemetry.Record(LenientDispatchEventType.ParameterEnvelopeUnwrapped, CanonicalToolNames.Ask, "parameters");
        // EnumValueUnmatched is not counted in LenientDispatchTelemetrySnapshot.TotalHits
        telemetry.Record(LenientDispatchEventType.EnumValueUnmatched, "invalid", "invalid");

        var snapshot = telemetry.CreateSnapshot();
        var breakdown = CrucibleBenchmarkRunner.BuildTelemetryBreakdown(Array.Empty<ConversationTurn>(), snapshot);

        Assert.Equal(7, breakdown.DispatcherRepairs);
        Assert.Equal(1, breakdown.ToolAliasesResolved);
        Assert.Equal(1, breakdown.ParameterAliasesResolved);
        Assert.Equal(1, breakdown.NamespacesStripped);
        Assert.Equal(1, breakdown.CasingNormalized);
        Assert.Equal(1, breakdown.EnumValuesSnapped);
        Assert.Equal(1, breakdown.StringifiedJsonRepaired);
        Assert.Equal(1, breakdown.ParameterEnvelopesUnwrapped);
        Assert.Equal(0, breakdown.OtherDispatcherRepairs);

        int sumOfCategories = breakdown.ToolAliasesResolved
            + breakdown.ParameterAliasesResolved
            + breakdown.NamespacesStripped
            + breakdown.CasingNormalized
            + breakdown.EnumValuesSnapped
            + breakdown.StringifiedJsonRepaired
            + breakdown.ParameterEnvelopesUnwrapped
            + breakdown.OtherDispatcherRepairs;
        Assert.Equal(breakdown.DispatcherRepairs, sumOfCategories);

        // Verify fallback attribution when TotalHits exceeds known breakdown categories
        var uncategorizedSnapshot = new LenientDispatchTelemetrySnapshot(
            TotalHits: 5,
            ToolAliasesResolved: 0,
            ParameterAliasesResolved: 1,
            NamespacesStripped: 0,
            CasingNormalized: 0,
            EnumValuesSnapped: 0,
            EnumValuesUnmatched: 0,
            StringifiedJsonRepaired: 0);
        var fallbackBreakdown = CrucibleBenchmarkRunner.BuildTelemetryBreakdown(Array.Empty<ConversationTurn>(), uncategorizedSnapshot);
        Assert.Equal(5, fallbackBreakdown.DispatcherRepairs);
        Assert.Equal(1, fallbackBreakdown.ParameterAliasesResolved);
        Assert.Equal(4, fallbackBreakdown.OtherDispatcherRepairs);
    }

    [Fact]
    public async Task RunAsync_ScopesCSharpSkillsAndDotnetToolsMountToCSharpInstancesOnly()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-lang-skills.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"cs-inst","language":"csharp","repo":"owner/cs","base_commit":"c1","problem_statement":"C# fix"}""",
            """{"instance_id":"py-inst","language":"python","repo":"owner/py","base_commit":"c2","problem_statement":"Py fix"}""",
            """{"instance_id":"rs-inst","language":"rust","repo":"owner/rs","base_commit":"c3","problem_statement":"Rust fix"}"""
        ]));

        var wksCs = Path.Combine(_tempDir, "wks-lang-cs");
        var wksPy = Path.Combine(_tempDir, "wks-lang-py");
        var wksRs = Path.Combine(_tempDir, "wks-lang-rs");
        Directory.CreateDirectory(wksCs);
        Directory.CreateDirectory(wksPy);
        Directory.CreateDirectory(wksRs);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cs-inst"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-inst", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wksCs, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "py-inst"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "py-inst", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wksPy, BaseCommit = "c2" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "rs-inst"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "rs-inst", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wksRs, BaseCommit = "c3" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("diff");

        var capturedByRepo = new Dictionary<string, (string? DisabledSkillsJson, bool MountHostDotnetTools)>(StringComparer.OrdinalIgnoreCase);
        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>((ctx, _) =>
            {
                capturedByRepo[ctx.Workspace!.Name] = (ctx.Workspace.DisabledGlobalSkillsJson, ctx.SandboxScope?.MountHostDotnetTools ?? false);
                return Task.FromResult(AgentRunResult.Completed(new AgentRunMetrics(50, 10, 10, 20), summary: "Ok"));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "lang-skills",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-lang-skills"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1", EnableStackSkills = true }],
            ExecutionMode = ExecutionMode.BareHost
        });

        Assert.True(capturedByRepo["owner/cs"].MountHostDotnetTools);
        Assert.DoesNotContain(BenchmarkSkillNames.CSharpNav, capturedByRepo["owner/cs"].DisabledSkillsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BenchmarkSkillNames.CodeCondenser, capturedByRepo["owner/cs"].DisabledSkillsJson, StringComparison.OrdinalIgnoreCase);

        foreach (var nonCSharpRepo in new[] { "owner/py", "owner/rs" })
        {
            Assert.False(capturedByRepo[nonCSharpRepo].MountHostDotnetTools);
            Assert.Contains(BenchmarkSkillNames.CSharpNav, capturedByRepo[nonCSharpRepo].DisabledSkillsJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BenchmarkSkillNames.CodeCondenser, capturedByRepo[nonCSharpRepo].DisabledSkillsJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BenchmarkSkillNames.MdFetch, capturedByRepo[nonCSharpRepo].DisabledSkillsJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ResolveEffectiveMetrics_UsesCapturedMetricsAtomically_WithoutMixingDurationOrTokenFields()
    {
        // Primary has DurationMs > 0 with zero tokens; captured has tokens + its own DurationMs.
        // TokensPerSecond must use captured.DurationMs (2_000ms -> 600 / 2.0 = 300.0), not primary.DurationMs (10_000ms).
        var primaryZeroTokens = new AgentRunMetrics(DurationMs: 10_000, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0);
        var captured = new AgentRunMetrics(DurationMs: 2_000, PromptTokens: 4_000, CompletionTokens: 600, TotalTokens: 4_600);

        var resolvedFromCaptured = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(primaryZeroTokens, captured, fallbackAgentDurationMs: 5_000);
        Assert.Equal(4_000, resolvedFromCaptured.PromptTokens);
        Assert.Equal(600, resolvedFromCaptured.CompletionTokens);
        Assert.Equal(4_600, resolvedFromCaptured.TotalTokens);
        Assert.Equal(300.0, resolvedFromCaptured.TokensPerSecond, 2);

        // Primary has PromptTokens > 0 and CompletionTokens == 0; must NOT mix CompletionTokens from captured.
        var primaryPromptOnly = new AgentRunMetrics(DurationMs: 1_000, PromptTokens: 250, CompletionTokens: 0, TotalTokens: 0);
        var resolvedFromPrimary = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(primaryPromptOnly, captured, fallbackAgentDurationMs: 5_000);
        Assert.Equal(250, resolvedFromPrimary.PromptTokens);
        Assert.Equal(0, resolvedFromPrimary.CompletionTokens);
        Assert.Equal(250, resolvedFromPrimary.TotalTokens);
        Assert.Equal(0.0, resolvedFromPrimary.TokensPerSecond, 2);
    }

    [Fact]
    public async Task RunAsync_AbnormalFailedResultWithDurationAndZeroTokens_UsesCapturedMetricsDurationForTokensPerSecond()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-failed-tokens.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cs-fail-tokens","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Fail with tokens"}""");

        var wks = Path.Combine(_tempDir, "wks-fail-tokens");
        Directory.CreateDirectory(wks);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-fail-tokens", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks, BaseCommit = "c1" });

        var coordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(_tempDir),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                var state = await coordinator.BeginAsync(ctx, StandardHarnessIds.Crucible, ct);
                await coordinator.SaveTranscriptAsync(
                    state,
                    new CrucibleTranscriptRequest(
                        ctx,
                        wks,
                        state.ChatHistory,
                        ElapsedMs: 2_000,
                        PromptTokens: 1_200,
                        CompletionTokens: 600,
                        Status: AgentRunStatus.Failed,
                        Summary: "Failed",
                        TracingClient: null,
                        HarnessOptions: null,
                        ActiveSkills: null),
                    ct);

                return AgentRunResult.Failed("Simulated failure", new AgentRunMetrics(DurationMs: 10_000));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            new Mock<IBenchmarkVerifier>().Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "fail-tokens",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-fail-tokens"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Error, result.Outcome);
        Assert.Equal(1_200, result.PromptTokens);
        Assert.Equal(600, result.CompletionTokens);
        Assert.Equal(1_800, result.TotalTokens);
        Assert.Equal(300.0, result.TokensPerSecond, 1);
    }

    [Fact]
    public async Task RunAsync_UnhandledHarnessException_DrainsPendingTurnsAndPreservesCapturedMetrics()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-unhandled-ex.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cs-ex-1","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"Unhandled exception"}""");

        var wks = Path.Combine(_tempDir, "wks-ex-1");
        Directory.CreateDirectory(wks);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-ex-1", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks, BaseCommit = "c1" });

        var coordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(_tempDir),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                var state = await coordinator.BeginAsync(ctx, StandardHarnessIds.Crucible, ct);
                var capture = InMemoryCrucibleConversationCoordinator.CurrentCapture!;
                state.EnqueueWrite(
                    async () =>
                    {
                        await Task.Delay(80);
                        await capture.TurnSink.AddTurnAsync(
                            state.Conversation.Id,
                            StageTurnRole.Tool,
                            toolName: CanonicalToolNames.Read,
                            toolArguments: """{"path":"Bug.cs"}""",
                            cancellationToken: CancellationToken.None);
                    },
                    "delayed turn before unhandled throw");

                await coordinator.SaveTranscriptAsync(
                    state,
                    new CrucibleTranscriptRequest(
                        ctx,
                        wks,
                        state.ChatHistory,
                        ElapsedMs: 2_000,
                        PromptTokens: 1_000,
                        CompletionTokens: 400,
                        Status: AgentRunStatus.Failed,
                        Summary: "Crash",
                        TracingClient: null,
                        HarnessOptions: null,
                        ActiveSkills: null),
                    ct);

                throw new InvalidOperationException("Simulated unhandled harness crash");
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            new Mock<IBenchmarkVerifier>().Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "unhandled-ex",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-ex"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Error, result.Outcome);
        Assert.Equal("Simulated unhandled harness crash", result.ErrorMessage);
        Assert.Equal(1, result.Turns);
        Assert.Equal(1, result.Telemetry.ReadCalls);
        Assert.Equal(1_000, result.PromptTokens);
        Assert.Equal(400, result.CompletionTokens);
        Assert.Equal(1_400, result.TotalTokens);
        Assert.Equal(200.0, result.TokensPerSecond, 1);
    }

    [Theory]
    [InlineData(true, "csharp", true)]
    [InlineData(true, "c#", true)]
    [InlineData(true, "dotnet", true)]
    [InlineData(true, null, true)]
    [InlineData(false, "csharp", false)]
    [InlineData(true, "python", false)]
    [InlineData(true, "rust", false)]
    [InlineData(true, "typescript", false)]
    [InlineData(true, "javascript", false)]
    [InlineData(true, "go", false)]
    public void BuildExecutionScope_MountsHostDotnetToolsOnlyWhenSkillsEnabledAndLanguageIsCSharp(
        bool enableStackSkills,
        string? language,
        bool expectedMount)
    {
        var scope = CrucibleBenchmarkRunner.BuildExecutionScope(
            _tempDir,
            null,
            ExecutionMode.ContainerSandbox,
            "ncf-bench-scope",
            enableStackSkills,
            language);

        Assert.Equal(expectedMount, scope.MountHostDotnetTools);
    }

    [Fact]
    public void ToolCallTelemetryBreakdown_SnakeCaseJsonRoundTrip_PreservesExtendedRepairFields()
    {
        var original = new ToolCallTelemetryBreakdown
        {
            TotalToolCalls = 4,
            DispatcherRepairs = 7,
            ToolAliasesResolved = 1,
            ParameterAliasesResolved = 1,
            NamespacesStripped = 1,
            CasingNormalized = 1,
            EnumValuesSnapped = 1,
            StringifiedJsonRepaired = 1,
            ParameterEnvelopesUnwrapped = 2,
            OtherDispatcherRepairs = 1
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        var json = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<ToolCallTelemetryBreakdown>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original, roundTripped);
        Assert.Contains("\"namespaces_stripped\":1", json);
        Assert.Contains("\"casing_normalized\":1", json);
        Assert.Contains("\"enum_values_snapped\":1", json);
        Assert.Contains("\"parameter_envelopes_unwrapped\":2", json);
        Assert.Contains("\"other_dispatcher_repairs\":1", json);
        Assert.Contains("\"anti_cheat_alert_count\":0", json);
    }

    [Fact]
    public async Task RunAsync_PersistsVerificationLogToDiskAndRecordsVerificationLogPathInResultsJson()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-vlog.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"inst-with-log","language":"csharp","repo":"owner/repo","base_commit":"c1","problem_statement":"One","fail_to_pass":["T1"]}""",
            """{"instance_id":"inst-no-log","language":"csharp","repo":"owner/repo","base_commit":"c2","problem_statement":"Two","fail_to_pass":["T2"]}"""
        ]));

        var outDir = Path.Combine(_tempDir, "out-vlog");
        var wks1 = Path.Combine(_tempDir, "wks-vlog-1");
        var wks2 = Path.Combine(_tempDir, "wks-vlog-2");
        Directory.CreateDirectory(wks1);
        Directory.CreateDirectory(wks2);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-with-log"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-with-log", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks1, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-no-log"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-no-log", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks2, BaseCommit = "c2" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks1, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff1");
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks2, "c2", It.IsAny<CancellationToken>())).ReturnsAsync("diff2");

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRunResult.Completed(new AgentRunMetrics(50, 10, 10, 20), summary: "Ok"));

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        const string expectedLog = "EXIT_CODE: 1\n\nSTDOUT:\n1 failed\n\nSTDERR:\nError: ENOTSUP\n";
        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-with-log"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                DeterministicScore = 0.0,
                VerificationLog = expectedLog,
                ErrorMessage = "Failed"
            });
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-no-log"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = true,
                DeterministicScore = 1.0,
                VerificationLog = "   "
            });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "vlog",
            DatasetPath = datasetPath,
            OutputDirectory = outDir,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand-1", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var withLog = Assert.Single(report.Results, r => r.InstanceId == "inst-with-log");
        var expectedPath = Path.Combine(outDir, BenchmarkPaths.VerificationLogsSubdir, "cand-1", "inst-with-log.log");
        Assert.Equal(expectedPath, withLog.VerificationLogPath);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(expectedLog, await File.ReadAllTextAsync(expectedPath));

        var noLog = Assert.Single(report.Results, r => r.InstanceId == "inst-no-log");
        Assert.Null(noLog.VerificationLogPath);
        Assert.False(File.Exists(Path.Combine(outDir, BenchmarkPaths.VerificationLogsSubdir, "cand-1", "inst-no-log.log")));

        var resultsJson = await File.ReadAllTextAsync(Path.Combine(outDir, BenchmarkPaths.ResultsFileName));
        Assert.Contains("\"verification_log_path\":", resultsJson);
    }

    [Fact]
    public async Task RunAsync_PopulatesLanguageAccurateProjectConfigOnAgentSessionContext()
    {
        var datasetPath = Path.Combine(_tempDir, "dataset-project-config.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"inst-cs-cfg","language":"csharp","repo":"owner/cs","base_commit":"c1","problem_statement":"Fix C#"}""",
            """{"instance_id":"inst-py-cfg","language":"python","repo":"pallets/flask","base_commit":"c2","problem_statement":"Fix Python"}"""
        ]));

        var wksCs = Path.Combine(_tempDir, "wks-cs-cfg");
        var wksPy = Path.Combine(_tempDir, "wks-py-cfg");
        Directory.CreateDirectory(wksCs);
        Directory.CreateDirectory(wksPy);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-cs-cfg"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-cs-cfg", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wksCs, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "inst-py-cfg"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "inst-py-cfg", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wksPy, BaseCommit = "c2" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("diff --git a/x b/x\n+1");

        var capturedConfigs = new System.Collections.Concurrent.ConcurrentDictionary<string, WorkspaceProjectConfig?>(StringComparer.Ordinal);
        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>((ctx, _) =>
            {
                capturedConfigs[ctx.WorkspacePath] = ctx.ProjectConfig;
                return Task.FromResult(AgentRunResult.Completed(new AgentRunMetrics(10, 1, 1, 2), summary: "Ok"));
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.ContainerSandbox, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "cfg",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-cfg"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand-1", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        Assert.True(capturedConfigs.TryGetValue(wksCs, out var csCfg) && csCfg != null);
        Assert.Equal(CodeStack.DotNet, csCfg.CodeStack);
        Assert.Equal("dotnet build", csCfg.BuildCommand);
        Assert.Equal("dotnet test --no-build", csCfg.TestCommand);
        Assert.DoesNotContain("--logger trx", csCfg.TestCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ncf-bench-results", csCfg.TestCommand, StringComparison.OrdinalIgnoreCase);

        Assert.True(capturedConfigs.TryGetValue(wksPy, out var pyCfg) && pyCfg != null);
        Assert.Equal(CodeStack.Python, pyCfg.CodeStack);
        Assert.Equal(CodeStackPresets.Python.BuildCommand, pyCfg.BuildCommand);
        Assert.Equal(CodeStackPresets.Python.TestCommand, pyCfg.TestCommand);
        Assert.DoesNotContain("--junitxml", pyCfg.TestCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ncf-bench-results", pyCfg.TestCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet", pyCfg.TestCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WhenRustToolchainPreflightFails_FailsFastOnRustWithoutAbortingOtherInstances()
    {
        var datasetPath = Path.Combine(_tempDir, "mixed-runner-preflight.jsonl");
        await File.WriteAllTextAsync(datasetPath, string.Join(Environment.NewLine,
        [
            """{"instance_id":"rust-missing","category":"Implementation","language":"rust","repo":"burntsushi/ripgrep","base_commit":"c1","problem_statement":"Fix rust"}""",
            """{"instance_id":"cs-ok","category":"Implementation","language":"csharp","repo":"owner/repo","base_commit":"c2","problem_statement":"Fix cs"}"""
        ]));

        var wksCs = Path.Combine(_tempDir, "wks-cs-ok");
        Directory.CreateDirectory(wksCs);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cs-ok"), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cs-ok", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wksCs, BaseCommit = "c2" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wksCs, "c2", It.IsAny<CancellationToken>()))
            .ReturnsAsync("diff --git a/A.cs b/A.cs\n+fix");

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRunResult.Completed(new AgentRunMetrics(10, 10, 5, 15), summary: "Done"));

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cs-ok"), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.ContainerSandbox, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var preflight = new Mock<IBenchmarkToolchainPreflightValidator>();
        preflight.Setup(p => p.ValidateAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "rust-missing"), BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Pre-flight Rust toolchain check failed for 'rust-missing': install rustup.");
        preflight.Setup(p => p.ValidateAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "cs-ok"), BenchmarkProblemCategory.Implementation, ExecutionMode.ContainerSandbox, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            preflight.Object,
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "preflight",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-preflight"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand-1", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434"
        });

        Assert.Equal(2, report.Results.Count);
        var rustResult = Assert.Single(report.Results, r => r.InstanceId == "rust-missing");
        Assert.Equal(BenchmarkInstanceOutcome.Error, rustResult.Outcome);
        Assert.Contains("install rustup", rustResult.ErrorMessage);

        var csResult = Assert.Single(report.Results, r => r.InstanceId == "cs-ok");
        Assert.Equal(BenchmarkInstanceOutcome.Passed, csResult.Outcome);

        workspaceMgr.Verify(m => m.ProvisionAsync(It.Is<BenchmarkInstance>(i => i.InstanceId == "rust-missing"), It.IsAny<BenchmarkProblemCategory>(), null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Step1163_RunAsync_WhenAgentReachesMaxTurns_ReportsExplicitMaxTurnsExhaustionDiagnostic()
    {
        var datasetPath = Path.Combine(_tempDir, "max-turns.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"cr-timeout","category":"CodeReview","language":"python","repo":"pallets/flask","custom_container_image":"python:3.11","pip_constraints":["Werkzeug<3.0"],"base_commit":"c1","problem_statement":"Review PR","candidate_patch":"diff --git a/a.py b/a.py\n--- a/a.py\n+++ b/a.py\n@@ -1 +1 @@\n-a\n+b","expected_verdict":"Approve"}""");

        var wks = Path.Combine(_tempDir, "wks-max-turns");
        Directory.CreateDirectory(wks);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.CodeReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "cr-timeout", Category = BenchmarkProblemCategory.CodeReview, WorkspacePath = wks, BaseCommit = "c1" });

        string? capturedPrompt = null;
        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                capturedPrompt = ctx.BootstrapPrompt;
                var sink = InMemoryCrucibleConversationCoordinator.CurrentCapture!.TurnSink;
                await sink.AddTurnAsync(1, StageTurnRole.Assistant, content: "Inspecting...", cancellationToken: ct);
                await sink.AddTurnAsync(2, StageTurnRole.Assistant, content: "Still checking...", cancellationToken: ct);
                await sink.AddTurnAsync(3, StageTurnRole.Assistant, content: "Running tests...", cancellationToken: ct);
                return AgentRunResult.Completed(new AgentRunMetrics(100, 50, 20, 70), summary: "Stopped at budget");
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = false, ErrorMessage = "Model did not invoke approve or request_changes." });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "max-turns",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-max-turns"),
            VerifyWithTests = false,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand-mt", ModelName = "m1", MaxTurns = 3 }],
            ExecutionMode = ExecutionMode.BareHost,
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://localhost:11434"
        });

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Failed, result.Outcome);
        Assert.Contains("Agent reached MaxTurns limit (3/3) without completing the task.", result.ErrorMessage);
        Assert.Contains("Model did not invoke approve or request_changes.", result.ErrorMessage);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("Werkzeug<3.0", capturedPrompt);
        Assert.Contains("PIP_CONSTRAINT=/tmp/ncf-pip-constraints.txt", capturedPrompt);
    }

    [Fact]
    public async Task Step1164_RunAsync_ClassifiesVerificationInfrastructureErrorAsErrorAndPersistsLog()
    {
        var datasetPath = Path.Combine(_tempDir, "infra-err.jsonl");
        await File.WriteAllTextAsync(datasetPath,
            """{"instance_id":"polly-1852","category":"Implementation","language":"csharp","repo":"App-vNext/Polly","base_commit":"c1","problem_statement":"Fix","fail_to_pass":["T1"]}""");

        var outDir = Path.Combine(_tempDir, "out-infra-err");
        var wks = Path.Combine(_tempDir, "wks-infra-err");
        Directory.CreateDirectory(wks);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.Implementation, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle { InstanceId = "polly-1852", Category = BenchmarkProblemCategory.Implementation, WorkspacePath = wks, BaseCommit = "c1" });
        workspaceMgr.Setup(m => m.ExtractPatchAsync(wks, "c1", It.IsAny<CancellationToken>())).ReturnsAsync("diff --git a/A.cs b/A.cs\n+fix");

        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRunResult.Completed(new AgentRunMetrics(50, 10, 10, 20), summary: "Ok"));

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        const string infraLog = "MSBUILD : error MSB4166: Child node \"2\" exited prematurely.";
        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult
            {
                Passed = false,
                IsInfrastructureError = true,
                DeterministicScore = 0.0,
                VerificationLog = infraLog,
                ErrorMessage = "Verification infrastructure error: MSBuild child-node crash persisted after single-node (-m:1) retry (exit code 1)."
            });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "infra-err",
            DatasetPath = datasetPath,
            OutputDirectory = outDir,
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand-1", ModelName = "m1", MaxTurns = 1 }],
            ExecutionMode = ExecutionMode.BareHost
        });

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Error, result.Outcome);
        Assert.NotNull(result.VerificationLogPath);
        Assert.True(File.Exists(result.VerificationLogPath));
        Assert.Equal(infraLog, await File.ReadAllTextAsync(result.VerificationLogPath!));
        Assert.StartsWith("Verification infrastructure error:", result.ErrorMessage);
    }

    [Fact]
    public void ResolveEffectiveMetrics_PrefersInferenceDurationMsForTokensPerSecond_AndFallsBackWhenZero()
    {
        var withInference = new AgentRunMetrics(
            DurationMs: 20_000,
            PromptTokens: 1_000,
            CompletionTokens: 200,
            TotalTokens: 1_200,
            InferenceDurationMs: 2_000);

        var resolved = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(withInference, capturedMetrics: null, fallbackAgentDurationMs: 30_000);
        Assert.Equal(100.0, resolved.TokensPerSecond, 2);

        // Falls back to wall-clock DurationMs when InferenceDurationMs <= 0
        var withoutInference = withInference with { InferenceDurationMs = 0 };
        var fallbackResolved = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(withoutInference, capturedMetrics: null, fallbackAgentDurationMs: 30_000);
        Assert.Equal(10.0, fallbackResolved.TokensPerSecond, 2);

        // Timeout/abort path: primary has zero tokens, captured has tokens + InferenceDurationMs
        var primaryZeroTokens = new AgentRunMetrics(DurationMs: 60_000, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0);
        var capturedWithInference = new AgentRunMetrics(DurationMs: 50_000, PromptTokens: 800, CompletionTokens: 150, TotalTokens: 950, InferenceDurationMs: 1_500);
        var capturedResolved = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(primaryZeroTokens, capturedWithInference, fallbackAgentDurationMs: 60_000);
        Assert.Equal(100.0, capturedResolved.TokensPerSecond, 2);

        // Cross-fallback: primary has tokens with InferenceDurationMs == 0 while captured has InferenceDurationMs > 0
        var primaryTokensNoInference = new AgentRunMetrics(DurationMs: 20_000, PromptTokens: 1_000, CompletionTokens: 200, TotalTokens: 1_200, InferenceDurationMs: 0);
        var capturedInferenceOnly = new AgentRunMetrics(DurationMs: 20_000, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0, InferenceDurationMs: 2_000);
        var crossFromCaptured = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(primaryTokensNoInference, capturedInferenceOnly, fallbackAgentDurationMs: 30_000);
        Assert.Equal(100.0, crossFromCaptured.TokensPerSecond, 2);

        // Cross-fallback: captured has tokens with InferenceDurationMs == 0 while primary has InferenceDurationMs > 0
        var primaryInferenceOnly = new AgentRunMetrics(DurationMs: 60_000, PromptTokens: 0, CompletionTokens: 0, TotalTokens: 0, InferenceDurationMs: 1_500);
        var capturedTokensNoInference = new AgentRunMetrics(DurationMs: 50_000, PromptTokens: 800, CompletionTokens: 150, TotalTokens: 950, InferenceDurationMs: 0);
        var crossFromPrimary = CrucibleBenchmarkRunner.ResolveEffectiveMetrics(primaryInferenceOnly, capturedTokensNoInference, fallbackAgentDurationMs: 60_000);
        Assert.Equal(100.0, crossFromPrimary.TokensPerSecond, 2);
    }

    [Fact]
    public void BlockedAntiCheatHosts_IncludesCdnAndSourceMirrors_AndOmitsPrimaryPackageRegistries()
    {
        var hosts = BenchmarkDefaults.BlockedAntiCheatHosts;
        foreach (var expectedCdnOrMirror in new[]
                 {
                     "unpkg.com",
                     "cdn.jsdelivr.net",
                     "esm.sh",
                     "skypack.dev",
                     "cdnjs.cloudflare.com",
                     "deps.dev",
                     "fuget.org",
                     "nuget.info",
                     "sourcegraph.com",
                     "grep.app",
                     "searchcode.com",
                     "github.com",
                     "raw.githubusercontent.com",
                     "gitlab.com",
                     "bitbucket.org",
                     "huggingface.co"
                 })
        {
            Assert.Contains(expectedCdnOrMirror, hosts);
        }

        foreach (var primaryRegistry in new[]
                 {
                     "pypi.org",
                     "files.pythonhosted.org",
                     "registry.npmjs.org",
                     "api.nuget.org",
                     "crates.io",
                     "static.crates.io"
                 })
        {
            Assert.DoesNotContain(primaryRegistry, hosts);
        }
    }

    [Theory]
    [InlineData("pip download pytest==7.2.0", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("/tmp/ncf-bench-venv/bin/pip download pytest==7.2.0", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("pip --no-cache-dir download pytest", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("python3 -m pip download flask -d /tmp/wheels", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("/tmp/ncf-bench-venv/bin/python -m pip download flask", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("npm pack @vue/compiler-core", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("/usr/local/bin/npm pack @vue/compiler-core", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("npm pack --pack-destination /tmp vue@3.4.0", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("npm view @vue/compiler-core version", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("npm --silent view lodash", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("npm info lodash", BenchmarkAntiCheatAuditor.PackageArchiveDownload)]
    [InlineData("curl -s https://unpkg.com/@vue/compiler-core/dist/compiler-core.cjs.js", BenchmarkAntiCheatAuditor.CdnOrSourceHostFetch)]
    [InlineData("wget https://raw.githubusercontent.com/vuejs/core/main/package.json", BenchmarkAntiCheatAuditor.CdnOrSourceHostFetch)]
    [InlineData("python3 -c \"import urllib.request; print(urllib.request.urlopen('https://cdn.jsdelivr.net/npm/vue').read())\"", BenchmarkAntiCheatAuditor.CdnOrSourceHostFetch)]
    [InlineData("git ls-remote https://github.com/pytest-dev/pytest.git", BenchmarkAntiCheatAuditor.ExternalGitRemoteQuery)]
    [InlineData("/usr/bin/git ls-remote https://github.com/org/repo.git", BenchmarkAntiCheatAuditor.ExternalGitRemoteQuery)]
    [InlineData("git fetch origin", BenchmarkAntiCheatAuditor.ExternalGitRemoteQuery)]
    [InlineData("git fetch --upload-pack /usr/bin/git-upload-pack origin", BenchmarkAntiCheatAuditor.ExternalGitRemoteQuery)]
    [InlineData("git clone git@github.com:org/repo.git /tmp/r", BenchmarkAntiCheatAuditor.ExternalGitRemoteQuery)]
    [InlineData("git clone user@host:org/repo.git /tmp/r", BenchmarkAntiCheatAuditor.ExternalGitRemoteQuery)]
    [InlineData("ilspycmd /cache/nuget/packages/spectre.console/0.49.1/lib/net8.0/Spectre.Console.dll", BenchmarkAntiCheatAuditor.BinaryOrCacheInspection)]
    [InlineData("~/.dotnet/tools/ilspycmd Foo.dll", BenchmarkAntiCheatAuditor.BinaryOrCacheInspection)]
    [InlineData("unzip -l /cache/nuget/spectre.console.0.49.1.nupkg", BenchmarkAntiCheatAuditor.BinaryOrCacheInspection)]
    [InlineData("rg \"Prompt\" /cache/cargo/registry/src/", BenchmarkAntiCheatAuditor.BinaryOrCacheInspection)]
    [InlineData("cat ~/.cargo/registry/src/foo/lib.rs", BenchmarkAntiCheatAuditor.BinaryOrCacheInspection)]
    [InlineData("CARGO_HOME=/cache/cargo nl /cache/cargo/registry/src/foo/lib.rs", BenchmarkAntiCheatAuditor.BinaryOrCacheInspection)]
    public void AntiCheatAuditor_FlagsHighSignalCheatPatterns(string command, string expectedSignal)
    {
        var signals = BenchmarkAntiCheatAuditor.DetectSignals(command);
        Assert.Contains(expectedSignal, signals);
    }

    [Theory]
    [InlineData("pip install -e .")]
    [InlineData("pip install -r requirements/tests.txt")]
    [InlineData("npm ci")]
    [InlineData("npm install --ignore-scripts")]
    [InlineData("npm pack .")]
    [InlineData("npm pack --dry-run")]
    [InlineData("npm pack > /tmp/pack.log")]
    [InlineData("npm pack --dry-run 2>/dev/null")]
    [InlineData("npm pack --loglevel silent .")]
    [InlineData("dotnet build --nologo")]
    [InlineData("dotnet test --no-build")]
    [InlineData("cargo test --no-fail-fast")]
    [InlineData("CARGO_HOME=/cache/cargo cargo test")]
    [InlineData("NUGET_PACKAGES=/cache/nuget/ dotnet build")]
    [InlineData("csnav decompile --type System.Uri")]
    [InlineData("curl -s http://localhost:8080/health")]
    [InlineData("wget -qO- http://127.0.0.1:5000/status")]
    [InlineData("rg \"github.com\" README.md")]
    [InlineData("git clone /tmp/local-src /tmp/local-dst")]
    [InlineData("git fetch file:///tmp/local-repo")]
    [InlineData("git fetch --depth 1 file:///tmp/local-repo")]
    [InlineData("git diff HEAD~1")]
    public void AntiCheatAuditor_AllowsLegitimateBuildTestAndLocalhostCommands(string command)
    {
        var signals = BenchmarkAntiCheatAuditor.DetectSignals(command);
        Assert.Empty(signals);
    }

    [Fact]
    public void BuildTelemetryBreakdown_DeduplicatesAntiCheatAlertsAcrossBashAuditAndToolArguments()
    {
        var turns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Bash,
                ToolArguments = """{"command":"curl -s https://unpkg.com/@vue/compiler-core"}""",
                MetadataJson = """{"CallId":"call-ac-1"}"""
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.ToolResult,
                ToolName = CanonicalToolNames.Bash,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    CallId = "call-ac-1",
                    BashAudit = new BashExecutionAuditEntry("curl -s https://unpkg.com/@vue/compiler-core", 0, null, "ok", 2, 2, false, false)
                })
            },
            new()
            {
                TurnIndex = 2,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Bash,
                ToolArguments = """{"command":"pip download pytest==7.2.0"}""",
                MetadataJson = """{"CallId":"call-ac-2"}"""
            },
            new()
            {
                TurnIndex = 3,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Bash,
                ToolArguments = """{"command":"dotnet test --no-build"}""",
                MetadataJson = """{"CallId":"call-ok"}"""
            }
        };

        var breakdown = CrucibleBenchmarkRunner.BuildTelemetryBreakdown(turns, new LenientDispatchTelemetry().CreateSnapshot());

        Assert.Equal(2, breakdown.AntiCheatAlertCount);
        Assert.Equal(
            [
                BenchmarkAntiCheatAuditor.CdnOrSourceHostFetch,
                BenchmarkAntiCheatAuditor.PackageArchiveDownload
            ],
            breakdown.AntiCheatSignals);
    }

    [Fact]
    public async Task RunAsync_TaskReview_SynchronizesFrontmatterIdAndFullTitle_AndTracksRevertedOutOfScopeEditsWithoutAlteringPassedScore()
    {
        const string longTitle = "Fix batchDepth decrement ordering and remove NO_BATCH effect flag across reactive watchers with nested recursive triggers";
        var taskSpec = $"""
            ---
            id: 202
            title: "{longTitle}"
            status: Open
            difficulty: standard
            ---

            Audit `endBatch` in `effect.ts`.
            """;

        var datasetPath = Path.Combine(_tempDir, "dataset-tr-sync.jsonl");
        await File.WriteAllTextAsync(datasetPath, JsonSerializer.Serialize(new
        {
            instance_id = "ncf-tr-ts-002",
            category = "task_review",
            language = "typescript",
            repo = "vuejs/core",
            base_commit = "3653bc0f",
            problem_statement = "Fallback problem statement that is definitely longer than eighty characters to verify truncation fallback",
            task_specification = taskSpec,
            expected_verdict = "Ready"
        }));

        var wks = Path.Combine(_tempDir, "wks-tr-sync");
        Directory.CreateDirectory(wks);

        var workspaceMgr = new Mock<IBenchmarkWorkspaceManager>();
        workspaceMgr.Setup(m => m.ProvisionAsync(It.IsAny<BenchmarkInstance>(), BenchmarkProblemCategory.TaskReview, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkWorkspaceHandle
            {
                InstanceId = "ncf-tr-ts-002",
                Category = BenchmarkProblemCategory.TaskReview,
                WorkspacePath = wks,
                BaseCommit = "3653bc0f",
                TaskMarkdownPath = "docs/tasks/ncf-tr-ts-002.md",
                TaskMarkdownInitialContent = taskSpec
            });
        workspaceMgr.Setup(m => m.GetModifiedFilesAsync(wks, "3653bc0f", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["docs/tasks/ncf-tr-ts-002.md"]);

        ForgeTask? capturedTask = null;
        var harness = new Mock<IAgentHarness>();
        harness.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                capturedTask = ctx.ForgeTask;
                var sink = InMemoryCrucibleConversationCoordinator.CurrentCapture!.TurnSink;
                // Legitimate scratch file under /tmp and docs/tasks/ edit must be ignored; reverted src edit is tracked
                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Write,
                    toolArguments: """{"path":"/tmp/scratch-check.ts","content":"x"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Edit,
                    toolArguments: """{"path":"/workspace/docs/tasks/ncf-tr-ts-002.md"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Edit,
                    toolArguments: """{"file_path":"packages/reactivity/src/effect.ts"}""", cancellationToken: ct);
                await sink.AddTurnAsync(1, StageTurnRole.Tool, toolName: CanonicalToolNames.Bash,
                    toolArguments: """{"command":"npm pack @vue/compiler-core"}""",
                    metadataJson: """{"CallId":"call-cheat"}""", cancellationToken: ct);
                return AgentRunResult.Completed(new AgentRunMetrics(100, 10, 10, 20), summary: "Ready");
            });

        var registry = new Mock<IHarnessRegistry>();
        registry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        BenchmarkAgentOutcome? capturedOutcome = null;
        var verifier = new Mock<IBenchmarkVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkWorkspaceHandle>(), It.IsAny<BenchmarkAgentOutcome>(), ExecutionMode.BareHost, true, null, It.IsAny<CancellationToken>()))
            .Callback<BenchmarkInstance, BenchmarkWorkspaceHandle, BenchmarkAgentOutcome, ExecutionMode, bool, BenchmarkJudgeConfig?, CancellationToken>((_, _, o, _, _, _, _) => capturedOutcome = o)
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0, ActualVerdict = "Ready" });

        var provenance = new Mock<IBenchmarkProvenanceProvider>();
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<ExecutionMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var report = await runner.RunAsync(new BenchmarkRunRequest
        {
            SuiteName = "tr-sync",
            DatasetPath = datasetPath,
            OutputDirectory = Path.Combine(_tempDir, "out-tr-sync"),
            Candidates = [new BenchmarkModelCandidate { CandidateId = "cand", ModelName = "m1" }],
            ExecutionMode = ExecutionMode.BareHost
        });

        Assert.NotNull(capturedTask);
        Assert.Equal(202, capturedTask!.Id);
        Assert.Equal(longTitle, capturedTask.Title);
        Assert.True(capturedTask.Title.Length > 80);

        Assert.NotNull(capturedOutcome);
        Assert.Equal(["packages/reactivity/src/effect.ts"], capturedOutcome!.RevertedOutOfScopeEditPaths);

        var result = Assert.Single(report.Results);
        Assert.Equal(BenchmarkInstanceOutcome.Passed, result.Outcome);
        Assert.Equal(1.0, result.DeterministicScore);
        Assert.Equal(1, result.Telemetry.AntiCheatAlertCount);
        Assert.Equal([BenchmarkAntiCheatAuditor.PackageArchiveDownload], result.Telemetry.AntiCheatSignals);
        Assert.Equal(1, Assert.Single(report.Summaries).TotalAntiCheatAlerts);
    }

    [Fact]
    public void ResolveEphemeralTaskMetadata_FallsBackSafelyAndLeavesImplementationAndCodeReviewUnchanged()
    {
        var longProblem = new string('x', 120);
        var instance = new BenchmarkInstance
        {
            InstanceId = "meta-1",
            Repo = "o/r",
            BaseCommit = "c1",
            ProblemStatement = longProblem
        };

        var malformedHandle = new BenchmarkWorkspaceHandle
        {
            InstanceId = "meta-1",
            Category = BenchmarkProblemCategory.TaskReview,
            WorkspacePath = _tempDir,
            BaseCommit = "c1",
            TaskMarkdownInitialContent = "---\nid: not-a-number\n---\nBody"
        };
        var (badId, badTitle) = CrucibleBenchmarkRunner.ResolveEphemeralTaskMetadata(instance, BenchmarkProblemCategory.TaskReview, malformedHandle);
        Assert.Equal(0, badId);
        Assert.Equal(80, badTitle.Length);

        var missingHandle = malformedHandle with { TaskMarkdownInitialContent = null };
        var (missingId, missingTitle) = CrucibleBenchmarkRunner.ResolveEphemeralTaskMetadata(instance, BenchmarkProblemCategory.TaskReview, missingHandle);
        Assert.Equal(0, missingId);
        Assert.Equal(80, missingTitle.Length);

        var validHandle = malformedHandle with
        {
            TaskMarkdownInitialContent = "---\nid: 301\ntitle: \"" + new string('t', 100) + "\"\nstatus: Open\n---\nBody"
        };
        var (implId, implTitle) = CrucibleBenchmarkRunner.ResolveEphemeralTaskMetadata(instance, BenchmarkProblemCategory.Implementation, validHandle);
        Assert.Equal(0, implId);
        Assert.Equal(80, implTitle.Length);

        var (crId, crTitle) = CrucibleBenchmarkRunner.ResolveEphemeralTaskMetadata(instance, BenchmarkProblemCategory.CodeReview, validHandle);
        Assert.Equal(0, crId);
        Assert.Equal(80, crTitle.Length);
    }

    [Fact]
    public void ExtractOutOfScopeTaskReviewMutationPaths_CanonicalizesTraversalAndWslPaths()
    {
        var turns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Write,
                ToolArguments = """{"path":"/tmp/../workspace/src/Foo.cs","content":"x"}"""
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Edit,
                ToolArguments = """{"path":"docs/tasks/../../src/Bar.cs"}"""
            },
            new()
            {
                TurnIndex = 2,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Edit,
                ToolArguments = """{"file_path":"./src/./Baz.cs"}"""
            },
            new()
            {
                TurnIndex = 3,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Edit,
                ToolArguments = """{"path":"./docs/tasks/../tasks/101.md"}"""
            },
            new()
            {
                TurnIndex = 4,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Write,
                ToolArguments = """{"path":"/tmp/scratch.txt","content":"ok"}"""
            }
        };

        var extracted = CrucibleBenchmarkRunner.ExtractOutOfScopeTaskReviewMutationPaths(turns, _tempDir);
        Assert.Equal(["src/Bar.cs", "src/Baz.cs", "src/Foo.cs"], extracted);

        const string wslWorkspace = @"\\wsl.localhost\Ubuntu-24.04\home\user\wks";
        var wslTurns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Edit,
                ToolArguments = """{"path":"/home/user/wks/docs/tasks/101.md"}"""
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Edit,
                ToolArguments = """{"path":"/home/user/wks/src/WslTarget.cs"}"""
            }
        };

        var wslExtracted = CrucibleBenchmarkRunner.ExtractOutOfScopeTaskReviewMutationPaths(wslTurns, wslWorkspace);
        Assert.Equal(["src/WslTarget.cs"], wslExtracted);
    }

    [Fact]
    public void ToolCallTelemetryBreakdown_NullAntiCheatSignalsAfterJsonDeserialization_HandlesEqualsAndGetHashCodeSafely()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        var deserialized = JsonSerializer.Deserialize<ToolCallTelemetryBreakdown>(
            """{"total_tool_calls":2,"anti_cheat_alert_count":0,"anti_cheat_signals":null}""",
            options);

        Assert.NotNull(deserialized);
        var expected = new ToolCallTelemetryBreakdown { TotalToolCalls = 2 };

        Assert.Equal(expected, deserialized);
        Assert.Equal(expected.GetHashCode(), deserialized!.GetHashCode());
    }
}
