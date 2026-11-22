using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
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
        provenance.Setup(p => p.CaptureAsync(LlmProviderType.Ollama, "http://localhost:11434", true, It.IsAny<LocalOptimizationFlags?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "deadbeef", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var envProvider = new Mock<IExecutionEnvironmentProvider>();

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            envProvider.Object,
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
                if (ctx.ForgeTask.Title.StartsWith("i-cancelled:", StringComparison.Ordinal))
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
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            envProvider.Object,
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
        provenance.Setup(p => p.CaptureAsync(It.IsAny<LlmProviderType>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<LocalOptimizationFlags?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkRunProvenance("0.1.0", "sha", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null));

        var runner = new CrucibleBenchmarkRunner(
            new BenchmarkDatasetLoader(),
            workspaceMgr.Object,
            registry.Object,
            verifier.Object,
            provenance.Object,
            new Mock<IExecutionEnvironmentProvider>().Object,
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
}
