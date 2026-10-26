namespace NightCodeForge.Cli.Commands;

using System.Text.Json;
using NightCodeForge.Core.Services;

public class TranscriptOptions
{
    public string TargetPath { get; set; } = Directory.GetCurrentDirectory();
    public bool Verbose { get; set; }
    public bool Json { get; set; }
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
            // If docs/transcripts subdirectory exists, prefer that when given a repo root
            var searchDir = fullPath;
            var docTranscripts = Path.Combine(fullPath, "docs", "transcripts");
            if (Directory.Exists(docTranscripts) && Directory.GetFiles(fullPath, "*.md").Length == 0)
            {
                searchDir = docTranscripts;
            }

            var analyses = await _analysisService.AnalyzeDirectoryAsync(searchDir, cancellationToken);
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

            RenderDirectoryAnalysis(searchDir, analyses, log, options.Verbose);
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
            log($"  Tokens:     {fm.Tokens.Total.ToString("N0", inv)} total ({fm.Tokens.Prompt.ToString("N0", inv)} prompt, {fm.Tokens.Completion.ToString("N0", inv)} completion)");
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

        if (a.FrictionPoints.Count > 0)
        {
            log($"\n--- Friction Points Detected ---");
            foreach (var fp in a.FrictionPoints)
            {
                log($"  ⚠️  {fp}");
            }
        }

        if (a.Recommendations.Count > 0)
        {
            log($"\n--- Actionable Recommendations ---");
            foreach (var rec in a.Recommendations)
            {
                log($"  💡 {rec}");
            }
        }

        log($"================================================================================\n");
    }

    private static void RenderDirectoryAnalysis(string dir, IReadOnlyList<TranscriptAnalysis> analyses, Action<string> log, bool verbose)
    {
        log($"================================================================================");
        log($"📊 TRANSCRIPT FLEET ANALYSIS: {dir}");
        log($"================================================================================");
        log($"Found {analyses.Count} transcript(s)\n");

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
            var tokens = fm?.Tokens?.Total ?? 0;
            var toolCalls = a.ToolTurns;
            var errs = a.EditFailures + a.BashErrors + a.CompilerErrors + a.TestFailures;

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

        // Aggregated friction and recommendations
        var allFriction = analyses.SelectMany(a => a.FrictionPoints).Distinct().ToList();
        var allRecommendations = analyses.SelectMany(a => a.Recommendations).Distinct().ToList();

        if (allFriction.Count > 0)
        {
            log("--- Key Friction Patterns Across Fleet ---");
            foreach (var fp in allFriction)
            {
                log($"  ⚠️  {fp}");
            }
        }

        if (allRecommendations.Count > 0)
        {
            log("\n--- Fleet Recommendations for Prompts & Tooling ---");
            foreach (var rec in allRecommendations)
            {
                log($"  💡 {rec}");
            }
        }

        log($"================================================================================\n");
    }

    public static string FormatDuration(long? durationMs) => TranscriptAnalysisService.FormatDuration(durationMs);
}
