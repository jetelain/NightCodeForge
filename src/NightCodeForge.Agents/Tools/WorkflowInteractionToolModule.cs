using System.ComponentModel;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Composable tool module encapsulating workflow interaction and turn-interruption tools (<c>ask</c>, <c>request_changes</c>).
/// Supports both in-place interactive handlers (Ideation) and terminal turn-unwinding via <see cref="IToolTurnInterruptionController"/> (Crucible stages).
/// </summary>
public sealed class WorkflowInteractionToolModule : IAgentToolModule
{
    public const string MissingModuleInstanceMessage = IAgentToolModule.MissingBoundModuleInstanceMessage;

    /// <summary>
    /// Optional interactive handler for the <c>ask</c> tool (e.g. Ideation session awaiting live human answers).
    /// When null, <c>ask</c> triggers a terminal interruption via <see cref="InterruptionController"/> or throws <see cref="HumanInterruptionException"/>.
    /// </summary>
    public AskHandler? AskHandler { get; set; }

    /// <summary>
    /// Optional callback invoked when <c>request_changes</c> is called.
    /// </summary>
    public Action<string, string?>? RequestChangesHandler { get; set; }

    /// <summary>
    /// Optional unified turn-interruption controller that unwinds the active LLM tool loop upon a terminal tool call.
    /// </summary>
    public IToolTurnInterruptionController? InterruptionController { get; set; }

    public WorkflowInteractionToolModule()
    {
    }

    public WorkflowInteractionToolModule(IToolTurnInterruptionController interruptionController)
    {
        InterruptionController = interruptionController ?? throw new ArgumentNullException(nameof(interruptionController));
    }

    public IReadOnlyList<AIFunction> CreateFunctions() => CreateFunctions(this);

    public static IReadOnlyList<AIFunction> CreateFunctions(WorkflowInteractionToolModule? module) =>
    [
        AIFunctionFactory.Create(
            ([Description("Consolidate ALL clarifying questions into this single list. Each question must include a prompt, with optional technical rationale and choice options.")]
             IReadOnlyList<QuestionItem>? questions = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.AskAsync(questions ?? Array.Empty<QuestionItem>(), ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.Ask,
            "Asks clarifying questions to the human user when blocked. Consolidate all requirements, ambiguities, and architectural questions into a single batched call."),

        AIFunctionFactory.Create(
            ([Description("Detailed critique and rework feedback for the implementation agent")] string feedback,
             [Description("Summary reason for rejection")] string? reason = null) =>
                module != null
                    ? module.RequestChanges(feedback, reason)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.RequestChanges,
            "Rejects implementation and sends task back to the implementation agent for rework.")
    ];

    public async Task<string> AskAsync(IReadOnlyList<QuestionItem>? questions, CancellationToken cancellationToken = default)
    {
        var effectiveQuestions = QuestionItemParser.Normalize(questions);

        if (effectiveQuestions == null || effectiveQuestions.Count == 0)
        {
            return "Error: At least one question must be provided in 'questions'. Consolidate all clarifying questions into a list.";
        }

        for (int i = 0; i < effectiveQuestions.Count; i++)
        {
            var q = effectiveQuestions[i];
            if (string.IsNullOrWhiteSpace(q.Prompt))
            {
                return $"Error: Question at index {i} has a blank or empty prompt.";
            }
        }

        if (AskHandler != null)
        {
            return await AskHandler(effectiveQuestions, cancellationToken).ConfigureAwait(false);
        }

        var domainInterruption = new HumanInterruptionException(effectiveQuestions);
        if (InterruptionController != null)
        {
            InterruptionController.TriggerTerminalInterruption(new HumanClarificationInterruption(domainInterruption.Questions));
        }

        throw domainInterruption;
    }

    public string RequestChanges(string feedback, string? reason = null)
    {
        RequestChangesHandler?.Invoke(feedback, reason);

        if (InterruptionController != null)
        {
            InterruptionController.TriggerTerminalInterruption(new ReviewerChangesRequestedInterruption(feedback, reason));
        }

        throw new RequestChangesException(feedback, reason);
    }
}

