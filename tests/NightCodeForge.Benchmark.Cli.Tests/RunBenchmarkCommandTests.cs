using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Commands;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Benchmark.Cli.Tests;

public sealed class RunBenchmarkCommandTests : IDisposable
{
    private readonly string _tempDir;

    public RunBenchmarkCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-cmd-{Guid.NewGuid():N}");
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
    public async Task ExecuteAsync_MapsRequestOptionsAndWritesSummaryMarkdown()
    {
        var outDir = Path.Combine(_tempDir, "out");
        var resumeDir = Path.Combine(_tempDir, "prev");
        var datasetPath = Path.Combine(_tempDir, "review.jsonl");

        BenchmarkRunRequest? capturedRequest = null;
        var runnerMock = new Mock<ICrucibleBenchmarkRunner>();
        runnerMock
            .Setup(r => r.RunAsync(It.IsAny<BenchmarkRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<BenchmarkRunRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync((BenchmarkRunRequest req, CancellationToken _) => new BenchmarkRunReport
            {
                SuiteName = req.SuiteName,
                DatasetPath = req.DatasetPath,
                Provenance = new BenchmarkRunProvenance(
                    NcfVersion: "0.1.0",
                    GitCommit: "1234567",
                    GitBranch: "main",
                    RunTimestampUtc: DateTimeOffset.UtcNow,
                    ProviderType: req.ProviderType,
                    Endpoint: req.Endpoint,
                    EnableStackSkills: true,
                    OptimizationFlags: null),
                Summaries =
                [
                    new BenchmarkCandidateSummary
                    {
                        CandidateId = "qwen3_8b",
                        DisplayName = "qwen3:8b",
                        ModelName = "qwen3:8b",
                        TotalInstances = 1,
                        PassedInstances = 1,
                        PassRate = 1.0,
                        PatchOrVerdictRate = 1.0,
                        AverageDeterministicScore = 1.0,
                        AverageJudgeScore = 0.9,
                        AverageDurationMs = 2000
                    }
                ],
                Results = Array.Empty<BenchmarkInstanceResult>()
            });

        var options = new RunCommandOptions
        {
            DatasetPath = datasetPath,
            ManifestPath = Path.Combine(_tempDir, "smoke.manifest.json"),
            SuiteName = "code-review-suite",
            Provider = LlmProviderType.OllamaRouter,
            Endpoint = "http://localhost:11435",
            Category = BenchmarkProblemCategory.CodeReview,
            Limit = 3,
            VerifyWithTests = false,
            Concurrency = 2,
            ResumeFromDir = resumeDir,
            JudgeModel = "gpt-4o-mini",
            JudgeProvider = LlmProviderType.OpenAiCompatible,
            JudgeEndpoint = "https://api.openai.com/v1",
            OutputDirectory = outDir
        };
        options.FilterInstanceIds.Add("rev-1");

        var candidates = new[]
        {
            new BenchmarkModelCandidate
            {
                CandidateId = "qwen3_8b",
                DisplayName = "qwen3:8b",
                ModelName = "qwen3:8b"
            }
        };

        var lines = new List<string>();
        var command = new RunBenchmarkCommand(runnerMock.Object);
        var exitCode = await command.ExecuteAsync(options, candidates, lines.Add, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal("code-review-suite", capturedRequest.SuiteName);
        Assert.Equal(Path.GetFullPath(datasetPath), capturedRequest.DatasetPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempDir, "smoke.manifest.json")), capturedRequest.ManifestPath);
        Assert.Equal(Path.GetFullPath(outDir), capturedRequest.OutputDirectory);
        Assert.Equal(BenchmarkProblemCategory.CodeReview, capturedRequest.CategoryOverride);
        Assert.Equal(["rev-1"], capturedRequest.InstanceIdFilter);
        Assert.Equal(3, capturedRequest.MaxInstances);
        Assert.False(capturedRequest.VerifyWithTests);
        Assert.Equal(2, capturedRequest.Concurrency);
        Assert.Equal(Path.GetFullPath(resumeDir), capturedRequest.ResumeFromDir);
        Assert.Equal(LlmProviderType.OllamaRouter, capturedRequest.ProviderType);
        Assert.Equal("http://localhost:11435", capturedRequest.Endpoint);
        Assert.NotNull(capturedRequest.JudgeConfig);
        Assert.Equal("gpt-4o-mini", capturedRequest.JudgeConfig.ModelName);
        Assert.Equal(LlmProviderType.OpenAiCompatible, capturedRequest.JudgeConfig.ProviderType);
        Assert.Equal("https://api.openai.com/v1", capturedRequest.JudgeConfig.Endpoint);

        var summaryPath = Path.Combine(outDir, BenchmarkPaths.SummaryFileName);
        Assert.True(File.Exists(summaryPath));
        var summaryText = await File.ReadAllTextAsync(summaryPath);
        Assert.Contains("# Benchmark Report: code-review-suite", summaryText);
        Assert.Contains("qwen3:8b", summaryText);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRunnerThrows_DoesNotCreateOutputDirectory()
    {
        var outDir = Path.Combine(_tempDir, "failed-out");
        var runnerMock = new Mock<ICrucibleBenchmarkRunner>();
        runnerMock
            .Setup(r => r.RunAsync(It.IsAny<BenchmarkRunRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BenchmarkDatasetException("Dataset missing", "missing.jsonl"));

        var options = new RunCommandOptions
        {
            DatasetPath = Path.Combine(_tempDir, "missing.jsonl"),
            ModelsCsv = "qwen3:8b",
            OutputDirectory = outDir
        };
        var candidates = new[]
        {
            new BenchmarkModelCandidate { CandidateId = "qwen3_8b", DisplayName = "qwen3:8b", ModelName = "qwen3:8b" }
        };

        var command = new RunBenchmarkCommand(runnerMock.Object);
        await Assert.ThrowsAsync<BenchmarkDatasetException>(() =>
            command.ExecuteAsync(options, candidates, _ => { }, CancellationToken.None));

        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void ResolveApiKeyFromEnvironment_FollowsPrecedenceOrder()
    {
        Assert.Equal("bench-key", RunBenchmarkCommand.ResolveApiKeyFromEnvironment(name => name switch
        {
            BenchmarkCliConstants.BenchmarkApiKeyEnvVar => "  bench-key  ",
            BenchmarkCliConstants.OpenAiApiKeyEnvVar => "openai-key",
            _ => null
        }));

        Assert.Equal("openai-key", RunBenchmarkCommand.ResolveApiKeyFromEnvironment(name => name switch
        {
            BenchmarkCliConstants.BenchmarkApiKeyEnvVar => "   ",
            BenchmarkCliConstants.OpenAiApiKeyEnvVar => " openai-key ",
            _ => null
        }));

        Assert.Null(RunBenchmarkCommand.ResolveApiKeyFromEnvironment(_ => null));
    }

    [Fact]
    public async Task CrucibleRunner_WithResumeFromDir_SkipsAlreadyCompletedPairsAndWritesSummary()
    {
        var resumeDir = Path.Combine(_tempDir, "resume-dir");
        var outDir = Path.Combine(_tempDir, "resumed-out");
        Directory.CreateDirectory(resumeDir);

        var priorReport = new BenchmarkRunReport
        {
            SuiteName = "resume-suite",
            DatasetPath = "ds.jsonl",
            Provenance = new BenchmarkRunProvenance(
                NcfVersion: "0.1.0",
                GitCommit: "abc1234",
                GitBranch: "main",
                RunTimestampUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
                ProviderType: LlmProviderType.Ollama,
                Endpoint: "http://localhost:11434",
                EnableStackSkills: true,
                OptimizationFlags: null),
            Summaries = Array.Empty<BenchmarkCandidateSummary>(),
            Results =
            [
                new BenchmarkInstanceResult
                {
                    InstanceId = "inst-1",
                    CandidateId = "cand-1",
                    ModelName = "qwen3:8b",
                    Category = BenchmarkProblemCategory.Implementation,
                    Language = BenchmarkLanguages.CSharp,
                    Outcome = BenchmarkInstanceOutcome.Passed,
                    DurationMs = 1500,
                    ModelPatch = "diff --git a/a.cs b/a.cs",
                    DeterministicScore = 1.0
                },
                new BenchmarkInstanceResult
                {
                    InstanceId = "inst-2",
                    CandidateId = "cand-1",
                    ModelName = "qwen3:8b",
                    Category = BenchmarkProblemCategory.Implementation,
                    Language = BenchmarkLanguages.CSharp,
                    Outcome = BenchmarkInstanceOutcome.Error,
                    DurationMs = 500,
                    ErrorMessage = "Transient error that should retry"
                }
            ]
        };

        var json = JsonSerializer.Serialize(priorReport, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        await File.WriteAllTextAsync(Path.Combine(resumeDir, BenchmarkPaths.ResultsFileName), json);

        var datasetInstances = new[]
        {
            new BenchmarkInstance
            {
                InstanceId = "inst-1",
                Category = BenchmarkProblemCategory.Implementation,
                Language = BenchmarkLanguages.CSharp,
                Repo = "owner/repo",
                BaseCommit = "deadbeef",
                ProblemStatement = "Already passed"
            },
            new BenchmarkInstance
            {
                InstanceId = "inst-2",
                Category = BenchmarkProblemCategory.Implementation,
                Language = BenchmarkLanguages.CSharp,
                Repo = "owner/repo",
                BaseCommit = "deadbeef",
                ProblemStatement = "Needs re-evaluation"
            }
        };

        var datasetLoader = new Mock<IBenchmarkDatasetLoader>();
        datasetLoader
            .Setup(l => l.LoadAsync(It.IsAny<string>(), It.IsAny<BenchmarkProblemCategory?>(), It.IsAny<string?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(datasetInstances);

        var evaluatedInstances = new List<string>();
        var workspaceManager = new Mock<IBenchmarkWorkspaceManager>();
        workspaceManager
            .Setup(w => w.ProvisionAsync(It.IsAny<BenchmarkInstance>(), It.IsAny<BenchmarkProblemCategory>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BenchmarkInstance inst, BenchmarkProblemCategory cat, string? _, CancellationToken _) =>
            {
                evaluatedInstances.Add(inst.InstanceId);
                var wsPath = Path.Combine(_tempDir, $"ws-{inst.InstanceId}");
                Directory.CreateDirectory(wsPath);
                return new BenchmarkWorkspaceHandle
                {
                    InstanceId = inst.InstanceId,
                    Category = cat,
                    WorkspacePath = wsPath,
                    BaseCommit = inst.BaseCommit
                };
            });
        workspaceManager
            .Setup(w => w.ExtractPatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("diff --git a/b.cs b/b.cs");

        var harness = new Mock<IAgentHarness>();
        harness.SetupGet(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);
        harness
            .Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentRunResult.Completed(new AgentRunMetrics(DurationMs: 1000, PromptTokens: 100, CompletionTokens: 50, TotalTokens: 150)));

        var harnessRegistry = new Mock<IHarnessRegistry>();
        harnessRegistry.Setup(r => r.GetHarness(StandardHarnessIds.Crucible)).Returns(harness.Object);

        var verifier = new Mock<IBenchmarkVerifier>();
        verifier
            .Setup(v => v.VerifyAsync(
                It.IsAny<BenchmarkInstance>(),
                It.IsAny<BenchmarkWorkspaceHandle>(),
                It.IsAny<BenchmarkAgentOutcome>(),
                It.IsAny<ExecutionMode>(),
                It.IsAny<bool>(),
                It.IsAny<BenchmarkJudgeConfig?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BenchmarkVerificationResult { Passed = true, DeterministicScore = 1.0 });

        var provenanceProvider = new Mock<IBenchmarkProvenanceProvider>();
        provenanceProvider
            .Setup(p => p.CaptureAsync(
                It.IsAny<LlmProviderType>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<LocalOptimizationFlags?>(),
                It.IsAny<ExecutionMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(priorReport.Provenance);

        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        var runner = new CrucibleBenchmarkRunner(
            datasetLoader.Object,
            workspaceManager.Object,
            harnessRegistry.Object,
            verifier.Object,
            provenanceProvider.Object,
            envProvider.Object,
            Mock.Of<IBenchmarkToolchainPreflightValidator>(),
            NullLogger<CrucibleBenchmarkRunner>.Instance);

        var options = new RunCommandOptions
        {
            DatasetPath = Path.Combine(_tempDir, "ds.jsonl"),
            SuiteName = "resume-suite",
            ModelsCsv = "qwen3:8b",
            ResumeFromDir = resumeDir,
            OutputDirectory = outDir,
            VerifyWithTests = false
        };
        var candidates = new[]
        {
            new BenchmarkModelCandidate
            {
                CandidateId = "cand-1",
                DisplayName = "qwen3:8b",
                ModelName = "qwen3:8b"
            }
        };

        var logs = new List<string>();
        var command = new RunBenchmarkCommand(runner);
        var exitCode = await command.ExecuteAsync(options, candidates, logs.Add, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["inst-2"], evaluatedInstances);
        Assert.Contains(logs, l => l.Contains("[SKIP]", StringComparison.Ordinal) && l.Contains("inst-1", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(outDir, BenchmarkPaths.ResultsFileName)));
        Assert.True(File.Exists(Path.Combine(outDir, BenchmarkPaths.SummaryFileName)));

        var predictionsFile = Path.Combine(outDir, "cand-1", BenchmarkPaths.PredictionsFileName);
        Assert.True(File.Exists(predictionsFile));
        var predictionLines = await File.ReadAllLinesAsync(predictionsFile);
        Assert.Equal(2, predictionLines.Length);
    }

    [Fact]
    public async Task ReportBenchmarkCommand_SingleAndComparisonModes_FormatsReports()
    {
        var dir1 = Path.Combine(_tempDir, "rep-1");
        var dir2 = Path.Combine(_tempDir, "rep-2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var report1 = new BenchmarkRunReport
        {
            SuiteName = "suite",
            DatasetPath = "ds.jsonl",
            Provenance = new BenchmarkRunProvenance("0.1.0", "1111111", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null),
            Summaries =
            [
                new BenchmarkCandidateSummary
                {
                    CandidateId = "qwen",
                    DisplayName = "Qwen",
                    ModelName = "qwen3:8b",
                    TotalInstances = 2,
                    PassedInstances = 2,
                    PassRate = 1.0,
                    PatchOrVerdictRate = 1.0,
                    AverageDeterministicScore = 1.0
                }
            ]
        };

        var report2 = new BenchmarkRunReport
        {
            SuiteName = "suite",
            DatasetPath = "ds.jsonl",
            Provenance = new BenchmarkRunProvenance("0.2.0", "2222222", "feat/x", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null),
            Summaries =
            [
                new BenchmarkCandidateSummary
                {
                    CandidateId = "qwen",
                    DisplayName = "Qwen",
                    ModelName = "qwen3:8b",
                    TotalInstances = 2,
                    PassedInstances = 1,
                    FailedInstances = 1,
                    PassRate = 0.5,
                    PatchOrVerdictRate = 1.0,
                    AverageDeterministicScore = 0.5
                }
            ]
        };

        var serializerOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        await File.WriteAllTextAsync(Path.Combine(dir1, BenchmarkPaths.ResultsFileName), JsonSerializer.Serialize(report1, serializerOpts));
        await File.WriteAllTextAsync(Path.Combine(dir2, BenchmarkPaths.ResultsFileName), JsonSerializer.Serialize(report2, serializerOpts));

        var command = new ReportBenchmarkCommand();

        var singleLines = new List<string>();
        var singleOpts = new ReportCommandOptions { Format = ReportOutputFormat.Ascii };
        singleOpts.ResultDirectories.Add(dir1);
        Assert.Equal(0, await command.ExecuteAsync(singleOpts, singleLines.Add));
        var singleOutput = string.Join(Environment.NewLine, singleLines);
        Assert.Contains("Overall Model Leaderboard", singleOutput);
        Assert.Contains("100.0% (2/2)", singleOutput);

        var compareLines = new List<string>();
        var compareOpts = new ReportCommandOptions { Format = ReportOutputFormat.Markdown, BaselineDirectory = dir1 };
        compareOpts.ResultDirectories.Add(dir2);
        Assert.Equal(0, await command.ExecuteAsync(compareOpts, compareLines.Add));
        var compareOutput = string.Join(Environment.NewLine, compareLines);
        Assert.Contains("Cross-Version Benchmark Comparison", compareOutput);
        Assert.Contains("BASELINE", compareOutput);
        Assert.Contains("REGRESSION", compareOutput);
        Assert.Contains("-50.0%", compareOutput);
    }

    [Fact]
    public async Task ReportBenchmarkCommand_BaselineWithTrailingSeparatorMatchingResultDir_DoesNotDuplicateRun()
    {
        var dir1 = Path.Combine(_tempDir, "rep-1");
        var dir2 = Path.Combine(_tempDir, "rep-2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var report1 = new BenchmarkRunReport
        {
            SuiteName = "suite",
            DatasetPath = "ds.jsonl",
            Provenance = new BenchmarkRunProvenance("0.1.0", "1111111", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null),
            Summaries = [new BenchmarkCandidateSummary { CandidateId = "qwen", DisplayName = "Qwen", ModelName = "qwen3:8b", TotalInstances = 1, PassedInstances = 1, PassRate = 1.0 }]
        };
        var report2 = new BenchmarkRunReport
        {
            SuiteName = "suite",
            DatasetPath = "ds.jsonl",
            Provenance = new BenchmarkRunProvenance("0.2.0", "2222222", "feat", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null),
            Summaries = [new BenchmarkCandidateSummary { CandidateId = "qwen", DisplayName = "Qwen", ModelName = "qwen3:8b", TotalInstances = 1, PassedInstances = 1, PassRate = 1.0 }]
        };

        var serializerOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        await File.WriteAllTextAsync(Path.Combine(dir1, BenchmarkPaths.ResultsFileName), JsonSerializer.Serialize(report1, serializerOpts));
        await File.WriteAllTextAsync(Path.Combine(dir2, BenchmarkPaths.ResultsFileName), JsonSerializer.Serialize(report2, serializerOpts));

        var command = new ReportBenchmarkCommand();
        var lines = new List<string>();
        var opts = new ReportCommandOptions
        {
            Format = ReportOutputFormat.Json,
            BaselineDirectory = dir1 + Path.DirectorySeparatorChar
        };
        opts.ResultDirectories.Add(dir1);
        opts.ResultDirectories.Add(dir2);

        Assert.Equal(0, await command.ExecuteAsync(opts, lines.Add));

        using var doc = JsonDocument.Parse(string.Join(Environment.NewLine, lines));
        Assert.Equal(2, doc.RootElement.GetProperty("runs").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("comparisons").GetArrayLength());
    }
}
