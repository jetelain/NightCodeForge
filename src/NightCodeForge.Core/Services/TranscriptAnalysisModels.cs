namespace NightCodeForge.Core.Services;

using YamlDotNet.Serialization;

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
    IReadOnlyList<string> Recommendations)
{
    public int TotalErrors => BashErrors + EditFailures + ToolErrors + CompilerErrors + TestFailures;
    public bool HasFriction => FrictionPoints.Count > 0 || EditFailures > 0 || ToolErrors > 0 || BashErrors > 0 || CompilerErrors > 0 || TestFailures > 0 || CompactionEvents > 5 || HistoryRewrites > 10;
}
