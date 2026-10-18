using System.ComponentModel;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Structured question definition for multi-question clarifications.
/// </summary>
public sealed record QuestionItem(
    [property: Description("The clarification question prompt to ask the human user.")]
    string Prompt,

    [property: Description("Optional background, context, or technical rationale explaining why this clarification is needed.")]
    string? Rationale = null,

    [property: Description("Optional selectable choices; if provided, prompts a single-choice decision. If omitted or empty, prompts free-form text.")]
    IReadOnlyList<string>? Options = null);
