using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Exception thrown during stage execution when human clarification is needed,
/// triggering a clean Hangfire job completion and workspace checkpointing.
/// </summary>
public class HumanInterruptionException : Exception
{
    public Question Question { get; }

    public HumanInterruptionException(string prompt, QuestionType questionType = QuestionType.FreeText, string? rationale = null, string? optionsJson = null)
        : base(prompt)
    {
        Question = new Question
        {
            Prompt = prompt,
            QuestionType = questionType,
            Rationale = rationale ?? string.Empty,
            OptionsJson = optionsJson,
            Status = QuestionStatus.Pending,
            AskedAt = DateTime.UtcNow
        };
    }

    public HumanInterruptionException(Question question)
        : base(question?.Prompt ?? "Human clarification required")
    {
        Question = question ?? throw new ArgumentNullException(nameof(question));
    }
}
