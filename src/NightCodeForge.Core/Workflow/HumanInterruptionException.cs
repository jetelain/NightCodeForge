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
        : this(NormalizeQuestions(questions), validateOnly: true)
    {
    }

    private HumanInterruptionException(IReadOnlyList<Question> questions, bool validateOnly)
        : base(FormatMessage(questions))
    {
        Questions = questions;
    }

    public HumanInterruptionException(IReadOnlyList<QuestionItem> items)
        : this(NormalizeToQuestions(items), validateOnly: true)
    {
    }

    private static IReadOnlyList<Question> NormalizeQuestions(IReadOnlyList<Question>? questions)
    {
        if (questions == null || questions.Count == 0)
        {
            throw new ArgumentException("Questions collection cannot be null or empty.", nameof(questions));
        }

        bool needsUnpacking = false;
        foreach (var q in questions)
        {
            if (q != null && !string.IsNullOrWhiteSpace(q.Prompt))
            {
                var trimmed = q.Prompt.TrimStart();
                if (trimmed.StartsWith('[') || trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    needsUnpacking = true;
                    break;
                }
            }
        }

        if (!needsUnpacking)
        {
            return questions;
        }

        var result = new List<Question>();
        foreach (var q in questions)
        {
            if (q == null) continue;

            var items = QuestionItemParser.ToQuestionItems(q);
            if (items.Count == 0)
            {
                result.Add(q);
            }
            else
            {
                foreach (var item in items)
                {
                    result.Add(new Question
                    {
                        Id = q.Id,
                        TaskId = q.TaskId,
                        StageId = q.StageId,
                        AgentProfileId = q.AgentProfileId,
                        BatchId = q.BatchId,
                        ForgeTask = q.ForgeTask,
                        Prompt = item.Prompt,
                        Rationale = item.Rationale ?? q.Rationale ?? string.Empty,
                        OptionsJson = item.Options is { Count: > 0 } ? JsonSerializer.Serialize(item.Options) : q.OptionsJson,
                        QuestionType = (item.Options is { Count: > 0 } || !string.IsNullOrWhiteSpace(q.OptionsJson)) ? QuestionType.SingleChoice : q.QuestionType,
                        Status = q.Status,
                        AskedAt = q.AskedAt != default ? q.AskedAt : DateTime.UtcNow
                    });
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<Question> NormalizeToQuestions(IReadOnlyList<QuestionItem>? items)
    {
        if (items == null || items.Count == 0)
        {
            throw new ArgumentException("Question items collection cannot be null or empty.", nameof(items));
        }

        var normalized = QuestionItemParser.Normalize(items);
        if (normalized.Count == 0)
        {
            throw new ArgumentException("Question items collection cannot be null or empty.", nameof(items));
        }

        return normalized.Select(i => new Question
        {
            Prompt = i.Prompt,
            Rationale = i.Rationale ?? string.Empty,
            OptionsJson = i.Options is { Count: > 0 } ? JsonSerializer.Serialize(i.Options) : null,
            QuestionType = i.Options is { Count: > 0 } ? QuestionType.SingleChoice : QuestionType.FreeText,
            Status = QuestionStatus.Pending,
            AskedAt = DateTime.UtcNow
        }).ToList();
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
