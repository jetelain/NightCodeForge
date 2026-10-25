using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Helper and presentation service for Ideation Chat question mapping, answers dispatch, and history turns.
/// </summary>
public static class IdeationPresentationHelper
{
    /// <summary>
    /// Maps a batch of live IdeationQuestion objects into a list of domain QuestionItem records.
    /// </summary>
    public static IReadOnlyList<QuestionItem> MapToQuestionItems(IEnumerable<IdeationQuestion>? questions)
    {
        if (questions == null) return Array.Empty<QuestionItem>();
        return questions.Select(q => new QuestionItem(q.Prompt, q.Rationale, q.Options)).ToList();
    }

    /// <summary>
    /// Delivers answered texts to each pending IdeationQuestion in sequence.
    /// </summary>
    public static void SubmitAnswersToPendingQuestions(
        IReadOnlyList<IdeationQuestion>? pendingQuestions,
        IReadOnlyDictionary<int, string> answersByIndex)
    {
        if (pendingQuestions == null || pendingQuestions.Count == 0 || answersByIndex == null)
            return;

        for (int i = 0; i < pendingQuestions.Count; i++)
        {
            if (answersByIndex.TryGetValue(i, out var ans) && !string.IsNullOrWhiteSpace(ans))
            {
                pendingQuestions[i].Submit(ans.Trim());
            }
            else
            {
                pendingQuestions[i].Submit("(No answer provided)");
            }
        }
    }

    /// <summary>
    /// Computes the single formatted answer and structured metadata JSON for a question submission.
    /// </summary>
    public static (string FormattedAnswer, string MetadataJson) FormatSubmission(
        IReadOnlyDictionary<int, string> answersByIndex,
        IReadOnlyList<QuestionItem>? questions = null)
    {
        var formatted = StageConversationParser.FormatSingleSubmissionAnswer(answersByIndex);
        var metadata = StageConversationParser.BuildStructuredAnswersJson(answersByIndex, questions);
        return (formatted, metadata);
    }

    /// <summary>
    /// Gets the user-facing submit button label according to the number of questions.
    /// </summary>
    public static string GetSubmitButtonLabel(int questionCount)
    {
        return questionCount > 1
            ? $"Submit All Clarifications ({questionCount}) ➔"
            : "Submit & Continue Ideation ➔";
    }
}
