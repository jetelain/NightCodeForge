namespace NightCodeForge.Core.Domain;

/// <summary>
/// Immutable record capturing deterministic stage execution outputs used to pre-seed
/// downstream agent conversation history (Synthetic Conversation Priming).
/// </summary>
public sealed record SyntheticPrimingExecution(
    string SourceStageId,
    string ToolName = CanonicalToolNames.Bash,
    string Command = "",
    string Output = "",
    bool IsSuccess = true,
    long? DurationMs = null);
