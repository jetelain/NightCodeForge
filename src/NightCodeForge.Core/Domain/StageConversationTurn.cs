namespace NightCodeForge.Core.Domain;

/// <summary>
/// An individual turn or interaction within a stage conversation (prompt, response, thinking trace, tool invocation, optimization action).
/// </summary>
public class StageConversationTurn
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public int TurnIndex { get; set; }
    public StageTurnRole Role { get; set; } = StageTurnRole.User;
    public string? Content { get; set; }
    public string? ThinkingTrace { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArguments { get; set; }
    public string? ToolResult { get; set; }
    public long? DurationMs { get; set; }
    public bool IsError { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public StageConversation? Conversation { get; set; }
}
