namespace NightCodeForge.Cli.Commands;

using System.Text.Json;
using NightCodeForge.Core.Services;

public class TranscriptOptions
{
    public string TargetPath { get; set; } = Directory.GetCurrentDirectory();
    public bool Verbose { get; set; }
    public bool Json { get; set; }
    public int? Limit { get; set; } = TranscriptAnalysisDefaults.DefaultRecentLimit;
}

public class TranscriptCommand
{
    private readonly ITranscriptAnalysisService _analysisService;

    public TranscriptCommand(ITranscriptAnalysisService analysisService)
    {
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
    }

    public async Task<int> ExecuteAsync(TranscriptOptions options, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= Console.WriteLine;
        var rawPath = string.IsNullOrWhiteSpace(options.TargetPath) ? Directory.GetCurrentDirectory() : options.TargetPath;
        var fullPath = Path.GetFullPath(rawPath);

        if (File.Exists(fullPath))
        {
            var analysis = await _analysisService.AnalyzeFileAsync(fullPath, cancellationToken);
            if (options.Json)
            {
                log(JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            RenderSingleAnalysis(analysis, log, options.Verbose);
            return analysis.HasFriction ? 1 : 0;
        }

        if (Directory.Exists(fullPath))
        {
            // If docs/transcripts subdirectory exists, prefer that when given a repo root without top-level transcripts
            var searchDir = fullPath;
            var docTranscripts = Path.Combine(fullPath, "docs", "transcripts");
            if (Directory.Exists(docTranscripts) &&
                TranscriptAnalysisService.DiscoverOrderedTranscriptFiles(new[] { fullPath }).Count == 0)
            {
                searchDir = docTranscripts;
            }

            var window = await _analysisService.AnalyzeDirectoryWindowAsync(searchDir, options.Limit, cancellationToken);
            var analyses = window.Items;
            if (analyses.Count == 0)
            {
                log($"[ncf] No markdown transcripts found in {searchDir}");
                return 0;
            }

            if (options.Json)
            {
                log(JsonSerializer.Serialize(analyses, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            RenderDirectoryAnalysis(searchDir, analyses, window.TotalAvailableCount, log, options.Verbose);
            return analyses.Any(a => a.HasFriction) ? 1 : 0;
        }

        log($"[ncf] Error: Path not found: {fullPath}");
        return 2;
    }

    private static void RenderSingleAnalysis(TranscriptAnalysis a, Action<string> log, bool verbose)
    {
        var fm = a.Frontmatter;
        var duration = fm?.DurationMs.HasValue == true
            ? FormatDuration(fm.DurationMs.Value)
            : "unknown";

        var statusIcon = fm?.Status switch
        {
            "Completed" => "🟢",
            "WaitingForHuman" => "🟡",
            "Cancelled" or "Interrupted" or "Failed" => "🔴",
            _ => "⚪"
        };

        log($"================================================================================");
        log($"📊 TRANSCRIPT ANALYSIS: {a.FileName}");
        log($"================================================================================");
        log($"  Task:       #{fm?.TaskId?.ToString() ?? "?"} ({fm?.StageId ?? "unknown"})");
        log($"  Profile:    {fm?.AgentProfile ?? "unknown"}");
        log($"  Model:      {fm?.Model ?? "unknown"}");
        log($"  Status:     {statusIcon} {fm?.Status ?? "unknown"}");
        log($"  Duration:   {duration}");

        if (fm?.Tokens != null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var totalTokens = fm.Tokens.Total > 0 ? fm.Tokens.Total : fm.Tokens.Prompt + fm.Tokens.Completion;
            log($"  Tokens:     {totalTokens.ToString("N0", inv)} total ({fm.Tokens.Prompt.ToString("N0", inv)} prompt, {fm.Tokens.Completion.ToString("N0", inv)} completion)");
        }

        log($"\n--- Turn & Message Dynamics ---");
        log($"  Total Messages:  {a.TotalTurns} (👤 {a.UserTurns} User, 🤖 {a.AssistantTurns} Assistant, 🔧 {a.ToolTurns} Tool, 💭 {a.ThinkingTurns} Thinking)");

        log($"\n--- Tool Invocations ---");
        if (a.ToolUsage.Count == 0)
        {
            log("  No tool calls detected.");
        }
        else
        {
            foreach (var kvp in a.ToolUsage.OrderByDescending(x => x.Value))
            {
                log($"  • {kvp.Key,-16}: {kvp.Value,4} call(s)");
            }
        }

        log($"\n--- Execution Errors & Diagnostic Failures ---");
        log($"  • Edit Mismatches:      {a.EditFailures}");
        log($"  • Bash Errors:          {a.BashErrors}");
        log($"  • Compiler Diagnostics: {a.CompilerErrors}");
        log($"  • Test Run Failures:    {a.TestFailures}");
        log($"  • Generic Tool Errors:  {a.ToolErrors}");
        log($"  • Context Compactions:  {a.CompactionEvents}");
        log($"  • History Rewrites:     {a.HistoryRewrites}");

        if (a.HighChurnFiles.Count > 0)
        {
            log($"\n--- High-Churn Edited Files (>= 3 edits) ---");
            foreach (var f in a.HighChurnFiles)
            {
                log($"  ⚠️ {f.Key}: {f.Value} edits");
            }
        }

        if (verbose && a.TopReadFiles.Count > 0)
        {
            log($"\n--- Most Frequently Read Files ---");
            foreach (var f in a.TopReadFiles.Take(5))
            {
                log($"  📖 {f.Key}: {f.Value} reads");
            }
        }

        RenderSingleBreakdowns(a, log);

        if (a.FrictionPoints.Count > 0)
        {
            log($"\n--- Friction Points Detected ---");
            foreach (var fp in a.FrictionPoints)
            {
                log($"  ⚠️  {fp}");
            }
        }

        RenderCategorizedRecommendations(
            a.UserCodebaseRecommendations,
            a.NightCodeForgeRecommendations,
            log,
            isFleet: false);

        log($"================================================================================\n");
    }

    private static void RenderSingleBreakdowns(TranscriptAnalysis a, Action<string> log)
    {
        if (a.FileFriction.Count > 0)
        {
            log($"\n--- Top File Friction ---");
            foreach (var f in a.FileFriction.Take(5))
            {
                var splitTag = f.IsSplitCandidate ? " [split candidate]" : string.Empty;
                log($"  📁 {f.FilePath}: {f.ReadCount}R / {f.EditCount}E ({f.EditFailureCount} fail, {f.EditedWithoutReadCount} blind, {f.ConsecutiveReReadCount} reread){splitTag}");
            }
        }

        if (a.ToolFriction.Any(t => t.FailureCount > 0 || t.OversizedOutputCount > 0))
        {
            log($"\n--- Tool Friction & Output Bloat ---");
            foreach (var t in a.ToolFriction.Where(t => t.FailureCount > 0 || t.OversizedOutputCount > 0).Take(5))
            {
                log($"  🧰 {t.ToolName}: {t.InvocationCount} calls, {t.FailureCount} errors ({t.FailureRatePercent:F1}%), {t.OversizedOutputCount} oversized");
            }
        }

        if (a.BashCommandFriction.Any(b => b.FailureCount > 0 || b.HangOrTimeoutCount > 0))
        {
            log($"\n--- Failing & Hanging Bash Commands ---");
            foreach (var b in a.BashCommandFriction.Where(b => b.FailureCount > 0 || b.HangOrTimeoutCount > 0).Take(5))
            {
                log($"  💻 {b.CommandPattern}: {b.FailureCount} fail, {b.HangOrTimeoutCount} hang/timeout (of {b.ExecutionCount})");
            }
        }

        if (a.CompilerErrorBreakdown.Count > 0)
        {
            log($"\n--- Top Compiler Errors ---");
            foreach (var err in a.CompilerErrorBreakdown.Take(5))
            {
                log($"  🧱 {err.DiagnosticCode} ({err.Count}×): {err.SampleMessage}");
            }
        }

        var bSig = a.BehavioralSignals;
        if (bSig.HasAnySignal)
        {
            var slowCount = bSig.SlowToolExecutions.Count + bSig.SlowLlmTurns.Count;
            log($"\n--- Behavioral & Latency Signals ---");
            log($"  🔁 Loops: {bSig.RepeatedToolLoops.Count} | Dead-end searches: {bSig.DeadEndSearchCount} | Test oscillation: {bSig.HasTestOscillation} | Self-reverts: {bSig.SelfRevertCount} | Malformed calls: {bSig.MalformedOrTruncatedToolCount} | Slow turns/tools: {slowCount}");
        }
    }

    private static void RenderCategorizedRecommendations(
        IReadOnlyList<FrictionRecommendation> userRecs,
        IReadOnlyList<FrictionRecommendation> harnessRecs,
        Action<string> log,
        bool isFleet)
    {
        if (userRecs.Count == 0 && harnessRecs.Count == 0)
        {
            return;
        }

        log(isFleet
            ? "\n--- Categorized Fleet Recommendations ---"
            : "\n--- Actionable Recommendations ---");

        if (userRecs.Count > 0)
        {
            log("  [User Codebase]");
            foreach (var rec in userRecs)
            {
                log($"    🏗️ [{rec.Category}] {rec.Title}: {rec.ActionableDetail}");
            }
        }

        if (harnessRecs.Count > 0)
        {
            log("  [NightCodeForge]");
            foreach (var rec in harnessRecs)
            {
                log($"    ⚙️ [{rec.Category}] {rec.Title}: {rec.ActionableDetail}");
            }
        }
    }

    private static void RenderDirectoryAnalysis(
        string dir,
        IReadOnlyList<TranscriptAnalysis> analyses,
        int totalAvailableCount,
        Action<string> log,
        bool verbose)
    {
        log($"================================================================================");
        log($"📊 TRANSCRIPT FLEET ANALYSIS: {dir}");
        log($"================================================================================");
        if (totalAvailableCount > analyses.Count)
        {
            log($"Showing latest {analyses.Count} of {totalAvailableCount} transcript(s)\n");
        }
        else
        {
            log($"Found {analyses.Count} transcript(s)\n");
        }

        log(string.Format("{0,-4} {1,-18} {2,-16} {3,-9} {4,10} {5,12} {6,6}",
            "Task", "Stage", "Status", "Duration", "Tokens", "Tool Calls", "Errs"));
        log(new string('-', 80));

        long totalTokens = 0;
        long totalDurationMs = 0;
        int totalErrors = 0;

        foreach (var a in analyses)
        {
            var fm = a.Frontmatter;
            var taskId = fm?.TaskId?.ToString() ?? "?";
            var stage = fm?.StageId ?? "unknown";
            var status = fm?.Status ?? "unknown";
            var duration = FormatDuration(fm?.DurationMs);
            var tokens = fm?.Tokens != null
                ? (fm.Tokens.Total > 0 ? fm.Tokens.Total : fm.Tokens.Prompt + fm.Tokens.Completion)
                : 0;
            var toolCalls = a.ToolUsage.Count > 0 ? a.ToolUsage.Values.Sum() : a.ToolTurns;
            var errs = a.TotalErrors;

            totalTokens += tokens;
            totalDurationMs += fm?.DurationMs ?? 0;
            totalErrors += errs;

            log(string.Format("{0,-4} {1,-18} {2,-16} {3,-9} {4,10:N0} {5,12} {6,6}",
                taskId,
                stage.Length > 18 ? stage.Substring(0, 15) + "..." : stage,
                status.Length > 16 ? status.Substring(0, 13) + "..." : status,
                duration,
                tokens,
                toolCalls,
                errs > 0 ? $"⚠️ {errs}" : "0"));
        }

        log(new string('-', 80));
        var totalDurationStr = FormatDuration(totalDurationMs);
        log($"TOTALS: {totalTokens:N0} tokens | {totalDurationStr} execution time | {totalErrors} total error(s)\n");

        var topFiles = analyses
            .SelectMany(a => a.FileFriction)
            .Where(f => f.EditCount + f.EditFailureCount + f.ReadCount > 0)
            .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FileFrictionEntry(
                g.Key,
                g.Sum(x => x.ReadCount),
                g.Sum(x => x.EditCount),
                g.Sum(x => x.EditFailureCount),
                g.Sum(x => x.EditedWithoutReadCount),
                g.Max(x => x.ConsecutiveReReadCount)))
            .OrderByDescending(x => x.EditFailureCount * 3 + x.EditCount * 2 + x.ReadCount)
            .Take(verbose ? 10 : 5)
            .ToList();

        if (topFiles.Count > 0)
        {
            log("--- High-Friction Files Across Fleet ---");
            foreach (var f in topFiles)
            {
                var splitTag = f.IsSplitCandidate ? " [split candidate]" : string.Empty;
                log($"  📁 {f.FilePath}: {f.ReadCount}R / {f.EditCount}E / {f.EditFailureCount} fail{splitTag}");
            }
            log(string.Empty);
        }

        var topTools = analyses
            .SelectMany(a => a.ToolFriction)
            .Where(t => t.FailureCount > 0 || t.OversizedOutputCount > 0)
            .GroupBy(t => t.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var calls = g.Sum(x => x.InvocationCount);
                var fails = g.Sum(x => x.FailureCount);
                var oversized = g.Sum(x => x.OversizedOutputCount);
                var rate = calls > 0 ? Math.Round(fails * 100.0 / calls, 1) : 0.0;
                return new ToolFrictionEntry(
                    g.Key,
                    calls,
                    fails,
                    rate,
                    oversized,
                    g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SampleError))?.SampleError);
            })
            .OrderByDescending(t => t.FailureCount)
            .ThenByDescending(t => t.OversizedOutputCount)
            .Take(verbose ? 10 : 5)
            .ToList();

        if (topTools.Count > 0)
        {
            log("--- Tool Friction & Output Bloat Across Fleet ---");
            foreach (var t in topTools)
            {
                log($"  🧰 {t.ToolName}: {t.InvocationCount} calls, {t.FailureCount} errors ({t.FailureRatePercent:F1}%), {t.OversizedOutputCount} oversized");
            }
            log(string.Empty);
        }

        var topBash = analyses
            .SelectMany(a => a.BashCommandFriction)
            .Where(b => b.FailureCount > 0 || b.HangOrTimeoutCount > 0 || b.OversizedOutputCount > 0)
            .GroupBy(b => b.CommandPattern, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BashCommandFrictionEntry(
                g.Key,
                g.Sum(x => x.ExecutionCount),
                g.Sum(x => x.FailureCount),
                g.Sum(x => x.HangOrTimeoutCount),
                g.Sum(x => x.OversizedOutputCount),
                g.FirstOrDefault(x => x.LastExitCode.HasValue)?.LastExitCode))
            .OrderByDescending(b => b.HangOrTimeoutCount * 3 + b.FailureCount * 2 + b.OversizedOutputCount)
            .Take(verbose ? 10 : 5)
            .ToList();

        if (topBash.Count > 0)
        {
            log("--- Failing & Hanging Bash Commands Across Fleet ---");
            foreach (var b in topBash)
            {
                log($"  💻 {b.CommandPattern}: {b.FailureCount} fail / {b.HangOrTimeoutCount} hang");
            }
            log(string.Empty);
        }

        var topCompiler = analyses
            .SelectMany(a => a.CompilerErrorBreakdown)
            .GroupBy(e => e.DiagnosticCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CompilerErrorEntry(
                g.Key,
                g.Sum(x => x.Count),
                g.First().SampleMessage,
                g.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SampleFilePath))?.SampleFilePath))
            .OrderByDescending(e => e.Count)
            .Take(verbose ? 10 : 5)
            .ToList();

