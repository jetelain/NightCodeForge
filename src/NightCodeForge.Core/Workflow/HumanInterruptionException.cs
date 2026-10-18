using System.Text.Json;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Exception thrown during stage execution when human clarification is needed,
/// triggering a clean Hangfire job completion and workspace checkpointing.
/// </summary>
public class HumanInterruptionException : Exception
{
    public IReadOnlyList<Question> Questions { get; }

    public HumanInterruptionException(IReadOnlyList<Question> questions)
        : base(FormatMessage(questions))
    {
        if (questions == null || questions.Count == 0)
        {
            throw new ArgumentException("Questions collection cannot be null or empty.", nameof(questions));
        }
        Questions = questions;
    }

    public HumanInterruptionException(IReadOnlyList<QuestionItem> items)
        : this(items != null && items.Count > 0
            ? items.Select(i => new Question
            {
                Prompt = i.Prompt,
                Rationale = i.Rationale ?? string.Empty,
                OptionsJson = i.Options is { Count: > 0 } ? JsonSerializer.Serialize(i.Options) : null,
                QuestionType = i.Options is { Count: > 0 } ? QuestionType.SingleChoice : QuestionType.FreeText,
                Status = QuestionStatus.Pending,
                AskedAt = DateTime.UtcNow
            }).ToList()
            : throw new ArgumentException("Question items collection cannot be null or empty.", nameof(items)))
    {
    }

    private static string FormatMessage(IReadOnlyList<Question>? questions)
    {
        if (questions == null || questions.Count == 0)
        {
            return "Human clarification required.";
        }

        return string.Join("; ", questions.Select(q => q.Prompt));
    }
}
