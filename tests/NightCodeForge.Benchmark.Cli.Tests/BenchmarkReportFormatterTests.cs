using System.Text.Json;
using System.Text.Json.Serialization;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Reporting;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Tests;

public sealed class BenchmarkReportFormatterTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkReportFormatterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-rep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(ReportOutputFormat.Markdown)]
    [InlineData(ReportOutputFormat.Ascii)]
    [InlineData(ReportOutputFormat.Json)]
    public void FormatSingleRun_IncludesAllMetricsAndLanguageBreakdowns(ReportOutputFormat format)
    {
        var report = BuildSampleReport(
            branch: "main",
            commit: "1111111abcdef",
            version: "0.1.0",
            summaries:
            [
                BuildSummary("qwen-3.8", "Qwen 3.8", passRate: 0.75, passed: 3, total: 4, patchRate: 1.0, judgeScore: 0.86, avgDurationMs: 12500, avgTurns: 8.5, avgTokens: 4200, tps: 44.2, editFailureRate: 0.05, csnav: 12, condenser: 3, repairs: 2),
                BuildSummary("llama-3.1", "Llama 3.1", passRate: 0.50, passed: 2, total: 4, patchRate: 0.75, judgeScore: 0.72, avgDurationMs: 18000, avgTurns: 11.0, avgTokens: 5100, tps: 31.0, editFailureRate: 0.12, csnav: 4, condenser: 1, repairs: 5)
            ]);

        var formatted = BenchmarkReportFormatter.FormatSingleRun(report, format);

        Assert.Contains("Qwen 3.8", formatted);
        Assert.Contains("Llama 3.1", formatted);
        Assert.Contains("csharp", formatted);
        Assert.Contains("typescript", formatted);
        Assert.Contains("python", formatted);
        Assert.Contains("rust", formatted);

        if (format != ReportOutputFormat.Json)
        {
            Assert.Contains("Pass@1 / Accuracy %", formatted);
            Assert.Contains("Verdict / Patch %", formatted);
            Assert.Contains("Judge Score", formatted);
            Assert.Contains("Avg Duration", formatted);
            Assert.Contains("Avg Turns", formatted);
            Assert.Contains("Avg Tokens", formatted);
            Assert.Contains("Tokens/s", formatted);
            Assert.Contains("Edit Failure %", formatted);
            Assert.Contains("csnav Calls", formatted);
            Assert.Contains("codecondenser Calls", formatted);
            Assert.Contains("Dispatcher Repairs", formatted);
            Assert.Contains("75.0% (3/4)", formatted);
            Assert.Contains("12.5s", formatted);
            Assert.Contains("4,200", formatted);
            Assert.Contains("main @ 1111111", formatted);
        }
        else
        {
            using var doc = JsonDocument.Parse(formatted);
            Assert.Equal("single", doc.RootElement.GetProperty("mode").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("summaries").GetArrayLength());
            Assert.Equal(4, doc.RootElement.GetProperty("languages").GetArrayLength());
        }
    }

    [Fact]
    public async Task FormatComparison_MultiRunWithBaseline_DetectsRegressionsAndImprovements()
    {
        var dirBase = WriteReportDir("run-base", BuildSampleReport(
            branch: "main",
            commit: "aaaa11112222",
            version: "0.1.0",
            summaries:
            [
                BuildSummary("qwen", "Qwen 3.8", passRate: 0.75, passed: 3, total: 4, patchRate: 1.0, judgeScore: 0.80, avgDurationMs: 10000, avgTurns: 8.0, avgTokens: 4000, tps: 40.0, editFailureRate: 0.05, csnav: 10, condenser: 2, repairs: 1)
            ]));

        var dirImproved = WriteReportDir("run-improved", BuildSampleReport(
            branch: "feat/prompts",
            commit: "bbbb33334444",
            version: "0.1.1",
            summaries:
            [
                BuildSummary("qwen", "Qwen 3.8", passRate: 1.00, passed: 4, total: 4, patchRate: 1.0, judgeScore: 0.92, avgDurationMs: 9000, avgTurns: 7.0, avgTokens: 3500, tps: 45.0, editFailureRate: 0.00, csnav: 14, condenser: 4, repairs: 0)
            ]));

        var dirRegressed = WriteReportDir("run-regressed", BuildSampleReport(
            branch: "exp/broken",
            commit: "cccc55556666",
            version: "0.1.2",
            summaries:
            [
                BuildSummary("qwen", "Qwen 3.8", passRate: 0.50, passed: 2, total: 4, patchRate: 0.75, judgeScore: 0.65, avgDurationMs: 14500, avgTurns: 12.0, avgTokens: 6000, tps: 30.0, editFailureRate: 0.25, csnav: 6, condenser: 0, repairs: 4)
            ]));

        var runBase = await BenchmarkReportLoader.LoadAsync(dirBase);
        var runImproved = await BenchmarkReportLoader.LoadAsync(dirImproved);
        var runRegressed = await BenchmarkReportLoader.LoadAsync(dirRegressed);

        var markdown = BenchmarkReportFormatter.FormatComparison(
            [runBase, runImproved, runRegressed],
            runBase,
            ReportOutputFormat.Markdown);

        Assert.Contains("main @ aaaa111 (v0.1.0)", markdown);
        Assert.Contains("feat/prompts @ bbbb333 (v0.1.1)", markdown);
        Assert.Contains("exp/broken @ cccc555 (v0.1.2)", markdown);
        Assert.Contains("BASELINE", markdown);
        Assert.Contains("IMPROVED", markdown);
        Assert.Contains("REGRESSION", markdown);
        Assert.Contains("+25.0%", markdown);
        Assert.Contains("-25.0%", markdown);
        Assert.Contains("+4.5s", markdown);
        Assert.Contains("-1.0s", markdown);

        var json = BenchmarkReportFormatter.FormatComparison(
            [runBase, runImproved, runRegressed],
            runBase,
            ReportOutputFormat.Json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("comparison", doc.RootElement.GetProperty("mode").GetString());
        var comparisons = doc.RootElement.GetProperty("comparisons");
        Assert.Equal(3, comparisons.GetArrayLength());
        Assert.False(comparisons[0].GetProperty("is_regression").GetBoolean());
        Assert.False(comparisons[1].GetProperty("is_regression").GetBoolean());
        Assert.True(comparisons[2].GetProperty("is_regression").GetBoolean());
        Assert.Equal(-0.25, comparisons[2].GetProperty("deltas").GetProperty("pass_rate").GetDouble(), 4);
    }

    [Fact]
    public void FormatComparison_SharedModelNameDifferentCandidates_DoesNotDuplicateBaselineAndMarksNew()
    {
        var baseSummaryLow = BuildSummary("qwen-low", "Qwen Low", passRate: 0.50, passed: 2, total: 4, patchRate: 1.0, judgeScore: 0.70, avgDurationMs: 5000, avgTurns: 5.0, avgTokens: 1000, tps: 20.0, editFailureRate: 0.0, csnav: 1, condenser: 0, repairs: 0) with
        {
            ModelName = "qwen3.8:latest"
        };
        var candidateLow = baseSummaryLow with { PassRate = 0.75, PassedInstances = 3 };
        var candidateXhigh = BuildSummary("qwen-xhigh", "Qwen XHigh", passRate: 1.0, passed: 4, total: 4, patchRate: 1.0, judgeScore: 0.90, avgDurationMs: 8000, avgTurns: 8.0, avgTokens: 2000, tps: 25.0, editFailureRate: 0.0, csnav: 3, condenser: 1, repairs: 0) with
        {
            ModelName = "qwen3.8:latest"
        };

        var baseRun = new LoadedBenchmarkRun(
            Path.Combine(_tempDir, "base"),
            Path.Combine(_tempDir, "base", BenchmarkPaths.ResultsFileName),
            BuildSampleReport("main", "1111111", "0.1.0", [baseSummaryLow]),
            "main @ 1111111 (v0.1.0)");
        var candRun = new LoadedBenchmarkRun(
            Path.Combine(_tempDir, "cand"),
            Path.Combine(_tempDir, "cand", BenchmarkPaths.ResultsFileName),
            BuildSampleReport("feat", "2222222", "0.1.1", [candidateLow, candidateXhigh]),
            "feat @ 2222222 (v0.1.1)");

        var json = BenchmarkReportFormatter.FormatComparison([baseRun, candRun], baseRun, ReportOutputFormat.Json);
        using var doc = JsonDocument.Parse(json);
        var comparisons = doc.RootElement.GetProperty("comparisons").EnumerateArray().ToList();

        Assert.Equal(3, comparisons.Count);
        var xhighEntry = Assert.Single(comparisons, e => e.GetProperty("candidate_id").GetString() == "qwen-xhigh");
        Assert.Equal("NEW", xhighEntry.GetProperty("status").GetString());
        Assert.True(!xhighEntry.TryGetProperty("deltas", out var deltasProp) || deltasProp.ValueKind == JsonValueKind.Null);
        Assert.Single(comparisons, e => e.GetProperty("is_baseline").GetBoolean());
    }

    [Theory]
    [InlineData(ReportOutputFormat.Markdown)]
    [InlineData(ReportOutputFormat.Ascii)]
    [InlineData(ReportOutputFormat.Json)]
    public void FormatSingleRun_UnnormalizedLanguageAliases_NormalizesInOutput(ReportOutputFormat format)
    {
        var summary = new BenchmarkCandidateSummary
        {
            CandidateId = "qwen",
            DisplayName = "Qwen",
            ModelName = "qwen3:8b",
            TotalInstances = 3,
            PassedInstances = 3,
            PassRate = 1.0,
            ByLanguage =
            [
                new BenchmarkLanguageBreakdown { Language = "c#", TotalInstances = 1, PassedInstances = 1, PassRate = 1.0, AverageDeterministicScore = 1.0, AverageDurationMs = 1000 },
                new BenchmarkLanguageBreakdown { Language = "py", TotalInstances = 1, PassedInstances = 1, PassRate = 1.0, AverageDeterministicScore = 1.0, AverageDurationMs = 1000 },
                new BenchmarkLanguageBreakdown { Language = "ts", TotalInstances = 1, PassedInstances = 1, PassRate = 1.0, AverageDeterministicScore = 1.0, AverageDurationMs = 1000 }
            ]
        };

        var report = BuildSampleReport("main", "1234567", "0.1.0", [summary]);
        var formatted = BenchmarkReportFormatter.FormatSingleRun(report, format);

        Assert.Contains("csharp", formatted);
        Assert.Contains("python", formatted);
        Assert.Contains("typescript", formatted);

        if (format != ReportOutputFormat.Json)
        {
            Assert.DoesNotContain("| c# |", formatted);
            Assert.DoesNotContain("| py |", formatted);
            Assert.DoesNotContain("| ts |", formatted);
        }
        else
        {
            using var doc = JsonDocument.Parse(formatted);
            var langs = doc.RootElement.GetProperty("languages");
            Assert.Equal(3, langs.GetArrayLength());
            Assert.All(langs.EnumerateArray(), l =>
                Assert.NotEqual(JsonValueKind.Null, l.GetProperty("models")[0].GetProperty("breakdown").ValueKind));
        }
    }

    [Fact]
    public void FormatComparison_TinyNegativeDeltas_NeverEmitNegativeZeroOrPlusMinus()
    {
        var baseSummary = BuildSummary("qwen", "Qwen", passRate: 0.500001, passed: 1, total: 2, patchRate: 0.500001, judgeScore: 0.8001, avgDurationMs: 1000.4, avgTurns: 5.01, avgTokens: 100.2, tps: 40.01, editFailureRate: 0.050001, csnav: 1, condenser: 1, repairs: 1);
        var currSummary = BuildSummary("qwen", "Qwen", passRate: 0.500000, passed: 1, total: 2, patchRate: 0.500000, judgeScore: 0.8000, avgDurationMs: 1000.0, avgTurns: 5.00, avgTokens: 100.0, tps: 40.00, editFailureRate: 0.050000, csnav: 1, condenser: 1, repairs: 1);

        var baseRun = new LoadedBenchmarkRun("/tmp/base", "/tmp/base/results.json", BuildSampleReport("main", "1111111", "0.1.0", [baseSummary]), "base");
        var currRun = new LoadedBenchmarkRun("/tmp/curr", "/tmp/curr/results.json", BuildSampleReport("main", "2222222", "0.1.0", [currSummary]), "curr");

        var markdown = BenchmarkReportFormatter.FormatComparison([baseRun, currRun], baseRun, ReportOutputFormat.Markdown);

        Assert.DoesNotContain("+-", markdown);
        Assert.DoesNotContain("(-0.0", markdown);
        Assert.DoesNotContain("(-0)", markdown);
        Assert.Contains("(+0.0%)", markdown);
        Assert.Contains("(+0.00)", markdown);
        Assert.Contains("(+0.0s)", markdown);
        Assert.Contains("(+0)", markdown);
    }

    [Theory]
    [InlineData(ReportOutputFormat.Markdown)]
    [InlineData(ReportOutputFormat.Ascii)]
    public void FormatSingleRun_MultilineAndPipeDisplayName_RendersSingleLineTableRow(ReportOutputFormat format)
    {
        var summary = BuildSummary("weird", "Model | Line1\nLine2\r\nLine3", 1.0, 1, 1, 1.0, 0.9, 1000, 2, 100, 10, 0, 0, 0, 0);
        var report = BuildSampleReport("main", "1234567", "0.1.0", [summary]);

        var formatted = BenchmarkReportFormatter.FormatSingleRun(report, format);

        Assert.DoesNotContain("Line1\nLine2", formatted);
        Assert.Contains("Model", formatted);
        Assert.Contains("Line1 Line2 Line3", formatted);
        if (format == ReportOutputFormat.Markdown)
        {
            Assert.Contains(@"Model \| Line1 Line2 Line3", formatted);
        }
    }

    [Fact]
    public void FormatComparison_BaselineOmittedFromRunsList_StillIncludesBaselineInOutput()
    {
        var baseSummary = BuildSummary("qwen", "Qwen", 0.5, 1, 2, 1.0, 0.7, 5000, 5, 1000, 20, 0, 1, 0, 0);
        var currSummary = BuildSummary("qwen", "Qwen", 1.0, 2, 2, 1.0, 0.9, 4000, 4, 800, 25, 0, 2, 0, 0);

        var baseRun = new LoadedBenchmarkRun(Path.Combine(_tempDir, "base"), Path.Combine(_tempDir, "base", BenchmarkPaths.ResultsFileName), BuildSampleReport("main", "1111111", "0.1.0", [baseSummary]), "main @ 1111111 (v0.1.0)");
        var currRun = new LoadedBenchmarkRun(Path.Combine(_tempDir, "curr"), Path.Combine(_tempDir, "curr", BenchmarkPaths.ResultsFileName), BuildSampleReport("feat", "2222222", "0.1.1", [currSummary]), "feat @ 2222222 (v0.1.1)");

        var markdown = BenchmarkReportFormatter.FormatComparison([currRun], baseRun, ReportOutputFormat.Markdown);

        Assert.Contains("Compared Runs:** 2", markdown);
        Assert.Contains("BASELINE", markdown);
        Assert.Contains("IMPROVED", markdown);
    }

    [Fact]
    public async Task BenchmarkReportLoader_LoadsStringEnumsAndValidatesRequiredFields()
    {
        var stringEnumDir = Path.Combine(_tempDir, "string-enums");
        Directory.CreateDirectory(stringEnumDir);
        var report = BuildSampleReport("main", "1234567", "0.1.0", [BuildSummary("qwen", "Qwen", 1.0, 1, 1, 1.0, 0.9, 1000, 1, 100, 10, 0, 0, 0, 0)]) with
        {
            Provenance = new BenchmarkRunProvenance("0.1.0", "1234567", "main", DateTimeOffset.UtcNow, LlmProviderType.OllamaRouter, "http://localhost:11435", true, null)
        };
        var stringEnumJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        });
        await File.WriteAllTextAsync(Path.Combine(stringEnumDir, BenchmarkPaths.ResultsFileName), stringEnumJson);

        var loaded = await BenchmarkReportLoader.LoadAsync(stringEnumDir);
        Assert.Equal(LlmProviderType.OllamaRouter, loaded.Report.Provenance.ProviderType);

        var nullProvenanceDir = Path.Combine(_tempDir, "null-prov");
        Directory.CreateDirectory(nullProvenanceDir);
        await File.WriteAllTextAsync(Path.Combine(nullProvenanceDir, BenchmarkPaths.ResultsFileName), """{ "suite_name": "s", "dataset_path": "d", "provenance": null, "summaries": [] }""");
        await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkReportLoader.LoadAsync(nullProvenanceDir));

        var nullSummariesDir = Path.Combine(_tempDir, "null-sum");
        Directory.CreateDirectory(nullSummariesDir);
        await File.WriteAllTextAsync(Path.Combine(nullSummariesDir, BenchmarkPaths.ResultsFileName), """{ "suite_name": "s", "dataset_path": "d", "provenance": { "ncf_version": "0.1", "git_commit": "abc", "git_branch": "main", "run_timestamp_utc": "2026-11-22T00:00:00Z", "provider_type": "ollama", "endpoint": "http://localhost:11434", "enable_stack_skills": true }, "summaries": null }""");
        await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkReportLoader.LoadAsync(nullSummariesDir));
    }

    [Fact]
    public void BenchmarkReportLoader_FormatProvenanceTagAndDisambiguateTags_HandleUnknownAndSameLeafFolders()
    {
        var unknownProvenance = new BenchmarkRunProvenance("unknown", "unknown", "unknown", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null);
        Assert.Equal("run-alpha", BenchmarkReportLoader.FormatProvenanceTag(unknownProvenance, Path.Combine(_tempDir, "run-alpha")));

        var sampleReport = BuildSampleReport("main", "1111111", "0.1.0", []);
        var run1 = new LoadedBenchmarkRun(Path.Combine(_tempDir, "exp1", "results"), Path.Combine(_tempDir, "exp1", "results", "results.json"), sampleReport, "main @ 1111111 (v0.1.0)");
        var run2 = new LoadedBenchmarkRun(Path.Combine(_tempDir, "exp2", "results"), Path.Combine(_tempDir, "exp2", "results", "results.json"), sampleReport, "main @ 1111111 (v0.1.0)");
        var run3 = new LoadedBenchmarkRun(Path.Combine(_tempDir, "exp2", "results"), Path.Combine(_tempDir, "exp2", "results", "results.json"), sampleReport, "main @ 1111111 (v0.1.0)");

        var disambiguated = BenchmarkReportLoader.DisambiguateTags([run1, run2, run3]);

        Assert.Equal(3, disambiguated.Select(r => r.DisplayTag).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("exp1/results", disambiguated[0].DisplayTag);
    }

    private string WriteReportDir(string folderName, BenchmarkRunReport report)
    {
        var dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        File.WriteAllText(Path.Combine(dir, BenchmarkPaths.ResultsFileName), json);
        return dir;
    }

    private static BenchmarkRunReport BuildSampleReport(
        string branch,
        string commit,
        string version,
        IReadOnlyList<BenchmarkCandidateSummary> summaries) =>
        new()
        {
            SuiteName = "polyglot-suite",
            DatasetPath = "datasets/polyglot.jsonl",
            Provenance = new BenchmarkRunProvenance(
                NcfVersion: version,
                GitCommit: commit,
                GitBranch: branch,
                RunTimestampUtc: new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero),
                ProviderType: LlmProviderType.Ollama,
                Endpoint: "http://localhost:11434",
                EnableStackSkills: true,
                OptimizationFlags: new LocalOptimizationFlags()),
            Summaries = summaries,
            Results = Array.Empty<BenchmarkInstanceResult>()
        };

    private static BenchmarkCandidateSummary BuildSummary(
        string candidateId,
        string displayName,
        double passRate,
        int passed,
        int total,
        double patchRate,
        double judgeScore,
        double avgDurationMs,
        double avgTurns,
        double avgTokens,
        double tps,
        double editFailureRate,
        int csnav,
        int condenser,
        int repairs) =>
        new()
        {
            CandidateId = candidateId,
            DisplayName = displayName,
            ModelName = candidateId,
            TotalInstances = total,
            PassedInstances = passed,
            FailedInstances = total - passed,
            PassRate = passRate,
            PatchOrVerdictRate = patchRate,
            AverageDeterministicScore = passRate,
            AverageJudgeScore = judgeScore,
            AverageDurationMs = avgDurationMs,
            AverageTurns = avgTurns,
            AverageTokens = avgTokens,
            AverageTokensPerSecond = tps,
            EditFailureRate = editFailureRate,
            TotalCsNavBashCalls = csnav,
            TotalCodeCondenserBashCalls = condenser,
            TotalDispatcherRepairs = repairs,
            ByLanguage =
            [
                new BenchmarkLanguageBreakdown
                {
                    Language = BenchmarkLanguages.CSharp,
                    TotalInstances = 1,
                    PassedInstances = passed > 0 ? 1 : 0,
                    PassRate = passed > 0 ? 1.0 : 0.0,
                    AverageDeterministicScore = passRate,
                    AverageJudgeScore = judgeScore,
                    AverageDurationMs = avgDurationMs
                },
                new BenchmarkLanguageBreakdown
                {
                    Language = BenchmarkLanguages.TypeScript,
                    TotalInstances = 1,
                    PassedInstances = passed > 1 ? 1 : 0,
                    PassRate = passed > 1 ? 1.0 : 0.0,
                    AverageDeterministicScore = passRate,
                    AverageJudgeScore = judgeScore,
                    AverageDurationMs = avgDurationMs
                },
                new BenchmarkLanguageBreakdown
                {
                    Language = BenchmarkLanguages.Python,
                    TotalInstances = 1,
                    PassedInstances = passed > 2 ? 1 : 0,
                    PassRate = passed > 2 ? 1.0 : 0.0,
                    AverageDeterministicScore = passRate,
                    AverageJudgeScore = judgeScore,
                    AverageDurationMs = avgDurationMs
                },
                new BenchmarkLanguageBreakdown
                {
                    Language = BenchmarkLanguages.Rust,
                    TotalInstances = 1,
                    PassedInstances = passed > 3 ? 1 : 0,
                    PassRate = passed > 3 ? 1.0 : 0.0,
                    AverageDeterministicScore = passRate,
                    AverageJudgeScore = judgeScore,
                    AverageDurationMs = avgDurationMs
                }
            ]
        };
}
