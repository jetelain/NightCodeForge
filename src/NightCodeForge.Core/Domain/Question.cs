namespace NightCodeForge.Core.Domain;

/// <summary>
/// Human clarification question posed by an agent during a workflow stage.
/// </summary>
public class Question
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string StageId { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public QuestionType QuestionType { get; set; } = QuestionType.FreeText;
    public string? OptionsJson { get; set; }
    public string? Answer { get; set; }
    public DateTime AskedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredAt { get; set; }
    public QuestionStatus Status { get; set; } = QuestionStatus.Pending;
    public string? AgentProfileId { get; set; }

    public ForgeTask? ForgeTask { get; set; }
}
