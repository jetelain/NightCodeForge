using System.Globalization;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

public sealed record FleetRecommendationItem(
    FrictionRecommendation Recommendation,
    int SessionCount);

public sealed record FleetAggregatedAnalysis(
    long TotalTokens,
    long TotalDurationMs,
    int TotalErrors,
    int FrictionSessionsCount,
    IReadOnlyList<string> AllFriction,
    IReadOnlyList<FleetRecommendationItem> UserCodebaseRecommendations,
    IReadOnlyList<FleetRecommendationItem> NightCodeForgeRecommendations,
    IReadOnlyList<FileFrictionEntry> FileFriction,
    IReadOnlyList<ToolFrictionEntry> ToolFriction,
    IReadOnlyList<BashCommandFrictionEntry> BashCommandFriction,
    IReadOnlyList<CompilerErrorEntry> CompilerErrorBreakdown,
    TranscriptBehavioralSignals BehavioralSignals);

/// <summary>
/// Presentation and fleet-aggregation helper for transcript analysis views,
/// keeping Razor components focused on layout and interaction.
/// </summary>
public static class TranscriptPresentationHelper
{
    private const int MaxFleetFiles = 15;
    private const int MaxFleetSlowTurns = 15;

    public static string FormatWindowBadge(int shownCount, int totalAvailableCount, int? appliedLimit)
    {
        _ = appliedLimit;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Showing latest {shownCount} of {totalAvailableCount} transcripts");
    }

    public static string FormatCategoryBadge(FrictionCategory category) => category switch
    {
        FrictionCategory.FileArchitecture => "File Architecture",
        FrictionCategory.Tooling => "Tooling",
        FrictionCategory.ShellCommand => "Shell Command",
        FrictionCategory.CompilerDiagnostic => "Compiler Diagnostic",
        FrictionCategory.TestSuite => "Test Suite",
        FrictionCategory.PromptOrStage => "Prompt / Stage",
        FrictionCategory.ContextAndCache => "Context & KV-Cache",
        _ => category.ToString()
    };

