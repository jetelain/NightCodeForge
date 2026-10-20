namespace NightCodeForge.Core.Domain;

/// <summary>
/// Structured representation of a human answer to a clarification question.
/// Preserves the question index, selected option (and its zero-based index), and full text response
/// to eliminate fragile text-parsing heuristics across turns.
/// </summary>
public sealed record QuestionAnswerEntry(
    int QuestionIndex,
    int? QuestionId,
    int? SelectedOptionIndex,
    string? SelectedOption,
    string AnswerText);
