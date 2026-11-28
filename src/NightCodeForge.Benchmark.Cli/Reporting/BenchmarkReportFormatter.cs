using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Reporting;

/// <summary>
/// Formats single-run leaderboards (including per-language breakdowns) and cross-version
/// comparative regression reports in Markdown, ASCII, and JSON formats.
/// </summary>
public static class BenchmarkReportFormatter
{
    private const double PassRateRegressionEpsilon = 0.0001;
    private const double EditFailureRegressionThreshold = 0.02;
    private const int ShortCommitLength = 7;

    private const string ModeSingle = "single";
    private const string ModeComparison = "comparison";

    private const string StatusBaseline = "BASELINE";
    private const string StatusNew = "NEW";
    private const string StatusRegression = "REGRESSION";
    private const string StatusImproved = "IMPROVED";
    private const string StatusUnchanged = "UNCHANGED";

    private static readonly string[] PreferredLanguageOrder =
    [
        BenchmarkLanguages.CSharp,
        BenchmarkLanguages.TypeScript,
        BenchmarkLanguages.JavaScript,
        BenchmarkLanguages.Python,
        BenchmarkLanguages.Rust
    ];

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static readonly IReadOnlyList<string> LeaderboardHeaders =
    [
        "Model",
        "Pass@1 / Accuracy %",
        "Output Produced %",
        "Judge Score",
        "Avg Duration",
        "Avg Turns",
        "Avg Tokens",
        "Tokens/s",
        "Edit Failure %",
        "csnav Calls",
        "codecondenser Calls",
        "Dispatcher Repairs",
        "Anti-Cheat Alerts"
    ];

    private static readonly IReadOnlyList<string> LanguageHeaders =
    [
        "Language",
        "Model",
        "Passed / Total",
        "Pass@1 / Accuracy %",
        "Det. Score",
        "Judge Score",
        "Avg Duration"
    ];

    private static readonly IReadOnlyList<string> ComparisonLeaderboardHeaders =
    [
        "Model",
        "Run (Branch @ Commit / Version)",
        "Pass@1 / Accuracy %",
        "Output Produced %",
        "Judge Score",
        "Avg Duration",
        "Avg Turns",
        "Avg Tokens",
        "Tokens/s",
        "Edit Failure %",
        "csnav Calls",
        "codecondenser Calls",
        "Dispatcher Repairs",
        "Anti-Cheat Alerts",
        "Status"
    ];

    private static readonly IReadOnlyList<string> ComparisonLanguageHeaders =
    [
        "Language",
        "Model",
        "Run (Branch @ Commit / Version)",
        "Passed / Total",
        "Pass@1 / Accuracy %",
        "Det. Score",
        "Judge Score",
        "Avg Duration",
        "Status"
    ];

    public static string FormatSingleRun(BenchmarkRunReport report, ReportOutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);

        var summaries = report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>();
        var orderedSummaries = OrderSummaries(summaries);
        var languages = CollectOrderedLanguages(
            orderedSummaries
                .SelectMany(s => s.ByLanguage ?? Array.Empty<BenchmarkLanguageBreakdown>())
                .Select(l => l.Language));

        if (format == ReportOutputFormat.Json)
        {
            var payload = new
            {
                mode = ModeSingle,
                suite_name = report.SuiteName,
                dataset_path = report.DatasetPath,
                provenance = report.Provenance,
                summaries = orderedSummaries,
                languages = languages.Select(lang => new
                {
                    language = lang,
                    models = orderedSummaries
                        .Select(s => new
                        {
                            candidate_id = s.CandidateId,
                            display_name = s.DisplayName,
                            model_name = s.ModelName,
                            breakdown = FindLanguageBreakdown(s, lang)
                        })
                        .Where(x => x.breakdown != null)
                        .ToList()
                }).ToList()
            };
            return JsonSerializer.Serialize(payload, JsonOutputOptions);
        }