        if (topCompiler.Count > 0)
        {
            log("--- Top Compiler Errors Across Fleet ---");
            foreach (var err in topCompiler)
            {
                log($"  🧱 {err.DiagnosticCode} ({err.Count}×): {err.SampleMessage}");
            }
            log(string.Empty);
        }

        // Aggregated friction and recommendations
        var allFriction = analyses.SelectMany(a => a.FrictionPoints).Distinct().ToList();
        var userRecs = analyses
            .SelectMany(a => a.UserCodebaseRecommendations)
            .GroupBy(r => (r.Category, Key: r.Subject ?? r.Title))
            .Select(g => g.First())
            .ToList();
        var harnessRecs = analyses
            .SelectMany(a => a.NightCodeForgeRecommendations)
            .GroupBy(r => (r.Category, Key: r.Subject ?? r.Title))
            .Select(g => g.First())
            .ToList();

        if (allFriction.Count > 0)
        {
            log("--- Key Friction Patterns Across Fleet ---");
            foreach (var fp in allFriction)
            {
                log($"  ⚠️  {fp}");
            }
        }

        RenderCategorizedRecommendations(userRecs, harnessRecs, log, isFleet: true);

        log($"================================================================================\n");
    }

    public static string FormatDuration(long? durationMs) => TranscriptAnalysisService.FormatDuration(durationMs);
}
