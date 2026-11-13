namespace NightCodeForge.Core.Services;

using System.Globalization;
using System.Text;
using YamlDotNet.Serialization;

public static class TranscriptAnalysisDefaults
{
    public const int DefaultRecentLimit = 50;
    public const int OversizedToolOutputCharThreshold = 10_000;
    public const int SlowToolExecutionThresholdSeconds = 60;
    public const int SlowLlmTurnThresholdSeconds = 30;
    public const int MaxFileFrictionEntriesPerSession = 15;
    public static readonly IReadOnlyList<int> WindowPresets = [25, 50, 100];
}

public sealed record TranscriptWindowResult<T>(
    IReadOnlyList<T> Items,
    int TotalAvailableCount,
    int? AppliedLimit = null);

public enum RecommendationTarget
{
    UserCodebase,
    NightCodeForge
}

public enum FrictionCategory
{
    FileArchitecture,
    Tooling,
    ShellCommand,
    CompilerDiagnostic,
    TestSuite,
    PromptOrStage,
    ContextAndCache
}

public sealed record FrictionRecommendation(
    RecommendationTarget Target,
    FrictionCategory Category,
    string Title,
    string ActionableDetail,
    string? Subject = null);

public sealed record FileFrictionEntry(
    string FilePath,
    int ReadCount,
    int EditCount,
    int EditFailureCount,
    int EditedWithoutReadCount = 0,
    int ConsecutiveReReadCount = 0)
{
    public bool IsSplitCandidate =>
        !TranscriptAnalysisService.IsNonSourceFile(FilePath) &&
        (EditCount >= 3 || EditFailureCount >= 2 || ConsecutiveReReadCount >= 3);
}

public sealed record ToolFrictionEntry(
    string ToolName,
    int InvocationCount,
    int FailureCount,
    double FailureRatePercent,
    int OversizedOutputCount,
    string? SampleError = null);

public sealed record BashCommandFrictionEntry(
    string CommandPattern,
    int ExecutionCount,
    int FailureCount,
    int HangOrTimeoutCount,
    int OversizedOutputCount,
    int? LastExitCode = null);

public sealed record CompilerErrorEntry(
    string DiagnosticCode,
    int Count,
    string SampleMessage,
    string? SampleFilePath = null);

public sealed record RepeatedToolLoopEntry(
    string ToolName,
    string NormalizedArgsSummary,
    int RepeatCount);

public sealed record SlowTurnEntry(
    string StartOffset,
    string EndOffset,
    string Label,
    int DurationSeconds);

public sealed record TranscriptBehavioralSignals(
    IReadOnlyList<RepeatedToolLoopEntry> RepeatedToolLoops,
    int DeadEndSearchCount,
    int TestRunCycles,
    IReadOnlyList<string> FailingTests,
    bool HasTestOscillation,
    int SelfRevertCount,
    int MalformedOrTruncatedToolCount,
    IReadOnlyList<SlowTurnEntry> SlowToolExecutions,
    IReadOnlyList<SlowTurnEntry> SlowLlmTurns)
{
    public static readonly TranscriptBehavioralSignals Empty = new(
        Array.Empty<RepeatedToolLoopEntry>(),
        0,
        0,
        Array.Empty<string>(),
        false,
        0,
        0,
        Array.Empty<SlowTurnEntry>(),
        Array.Empty<SlowTurnEntry>());

    public bool HasAnySignal =>
        RepeatedToolLoops.Count > 0 ||
        DeadEndSearchCount > 0 ||
        HasTestOscillation ||
        SelfRevertCount > 0 ||
        MalformedOrTruncatedToolCount > 0 ||
        SlowToolExecutions.Count > 0 ||
        SlowLlmTurns.Count > 0;
}

public sealed record WorkspaceFrictionHotspots(
    int AnalyzedSessionCount,
    IReadOnlyList<FileFrictionEntry> SplitCandidateFiles,
    IReadOnlyList<CompilerErrorEntry> TopCompilerErrors,
    IReadOnlyList<string> RecurringFailingTests)
{
    public static readonly WorkspaceFrictionHotspots Empty = new(
        0,
        Array.Empty<FileFrictionEntry>(),
        Array.Empty<CompilerErrorEntry>(),
        Array.Empty<string>());

    public bool HasHotspots =>
        SplitCandidateFiles.Count > 0 ||
        TopCompilerErrors.Count > 0 ||
        RecurringFailingTests.Count > 0;

    public string FormatPromptDigest()
    {
        if (!HasHotspots)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"<workspace_friction_hotspots analyzed_sessions=\"{AnalyzedSessionCount}\">");

        if (SplitCandidateFiles.Count > 0)
        {
            sb.AppendLine("High-churn split-candidate files (consider modularizing or scoping changes carefully):");
            foreach (var file in SplitCandidateFiles)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- {file.FilePath} ({file.EditCount} edits, {file.EditFailureCount} edit failures, {file.ConsecutiveReReadCount} consecutive re-reads)");
            }
        }

        if (TopCompilerErrors.Count > 0)
        {
            sb.AppendLine("Recurring compiler diagnostics:");
            foreach (var err in TopCompilerErrors)
            {
                var locationSuffix = string.IsNullOrWhiteSpace(err.SampleFilePath) ? string.Empty : $" in {err.SampleFilePath}";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {err.DiagnosticCode} ({err.Count}x{locationSuffix}): {err.SampleMessage}");
            }
        }

        if (RecurringFailingTests.Count > 0)
        {
            sb.AppendLine("Recurring failing tests:");
            foreach (var test in RecurringFailingTests)
            {
                sb.AppendLine($"- {test}");
            }
        }

        sb.Append("</workspace_friction_hotspots>");
        return sb.ToString();
    }
}