        var sb = new StringBuilder();
        var provenanceTag = BenchmarkReportLoader.FormatProvenanceTag(report.Provenance);
        var timestamp = report.Provenance.RunTimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        if (format == ReportOutputFormat.Markdown)
        {
            sb.AppendLine($"# Benchmark Report: {report.SuiteName}");
            sb.AppendLine();
            sb.AppendLine($"- **Dataset:** `{report.DatasetPath}`");
            sb.AppendLine($"- **Provenance:** `{provenanceTag}`");
            sb.AppendLine($"- **Provider:** `{report.Provenance.ProviderType}` (`{report.Provenance.Endpoint}`)");
            sb.AppendLine($"- **Stack Skills:** {(report.Provenance.EnableStackSkills ? "Enabled" : "Disabled")}");
            if (report.Provenance.DetectedToolVersions is { Count: > 0 })
            {
                var toolSummary = string.Join(", ",
                    report.Provenance.DetectedToolVersions
                        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => $"`{kv.Key}={kv.Value}`"));
                sb.AppendLine($"- **Toolchains:** {toolSummary}");
            }
            sb.AppendLine($"- **Timestamp (UTC):** {timestamp}");
            sb.AppendLine();
            sb.AppendLine("## Overall Model Leaderboard");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine($"Benchmark Report: {report.SuiteName}");
            sb.AppendLine($"Dataset     : {report.DatasetPath}");
            sb.AppendLine($"Provenance  : {provenanceTag}");
            sb.AppendLine($"Provider    : {report.Provenance.ProviderType} ({report.Provenance.Endpoint})");
            sb.AppendLine($"Stack Skills: {(report.Provenance.EnableStackSkills ? "Enabled" : "Disabled")}");
            if (report.Provenance.DetectedToolVersions is { Count: > 0 })
            {
                var toolSummary = string.Join(", ",
                    report.Provenance.DetectedToolVersions
                        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => $"{kv.Key}={kv.Value}"));
                sb.AppendLine($"Toolchains  : {toolSummary}");
            }
            sb.AppendLine($"Timestamp   : {timestamp} UTC");
            sb.AppendLine();
            sb.AppendLine("Overall Model Leaderboard");
        }

        var mainRows = orderedSummaries
            .Select(s => (IReadOnlyList<string>)
            [
                FormatCandidateLabel(s),
                FormatPassRate(s.PassRate, s.PassedInstances, s.TotalInstances),
                FormatPercent(s.PatchOrVerdictRate),
                FormatNullableScore(s.AverageJudgeScore),
                FormatDurationMs(s.AverageDurationMs),
                FormatDecimal(s.AverageTurns, "F1"),
                FormatTokens(s.AverageTokens),
                FormatDecimal(s.AverageTokensPerSecond, "F1"),
                FormatPercent(s.EditFailureRate),
                s.TotalCsNavBashCalls.ToString(CultureInfo.InvariantCulture),
                s.TotalCodeCondenserBashCalls.ToString(CultureInfo.InvariantCulture),
                s.TotalDispatcherRepairs.ToString(CultureInfo.InvariantCulture),
                s.TotalAntiCheatAlerts.ToString(CultureInfo.InvariantCulture)
            ])
            .ToList();

        RenderTable(sb, LeaderboardHeaders, mainRows, format);

        if (languages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(format == ReportOutputFormat.Markdown
                ? "## Breakdown by Language"
                : "Breakdown by Language");
            if (format == ReportOutputFormat.Markdown)
            {
                sb.AppendLine();
            }

            var langRows = new List<IReadOnlyList<string>>();
            foreach (var lang in languages)
            {
                foreach (var summary in orderedSummaries)
                {
                    var b = FindLanguageBreakdown(summary, lang);
                    if (b == null)
                    {
                        continue;
                    }

                    langRows.Add(
                    [
                        lang,
                        FormatCandidateLabel(summary),
                        $"{b.PassedInstances}/{b.TotalInstances}",
                        FormatPercent(b.PassRate),
                        FormatDecimal(b.AverageDeterministicScore, "F2"),
                        FormatNullableScore(b.AverageJudgeScore),
                        FormatDurationMs(b.AverageDurationMs)
                    ]);
                }
            }

            RenderTable(sb, LanguageHeaders, langRows, format);
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatComparison(
        IReadOnlyList<LoadedBenchmarkRun> runs,
        LoadedBenchmarkRun baseline,
        ReportOutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(baseline);
        if (runs.Count == 0)
        {
            throw new ArgumentException("At least one benchmark run is required for comparison.", nameof(runs));
        }

        bool baselineIncluded = runs.Any(r =>
            ReferenceEquals(r, baseline) ||
            string.Equals(r.ResultsFilePath, baseline.ResultsFilePath, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<LoadedBenchmarkRun> effectiveRuns;
        if (baselineIncluded)
        {
            effectiveRuns = runs;
        }
        else
        {
            var withBaseline = new List<LoadedBenchmarkRun>(runs.Count + 1) { baseline };
            withBaseline.AddRange(runs);
            effectiveRuns = withBaseline;
        }

        var normalizedRuns = BenchmarkReportLoader.DisambiguateTags(effectiveRuns);
        var normalizedBaseline = normalizedRuns.FirstOrDefault(r =>
            string.Equals(r.ResultsFilePath, baseline.ResultsFilePath, StringComparison.OrdinalIgnoreCase))
            ?? normalizedRuns[0];

        var comparisons = BuildComparisonEntries(normalizedRuns, normalizedBaseline);
        var languages = CollectOrderedLanguages(
            normalizedRuns
                .SelectMany(r => r.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>())
                .SelectMany(s => s.ByLanguage ?? Array.Empty<BenchmarkLanguageBreakdown>())
                .Select(l => l.Language));
        var languageComparisons = BuildLanguageComparisonEntries(normalizedRuns, normalizedBaseline, languages);

        if (format == ReportOutputFormat.Json)
        {
            var payload = new
            {
                mode = ModeComparison,
                baseline_tag = normalizedBaseline.DisplayTag,
                baseline_directory = normalizedBaseline.SourceDirectory,
                runs = normalizedRuns.Select(r => new
                {
                    tag = r.DisplayTag,
                    is_baseline = ReferenceEquals(r, normalizedBaseline),
                    directory = r.SourceDirectory,
                    suite_name = r.Report.SuiteName,
                    dataset_path = r.Report.DatasetPath,
                    provenance = r.Report.Provenance
                }).ToList(),
                comparisons = comparisons.Select(c => new
                {
                    model = c.ModelLabel,
                    candidate_id = c.Summary.CandidateId,
                    model_name = c.Summary.ModelName,
                    run_tag = c.Run.DisplayTag,
                    is_baseline = c.IsBaseline,
                    status = c.Status,
                    is_regression = c.IsRegression,
                    summary = c.Summary,
                    deltas = c.BaselineSummary == null || c.IsBaseline ? null : new
                    {
                        pass_rate = c.Summary.PassRate - c.BaselineSummary.PassRate,
                        patch_or_verdict_rate = c.Summary.PatchOrVerdictRate - c.BaselineSummary.PatchOrVerdictRate,
                        average_judge_score = (c.Summary.AverageJudgeScore.HasValue && c.BaselineSummary.AverageJudgeScore.HasValue)
                            ? c.Summary.AverageJudgeScore.Value - c.BaselineSummary.AverageJudgeScore.Value
                            : (double?)null,
                        average_duration_ms = c.Summary.AverageDurationMs - c.BaselineSummary.AverageDurationMs,
                        average_turns = c.Summary.AverageTurns - c.BaselineSummary.AverageTurns,
                        average_tokens = c.Summary.AverageTokens - c.BaselineSummary.AverageTokens,
                        average_tokens_per_second = c.Summary.AverageTokensPerSecond - c.BaselineSummary.AverageTokensPerSecond,
                        edit_failure_rate = c.Summary.EditFailureRate - c.BaselineSummary.EditFailureRate,
                        csnav_calls = c.Summary.TotalCsNavBashCalls - c.BaselineSummary.TotalCsNavBashCalls,
                        codecondenser_calls = c.Summary.TotalCodeCondenserBashCalls - c.BaselineSummary.TotalCodeCondenserBashCalls,
                        dispatcher_repairs = c.Summary.TotalDispatcherRepairs - c.BaselineSummary.TotalDispatcherRepairs,
                        anti_cheat_alerts = c.Summary.TotalAntiCheatAlerts - c.BaselineSummary.TotalAntiCheatAlerts
                    }
                }).ToList(),
                language_comparisons = languageComparisons.Select(lc => new
                {
                    language = lc.Language,
                    model = lc.ModelLabel,
                    run_tag = lc.Run.DisplayTag,
                    is_baseline = lc.IsBaseline,
                    status = lc.Status,
                    is_regression = lc.IsRegression,
                    breakdown = lc.Breakdown,
                    pass_rate_delta = lc.BaselineBreakdown != null && !lc.IsBaseline
                        ? lc.Breakdown.PassRate - lc.BaselineBreakdown.PassRate
                        : (double?)null
                }).ToList()
            };
            return JsonSerializer.Serialize(payload, JsonOutputOptions);
        }

        var sb = new StringBuilder();
        if (format == ReportOutputFormat.Markdown)
        {
            sb.AppendLine("# Cross-Version Benchmark Comparison");
            sb.AppendLine();
            sb.AppendLine($"- **Baseline:** `{normalizedBaseline.DisplayTag}` (`{normalizedBaseline.SourceDirectory}`)");
            sb.AppendLine($"- **Compared Runs:** {normalizedRuns.Count}");
            sb.AppendLine();
            sb.AppendLine("## Run Provenance");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Cross-Version Benchmark Comparison");
            sb.AppendLine($"Baseline      : {normalizedBaseline.DisplayTag} ({normalizedBaseline.SourceDirectory})");
            sb.AppendLine($"Compared Runs : {normalizedRuns.Count}");
            sb.AppendLine();
            sb.AppendLine("Run Provenance");
        }

        IReadOnlyList<string> provenanceHeaders =
        [
            "Run Tag",
            "Baseline",
            "Suite",
            "NCF Version",
            "GitBranch @ GitCommit",
            "Provider",
            "Skills",
            "Timestamp (UTC)"
        ];
        var provenanceRows = normalizedRuns
            .Select(r =>
            {
                var p = r.Report.Provenance;
                var rawBranch = string.IsNullOrWhiteSpace(p.GitBranch)
                    ? BenchmarkCliConstants.UnknownProvenanceValue
                    : p.GitBranch.Trim();
                var rawCommit = string.IsNullOrWhiteSpace(p.GitCommit)
                    ? BenchmarkCliConstants.UnknownProvenanceValue
                    : p.GitCommit.Trim();
                var shortCommit = rawCommit.Length > ShortCommitLength &&
                                  !string.Equals(rawCommit, BenchmarkCliConstants.UnknownProvenanceValue, StringComparison.OrdinalIgnoreCase)
                    ? rawCommit[..ShortCommitLength]
                    : rawCommit;
                var rawVersion = string.IsNullOrWhiteSpace(p.NcfVersion)
                    ? BenchmarkCliConstants.UnknownProvenanceValue
                    : p.NcfVersion.Trim();

                return (IReadOnlyList<string>)
                [
                    r.DisplayTag,
                    ReferenceEquals(r, normalizedBaseline) ? "YES" : "no",
                    r.Report.SuiteName,
                    rawVersion,
                    $"{rawBranch} @ {shortCommit}",
                    p.ProviderType.ToString(),
                    p.EnableStackSkills ? "On" : "Off",
                    p.RunTimestampUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                ];
            })
            .ToList();
        RenderTable(sb, provenanceHeaders, provenanceRows, format);

        sb.AppendLine();
        sb.AppendLine(format == ReportOutputFormat.Markdown
            ? "## Comparative Leaderboard vs Baseline"
            : "Comparative Leaderboard vs Baseline");
        if (format == ReportOutputFormat.Markdown)
        {
            sb.AppendLine();
        }

        var comparisonRows = comparisons.Select(c =>
        {
            var s = c.Summary;
            var b = c.IsBaseline ? null : c.BaselineSummary;
            return (IReadOnlyList<string>)
            [
                c.ModelLabel,
                c.Run.DisplayTag,
                FormatPassRateWithDelta(s.PassRate, s.PassedInstances, s.TotalInstances, b?.PassRate),
                FormatPercentWithDelta(s.PatchOrVerdictRate, b?.PatchOrVerdictRate),
                FormatScoreWithDelta(s.AverageJudgeScore, b?.AverageJudgeScore),
                FormatDurationWithDelta(s.AverageDurationMs, b?.AverageDurationMs),
                FormatDecimalWithDelta(s.AverageTurns, b?.AverageTurns, "F1"),
                FormatTokensWithDelta(s.AverageTokens, b?.AverageTokens),
                FormatDecimalWithDelta(s.AverageTokensPerSecond, b?.AverageTokensPerSecond, "F1"),
                FormatPercentWithDelta(s.EditFailureRate, b?.EditFailureRate),
                FormatIntWithDelta(s.TotalCsNavBashCalls, b?.TotalCsNavBashCalls),
                FormatIntWithDelta(s.TotalCodeCondenserBashCalls, b?.TotalCodeCondenserBashCalls),
                FormatIntWithDelta(s.TotalDispatcherRepairs, b?.TotalDispatcherRepairs),
                FormatIntWithDelta(s.TotalAntiCheatAlerts, b?.TotalAntiCheatAlerts),
                c.Status
            ];
        }).ToList();

        RenderTable(sb, ComparisonLeaderboardHeaders, comparisonRows, format);

        if (languageComparisons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(format == ReportOutputFormat.Markdown
                ? "## Language Breakdown Comparison"
                : "Language Breakdown Comparison");
            if (format == ReportOutputFormat.Markdown)
            {
                sb.AppendLine();
            }

            var langRows = languageComparisons.Select(lc =>
            {
                var l = lc.Breakdown;
                var bl = lc.IsBaseline ? null : lc.BaselineBreakdown;
                return (IReadOnlyList<string>)
                [
                    lc.Language,
                    lc.ModelLabel,
                    lc.Run.DisplayTag,
                    $"{l.PassedInstances}/{l.TotalInstances}",
                    FormatPercentWithDelta(l.PassRate, bl?.PassRate),
                    FormatDecimalWithDelta(l.AverageDeterministicScore, bl?.AverageDeterministicScore, "F2"),
                    FormatScoreWithDelta(l.AverageJudgeScore, bl?.AverageJudgeScore),
                    FormatDurationWithDelta(l.AverageDurationMs, bl?.AverageDurationMs),
                    lc.Status
                ];
            }).ToList();

            RenderTable(sb, ComparisonLanguageHeaders, langRows, format);
        }

        return sb.ToString().TrimEnd();
    }

    private sealed record ComparisonEntry(
        string ModelLabel,
        LoadedBenchmarkRun Run,
        BenchmarkCandidateSummary Summary,
        BenchmarkCandidateSummary? BaselineSummary,
        bool IsBaseline,
        bool IsRegression,
        string Status);

    private sealed record LanguageComparisonEntry(
        string Language,
        string ModelLabel,
        LoadedBenchmarkRun Run,
        BenchmarkLanguageBreakdown Breakdown,
        BenchmarkLanguageBreakdown? BaselineBreakdown,
        bool IsBaseline,
        bool IsRegression,
        string Status);

    private static List<ComparisonEntry> BuildComparisonEntries(
        IReadOnlyList<LoadedBenchmarkRun> runs,
        LoadedBenchmarkRun baseline)
    {
        var modelKeys = CollectOrderedModelKeys(runs, baseline);
        var entries = new List<ComparisonEntry>();

        foreach (var identity in modelKeys)
        {
            var baselineSummary = FindMatchingSummary(baseline.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>(), identity);
            foreach (var run in runs)
            {
                var summary = FindMatchingSummary(run.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>(), identity);
                if (summary == null)
                {
                    continue;
                }

                bool isBaseline = ReferenceEquals(run, baseline);
                var (isRegression, status) = EvaluateStatus(summary, baselineSummary, isBaseline);
                entries.Add(new ComparisonEntry(
                    FormatCandidateLabel(summary),
                    run,
                    summary,
                    baselineSummary,
                    isBaseline,
                    isRegression,
                    status));
            }
        }

        return entries;
    }

    private static List<LanguageComparisonEntry> BuildLanguageComparisonEntries(
        IReadOnlyList<LoadedBenchmarkRun> runs,
        LoadedBenchmarkRun baseline,
        IReadOnlyList<string> languages)
    {
        var modelKeys = CollectOrderedModelKeys(runs, baseline);
        var entries = new List<LanguageComparisonEntry>();

        foreach (var lang in languages)
        {
            foreach (var identity in modelKeys)
            {
                var baselineSummary = FindMatchingSummary(baseline.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>(), identity);
                var baselineLang = baselineSummary != null ? FindLanguageBreakdown(baselineSummary, lang) : null;

                foreach (var run in runs)
                {
                    var summary = FindMatchingSummary(run.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>(), identity);
                    var currentLang = summary != null ? FindLanguageBreakdown(summary, lang) : null;
                    if (summary == null || currentLang == null)
                    {
                        continue;
                    }

                    bool isBaseline = ReferenceEquals(run, baseline);
                    var (isRegression, status) = EvaluateLanguageStatus(currentLang, baselineLang, isBaseline);
                    entries.Add(new LanguageComparisonEntry(
                        lang,
                        FormatCandidateLabel(summary),
                        run,
                        currentLang,
                        baselineLang,
                        isBaseline,
                        isRegression,
                        status));
                }
            }
        }

        return entries;
    }

    private static List<string> CollectOrderedModelKeys(
        IReadOnlyList<LoadedBenchmarkRun> runs,
        LoadedBenchmarkRun baseline)
    {
        var orderedSummaries = OrderSummaries(baseline.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>())
            .Concat(runs
                .Where(r => !ReferenceEquals(r, baseline))
                .SelectMany(r => OrderSummaries(r.Report.Summaries ?? Array.Empty<BenchmarkCandidateSummary>())));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        foreach (var s in orderedSummaries)
        {
            var identity = ResolveCandidateIdentity(s);
            if (!string.IsNullOrEmpty(identity) && seen.Add(identity))
            {
                keys.Add(identity);
            }
        }

        return keys;
    }

    private static string ResolveCandidateIdentity(BenchmarkCandidateSummary summary) =>
        !string.IsNullOrWhiteSpace(summary.CandidateId)
            ? summary.CandidateId.Trim()
            : (summary.ModelName ?? string.Empty).Trim();

    private static BenchmarkCandidateSummary? FindMatchingSummary(
        IReadOnlyList<BenchmarkCandidateSummary> summaries,
        string candidateIdentity) =>
        summaries.FirstOrDefault(s =>
            string.Equals(ResolveCandidateIdentity(s), candidateIdentity, StringComparison.OrdinalIgnoreCase));

    private static BenchmarkLanguageBreakdown? FindLanguageBreakdown(
        BenchmarkCandidateSummary summary,
        string normalizedLanguage) =>
        (summary.ByLanguage ?? Array.Empty<BenchmarkLanguageBreakdown>())
            .FirstOrDefault(b => string.Equals(BenchmarkLanguages.Normalize(b.Language), normalizedLanguage, StringComparison.OrdinalIgnoreCase));

    private static (bool IsRegression, string Status) EvaluateStatus(
        BenchmarkCandidateSummary current,
        BenchmarkCandidateSummary? baseline,
        bool isBaseline)
    {
        if (isBaseline)
        {
            return (false, StatusBaseline);
        }

        if (baseline == null)
        {
            return (false, StatusNew);
        }

        var passDelta = current.PassRate - baseline.PassRate;
        var detDelta = current.AverageDeterministicScore - baseline.AverageDeterministicScore;
        var editFailureDelta = current.EditFailureRate - baseline.EditFailureRate;

        if (passDelta < -PassRateRegressionEpsilon ||
            (Math.Abs(passDelta) <= PassRateRegressionEpsilon && detDelta < -PassRateRegressionEpsilon) ||
            (passDelta <= PassRateRegressionEpsilon && editFailureDelta > EditFailureRegressionThreshold))
        {
            return (true, StatusRegression);
        }

        if (passDelta > PassRateRegressionEpsilon || detDelta > PassRateRegressionEpsilon)
        {
            return (false, StatusImproved);
        }

        return (false, StatusUnchanged);
    }

    private static (bool IsRegression, string Status) EvaluateLanguageStatus(
        BenchmarkLanguageBreakdown current,
        BenchmarkLanguageBreakdown? baseline,
        bool isBaseline)
    {
        if (isBaseline)
        {
            return (false, StatusBaseline);
        }

        if (baseline == null)
        {
            return (false, StatusNew);
        }

        var passDelta = current.PassRate - baseline.PassRate;
        var detDelta = current.AverageDeterministicScore - baseline.AverageDeterministicScore;

        if (passDelta < -PassRateRegressionEpsilon ||
            (Math.Abs(passDelta) <= PassRateRegressionEpsilon && detDelta < -PassRateRegressionEpsilon))
        {
            return (true, StatusRegression);
        }

        if (passDelta > PassRateRegressionEpsilon || detDelta > PassRateRegressionEpsilon)
        {
            return (false, StatusImproved);
        }

        return (false, StatusUnchanged);
    }

    private static IReadOnlyList<BenchmarkCandidateSummary> OrderSummaries(IEnumerable<BenchmarkCandidateSummary> summaries) =>
        summaries
            .OrderByDescending(s => s.PassRate)
            .ThenByDescending(s => s.AverageDeterministicScore)
            .ThenBy(s => s.AverageDurationMs)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> CollectOrderedLanguages(IEnumerable<string> languages)
    {
        var unique = languages
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(BenchmarkLanguages.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return unique
            .OrderBy(lang =>
            {
                int idx = Array.FindIndex(PreferredLanguageOrder, p => string.Equals(p, lang, StringComparison.OrdinalIgnoreCase));
                return idx >= 0 ? idx : PreferredLanguageOrder.Length;
            })
            .ThenBy(lang => lang, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatCandidateLabel(BenchmarkCandidateSummary s) =>
        string.IsNullOrWhiteSpace(s.DisplayName) ? s.ModelName : s.DisplayName;

    private static string FormatPassRate(double rate, int passed, int total) =>
        $"{(rate * 100.0).ToString("F1", CultureInfo.InvariantCulture)}% ({passed}/{total})";

    private static string FormatPassRateWithDelta(double rate, int passed, int total, double? baselineRate)
    {
        var baseText = FormatPassRate(rate, passed, total);
        if (!baselineRate.HasValue)
        {
            return baseText;
        }

        var deltaPct = (rate - baselineRate.Value) * 100.0;
        return $"{baseText} ({FormatSignedDouble(deltaPct, "F1")}%)";
    }

    private static string FormatPercent(double rate) =>
        $"{(rate * 100.0).ToString("F1", CultureInfo.InvariantCulture)}%";

    private static string FormatPercentWithDelta(double rate, double? baselineRate)
    {
        var baseText = FormatPercent(rate);
        if (!baselineRate.HasValue)
        {
            return baseText;
        }

        var deltaPct = (rate - baselineRate.Value) * 100.0;
        return $"{baseText} ({FormatSignedDouble(deltaPct, "F1")}%)";
    }

    private static string FormatNullableScore(double? score) =>
        score.HasValue ? score.Value.ToString("F2", CultureInfo.InvariantCulture) : "-";

    private static string FormatScoreWithDelta(double? score, double? baselineScore)
    {
        if (!score.HasValue)
        {
            return "-";
        }

        var baseText = score.Value.ToString("F2", CultureInfo.InvariantCulture);
        if (!baselineScore.HasValue)
        {
            return baseText;
        }

        return $"{baseText} ({FormatSignedDouble(score.Value - baselineScore.Value, "F2")})";
    }

    private static string FormatDurationMs(double durationMs) =>
        $"{(durationMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture)}s";

    private static string FormatDurationWithDelta(double durationMs, double? baselineDurationMs)
    {
        var baseText = FormatDurationMs(durationMs);
        if (!baselineDurationMs.HasValue)
        {
            return baseText;
        }

        var deltaSec = (durationMs - baselineDurationMs.Value) / 1000.0;
        return $"{baseText} ({FormatSignedDouble(deltaSec, "F1")}s)";
    }

    private static string FormatDecimal(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatDecimalWithDelta(double value, double? baselineValue, string format)
    {
        var baseText = FormatDecimal(value, format);
        if (!baselineValue.HasValue)
        {
            return baseText;
        }

        return $"{baseText} ({FormatSignedDouble(value - baselineValue.Value, format)})";
    }

    private static string FormatTokens(double tokens) =>
        Math.Round(tokens).ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatTokensWithDelta(double tokens, double? baselineTokens)
    {
        var baseText = FormatTokens(tokens);
        if (!baselineTokens.HasValue)
        {
            return baseText;
        }

        var delta = (long)Math.Round(tokens - baselineTokens.Value, MidpointRounding.AwayFromZero);
        var sign = delta >= 0 ? "+" : string.Empty;
        return $"{baseText} ({sign}{delta.ToString("N0", CultureInfo.InvariantCulture)})";
    }

    private static string FormatIntWithDelta(int value, int? baselineValue)
    {
        var baseText = value.ToString(CultureInfo.InvariantCulture);
        if (!baselineValue.HasValue)
        {
            return baseText;
        }

        var delta = value - baselineValue.Value;
        var sign = delta >= 0 ? "+" : string.Empty;
        return $"{baseText} ({sign}{delta.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string FormatSignedDouble(double value, string format)
    {
        var formatted = value.ToString(format, CultureInfo.InvariantCulture);
        if (formatted.StartsWith('-') &&
            double.TryParse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            parsed == 0.0)
        {
            return $"+{formatted[1..]}";
        }

        var sign = value >= 0 ? "+" : string.Empty;
        return $"{sign}{formatted}";
    }

    private static void RenderTable(
        StringBuilder sb,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        ReportOutputFormat format)
    {
        if (format == ReportOutputFormat.Markdown)
        {
            sb.Append("| ");
            sb.Append(string.Join(" | ", headers.Select(EscapeMarkdownCell)));
            sb.AppendLine(" |");

            sb.Append("| ");
            sb.Append(string.Join(" | ", headers.Select(_ => "---")));
            sb.AppendLine(" |");

            foreach (var row in rows)
            {
                sb.Append("| ");
                sb.Append(string.Join(" | ", row.Select(EscapeMarkdownCell)));
                sb.AppendLine(" |");
            }

            return;
        }

        var sanitizedHeaders = headers.Select(SanitizeSingleLine).ToArray();
        var sanitizedRows = rows
            .Select(r => r.Select(SanitizeSingleLine).ToArray())
            .ToList();

        var widths = new int[sanitizedHeaders.Length];
        for (int col = 0; col < sanitizedHeaders.Length; col++)
        {
            widths[col] = sanitizedHeaders[col].Length;
        }

        foreach (var row in sanitizedRows)
        {
            for (int col = 0; col < Math.Min(widths.Length, row.Length); col++)
            {
                if (row[col].Length > widths[col])
                {
                    widths[col] = row[col].Length;
                }
            }
        }

        var border = "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        sb.AppendLine(border);
        AppendAsciiRow(sb, sanitizedHeaders, widths);
        sb.AppendLine(border);
        foreach (var row in sanitizedRows)
        {
            AppendAsciiRow(sb, row, widths);
        }
        sb.AppendLine(border);
    }

    private static void AppendAsciiRow(StringBuilder sb, IReadOnlyList<string> cells, int[] widths)
    {
        sb.Append('|');
        for (int col = 0; col < widths.Length; col++)
        {
            var value = col < cells.Count ? cells[col] : string.Empty;
            sb.Append(' ');
            sb.Append(value.PadRight(widths[col]));
            sb.Append(" |");
        }
        sb.AppendLine();
    }

    private static string SanitizeSingleLine(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeMarkdownCell(string value) =>
        SanitizeSingleLine(value).Replace("|", "\\|", StringComparison.Ordinal);
}