    public static IReadOnlyList<TranscriptSummary> FilterTranscripts(
        IReadOnlyList<TranscriptSummary> transcripts,
        string? searchTerm,
        string selectedFilter)
    {
        var list = transcripts.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim();
            list = list.Where(t =>
                t.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                t.StageId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                t.ModelName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                t.TaskId.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal));
        }

        if (selectedFilter == "Friction")
        {
            list = list.Where(t => t.HasFriction);
        }
        else if (selectedFilter != "All")
        {
            list = list.Where(t => t.Status.Contains(selectedFilter, StringComparison.OrdinalIgnoreCase));
        }

        return list.ToList();
    }

    public static IReadOnlyList<TranscriptAnalysis> FilterFleet(
        IReadOnlyList<TranscriptAnalysis> fleet,
        string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return fleet;
        }

        var term = searchTerm.Trim();
        return fleet.Where(a =>
            (a.Frontmatter?.TaskId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            $"#{a.Frontmatter?.TaskId}".Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (a.Frontmatter?.StageId ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (a.Frontmatter?.Model ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            a.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (a.Frontmatter?.Status ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static FleetAggregatedAnalysis AggregateFleet(IReadOnlyList<TranscriptAnalysis> fleet)
    {
        long totalTokens = fleet.Sum(a =>
        {
            var t = a.Frontmatter?.Tokens;
            if (t == null) return 0L;
            return t.Total > 0 ? t.Total : (long)(t.Prompt + t.Completion);
        });
        long totalDurationMs = fleet.Sum(a => a.Frontmatter?.DurationMs ?? 0);
        int totalErrors = fleet.Sum(a => a.TotalErrors);
        int frictionSessionsCount = fleet.Count(a => a.HasFriction);
        var allFriction = fleet.SelectMany(a => a.FrictionPoints).Distinct().ToList();

        // Group recommendations by (Target, Category, Subject ?? Title) counting distinct sessions
        var recBuckets = new Dictionary<(RecommendationTarget Target, FrictionCategory Category, string Key), (FrictionRecommendation Rec, int Sessions)>();
        foreach (var session in fleet)
        {
            var seenInSession = new HashSet<(RecommendationTarget, FrictionCategory, string)>();
            foreach (var rec in session.Recommendations)
            {
                var key = (rec.Target, rec.Category, rec.Subject ?? rec.Title);
                if (!seenInSession.Add(key))
                {
                    continue;
                }

                if (recBuckets.TryGetValue(key, out var existing))
                {
                    recBuckets[key] = (existing.Rec, existing.Sessions + 1);
                }
                else
                {
                    recBuckets[key] = (rec, 1);
                }
            }
        }

        var userRecommendations = recBuckets.Values
            .Where(x => x.Rec.Target == RecommendationTarget.UserCodebase)
            .OrderByDescending(x => x.Sessions)
            .ThenBy(x => x.Rec.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => new FleetRecommendationItem(x.Rec, x.Sessions))
            .ToList();

        var ncfRecommendations = recBuckets.Values
            .Where(x => x.Rec.Target == RecommendationTarget.NightCodeForge)
            .OrderByDescending(x => x.Sessions)
            .ThenBy(x => x.Rec.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => new FleetRecommendationItem(x.Rec, x.Sessions))
            .ToList();

        // Merge FileFriction
        var fileBuckets = new Dictionary<string, FileFrictionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fleet.SelectMany(a => a.FileFriction))
        {
            if (fileBuckets.TryGetValue(file.FilePath, out var existing))
            {
                fileBuckets[file.FilePath] = new FileFrictionEntry(
                    existing.FilePath,
                    existing.ReadCount + file.ReadCount,
                    existing.EditCount + file.EditCount,
                    existing.EditFailureCount + file.EditFailureCount,
                    existing.EditedWithoutReadCount + file.EditedWithoutReadCount,
                    Math.Max(existing.ConsecutiveReReadCount, file.ConsecutiveReReadCount));
            }
            else
            {
                fileBuckets[file.FilePath] = file;
            }
        }

        var mergedFiles = fileBuckets.Values
            .OrderByDescending(f => f.EditFailureCount * 3 + f.EditCount * 2 + f.ReadCount)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFleetFiles)
            .ToList();

        // Merge ToolFriction
        var toolBuckets = new Dictionary<string, ToolFrictionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in fleet.SelectMany(a => a.ToolFriction))
        {
            if (toolBuckets.TryGetValue(tool.ToolName, out var existing))
            {
                var totalInvocations = existing.InvocationCount + tool.InvocationCount;
                var totalFailures = existing.FailureCount + tool.FailureCount;
                toolBuckets[tool.ToolName] = new ToolFrictionEntry(
                    existing.ToolName,
                    totalInvocations,
                    totalFailures,
                    totalInvocations > 0 ? Math.Round(totalFailures * 100.0 / totalInvocations, 1) : 0.0,
                    existing.OversizedOutputCount + tool.OversizedOutputCount,
                    existing.SampleError ?? tool.SampleError);
            }
            else
            {
                toolBuckets[tool.ToolName] = tool;
            }
        }

        var mergedTools = toolBuckets.Values
            .OrderByDescending(t => t.FailureCount)
            .ThenByDescending(t => t.OversizedOutputCount)
            .ThenByDescending(t => t.InvocationCount)
            .ToList();

        // Merge BashCommandFriction
        var bashBuckets = new Dictionary<string, BashCommandFrictionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var bash in fleet.SelectMany(a => a.BashCommandFriction))
        {
            if (bashBuckets.TryGetValue(bash.CommandPattern, out var existing))
            {
                bashBuckets[bash.CommandPattern] = new BashCommandFrictionEntry(
                    existing.CommandPattern,
                    existing.ExecutionCount + bash.ExecutionCount,
                    existing.FailureCount + bash.FailureCount,
                    existing.HangOrTimeoutCount + bash.HangOrTimeoutCount,
                    existing.OversizedOutputCount + bash.OversizedOutputCount,
                    existing.LastExitCode ?? bash.LastExitCode);
            }
            else
            {
                bashBuckets[bash.CommandPattern] = bash;
            }
        }

        var mergedBash = bashBuckets.Values
            .Where(b => b.FailureCount > 0 || b.HangOrTimeoutCount > 0 || b.OversizedOutputCount > 0)
            .OrderByDescending(b => b.FailureCount + b.HangOrTimeoutCount * 2 + b.OversizedOutputCount)
            .ThenByDescending(b => b.ExecutionCount)
            .ToList();

        // Merge CompilerErrorBreakdown
        var compilerBuckets = new Dictionary<string, CompilerErrorEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var err in fleet.SelectMany(a => a.CompilerErrorBreakdown))
        {
            if (compilerBuckets.TryGetValue(err.DiagnosticCode, out var existing))
            {
                compilerBuckets[err.DiagnosticCode] = new CompilerErrorEntry(
                    existing.DiagnosticCode,
                    existing.Count + err.Count,
                    existing.SampleMessage,
                    existing.SampleFilePath ?? err.SampleFilePath);
            }
            else
            {
                compilerBuckets[err.DiagnosticCode] = err;
            }
        }

        var mergedCompilerErrors = compilerBuckets.Values
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.DiagnosticCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Merge BehavioralSignals
        var loopBuckets = new Dictionary<(string Tool, string Args), RepeatedToolLoopEntry>();
        foreach (var loop in fleet.SelectMany(a => a.BehavioralSignals.RepeatedToolLoops))
        {
            var key = (loop.ToolName.ToLowerInvariant(), loop.NormalizedArgsSummary);
            if (loopBuckets.TryGetValue(key, out var existing))
            {
                loopBuckets[key] = existing with { RepeatCount = existing.RepeatCount + loop.RepeatCount };
            }
            else
            {
                loopBuckets[key] = loop;
            }
        }

        var mergedLoops = loopBuckets.Values
            .OrderByDescending(l => l.RepeatCount)
            .ToList();

        var mergedFailingTests = fleet
            .SelectMany(a => a.BehavioralSignals.FailingTests)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mergedSlowTools = fleet
            .SelectMany(a => a.BehavioralSignals.SlowToolExecutions)
            .OrderByDescending(s => s.DurationSeconds)
            .Take(MaxFleetSlowTurns)
            .ToList();

        var mergedSlowLlm = fleet
            .SelectMany(a => a.BehavioralSignals.SlowLlmTurns)
            .OrderByDescending(s => s.DurationSeconds)
            .Take(MaxFleetSlowTurns)
            .ToList();

        var mergedSignals = new TranscriptBehavioralSignals(
            mergedLoops,
            fleet.Sum(a => a.BehavioralSignals.DeadEndSearchCount),
            fleet.Sum(a => a.BehavioralSignals.TestRunCycles),
            mergedFailingTests,
            fleet.Any(a => a.BehavioralSignals.HasTestOscillation),
            fleet.Sum(a => a.BehavioralSignals.SelfRevertCount),
            fleet.Sum(a => a.BehavioralSignals.MalformedOrTruncatedToolCount),
            mergedSlowTools,
            mergedSlowLlm);

        return new FleetAggregatedAnalysis(
            totalTokens,
            totalDurationMs,
            totalErrors,
            frictionSessionsCount,
            allFriction,
            userRecommendations,
            ncfRecommendations,
            mergedFiles,
            mergedTools,
            mergedBash,
            mergedCompilerErrors,
            mergedSignals);
    }
}