public class TranscriptTokensFrontmatter
{
    [YamlMember(Alias = "prompt")]
    public int Prompt { get; set; }

    [YamlMember(Alias = "completion")]
    public int Completion { get; set; }

    [YamlMember(Alias = "total")]
    public int Total { get; set; }
}

public class TranscriptFrontmatter
{
    [YamlMember(Alias = "task_id")]
    public int? TaskId { get; set; }

    [YamlMember(Alias = "stage_id")]
    public string? StageId { get; set; }

    [YamlMember(Alias = "agent_profile")]
    public string? AgentProfile { get; set; }

    [YamlMember(Alias = "model")]
    public string? Model { get; set; }

    [YamlMember(Alias = "duration_ms")]
    public long? DurationMs { get; set; }

    [YamlMember(Alias = "tokens")]
    public TranscriptTokensFrontmatter? Tokens { get; set; }

    [YamlMember(Alias = "status")]
    public string? Status { get; set; }

    [YamlMember(Alias = "optimizations")]
    public Dictionary<string, object>? Optimizations { get; set; }

    [YamlMember(Alias = "compaction")]
    public Dictionary<string, object>? Compaction { get; set; }
}

public record TranscriptAnalysis(
    string FilePath,
    string FileName,
    TranscriptFrontmatter? Frontmatter,
    int TotalTurns,
    int UserTurns,
    int AssistantTurns,
    int ToolTurns,
    int ThinkingTurns,
    IReadOnlyDictionary<string, int> ToolUsage,
    int BashErrors,
    int EditFailures,
    int ToolErrors,
    int CompilerErrors,
    int TestFailures,
    int CompactionEvents,
    int HistoryRewrites,
    IReadOnlyList<KeyValuePair<string, int>> TopReadFiles,
    IReadOnlyList<KeyValuePair<string, int>> TopEditedFiles,
    IReadOnlyList<KeyValuePair<string, int>> HighChurnFiles,
    IReadOnlyList<string> FrictionPoints,
    IReadOnlyList<FrictionRecommendation> Recommendations,
    IReadOnlyList<FileFrictionEntry>? FileFriction = null,
    IReadOnlyList<ToolFrictionEntry>? ToolFriction = null,
    IReadOnlyList<BashCommandFrictionEntry>? BashCommandFriction = null,
    IReadOnlyList<CompilerErrorEntry>? CompilerErrorBreakdown = null,
    TranscriptBehavioralSignals? BehavioralSignals = null)
{
    public IReadOnlyList<FileFrictionEntry> FileFriction { get; init; } = FileFriction ?? Array.Empty<FileFrictionEntry>();
    public IReadOnlyList<ToolFrictionEntry> ToolFriction { get; init; } = ToolFriction ?? Array.Empty<ToolFrictionEntry>();
    public IReadOnlyList<BashCommandFrictionEntry> BashCommandFriction { get; init; } = BashCommandFriction ?? Array.Empty<BashCommandFrictionEntry>();
    public IReadOnlyList<CompilerErrorEntry> CompilerErrorBreakdown { get; init; } = CompilerErrorBreakdown ?? Array.Empty<CompilerErrorEntry>();
    public TranscriptBehavioralSignals BehavioralSignals { get; init; } = BehavioralSignals ?? TranscriptBehavioralSignals.Empty;

    public IReadOnlyList<FrictionRecommendation> UserCodebaseRecommendations =>
        Recommendations.Where(r => r.Target == RecommendationTarget.UserCodebase).ToList();

    public IReadOnlyList<FrictionRecommendation> NightCodeForgeRecommendations =>
        Recommendations.Where(r => r.Target == RecommendationTarget.NightCodeForge).ToList();

    public int TotalErrors => BashErrors + EditFailures + ToolErrors + CompilerErrors + TestFailures;
    public bool HasFriction =>
        FrictionPoints.Count > 0 ||
        Recommendations.Count > 0 ||
        EditFailures > 0 ||
        ToolErrors > 0 ||
        BashErrors > 0 ||
        CompilerErrors > 0 ||
        TestFailures > 0 ||
        CompactionEvents > 5 ||
        HistoryRewrites > 10 ||
        BehavioralSignals.HasAnySignal;
}
