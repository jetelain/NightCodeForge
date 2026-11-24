using System.ComponentModel;
using System.Text.Json;
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

    public static IReadOnlyList<AIFunction> CreateFunctions(WorkflowInteractionToolModule? module)
    {
        var rawAskFn = AIFunctionFactory.Create(
            ([Description("Consolidate ALL clarifying questions into this single list. Each question must include a prompt, with optional technical rationale and choice options.")]
             IReadOnlyList<QuestionItem>? questions = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.AskAsync(questions ?? Array.Empty<QuestionItem>(), ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.Ask,
            "Asks clarifying questions to the human user when blocked. Consolidate all requirements, ambiguities, and architectural questions into a single batched call.");

        var askFn = new LenientAskAIFunction(DiagnosticBindingAIFunction.Wrap(rawAskFn));

        var requestChangesFn = DiagnosticBindingAIFunction.Wrap(AIFunctionFactory.Create(
            ([Description("Detailed critique and rework feedback for the implementation agent")] string feedback,
             [Description("Summary reason for rejection")] string? reason = null) =>
                module != null
                    ? module.RequestChanges(feedback, reason)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.RequestChanges,
            "Rejects implementation and sends task back to the implementation agent for rework."));

        return [askFn, requestChangesFn];
    }

    private sealed class LenientAskAIFunction : DelegatingAIFunction
    {
        private const string QuestionsParameterName = "questions";

        public LenientAskAIFunction(AIFunction innerFunction)
            : base(innerFunction)
        {
        }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (TryFindQuestionsArgument(arguments, out var matchedKey, out var rawQuestions))
            {
                object? effectiveQuestions = null;
                bool shouldRewrite = false;

                if (IsCandidateForLenientQuestionParsing(rawQuestions))
                {
                    if (QuestionItemParser.TryBindAskQuestions(rawQuestions, out var boundQuestions))
                    {
                        effectiveQuestions = boundQuestions;
                        shouldRewrite = true;
                    }
                }
                else if (rawQuestions is IEnumerable<QuestionItem> typed)
                {
                    effectiveQuestions = QuestionItemParser.Normalize(typed);
                    shouldRewrite = !string.Equals(matchedKey, QuestionsParameterName, StringComparison.Ordinal);
                }

                if (!shouldRewrite &&
                    !string.Equals(matchedKey, QuestionsParameterName, StringComparison.Ordinal) &&
                    !arguments.ContainsKey(QuestionsParameterName))
                {
                    effectiveQuestions = rawQuestions;
                    shouldRewrite = true;
                }

                if (shouldRewrite)
                {
                    var normalizedArguments = new AIFunctionArguments
                    {
                        Services = arguments.Services,
                        Context = arguments.Context
                    };
                    foreach (var pair in arguments)
                    {
                        if (!string.Equals(pair.Key, matchedKey, StringComparison.Ordinal))
                        {
                            normalizedArguments[pair.Key] = pair.Value;
                        }
                    }

                    normalizedArguments[QuestionsParameterName] = effectiveQuestions;
                    return base.InvokeCoreAsync(normalizedArguments, cancellationToken);
                }
            }

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }

        private static bool TryFindQuestionsArgument(
            AIFunctionArguments arguments,
            out string matchedKey,
            out object? rawQuestions)
        {
            foreach (var pair in arguments)
            {
                if (string.Equals(pair.Key, QuestionsParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = pair.Key;
                    rawQuestions = pair.Value;
                    return true;
                }
            }

            matchedKey = QuestionsParameterName;
            rawQuestions = null;
            return false;
        }

        private static bool IsCandidateForLenientQuestionParsing(object? rawQuestions) =>
            rawQuestions switch
            {
                null => false,
                IReadOnlyList<QuestionItem> => false,
                IEnumerable<QuestionItem> => false,
                JsonElement je => je.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.String,
                IDictionary<string, object?> => true,
                IEnumerable<object?> => true,
                string => true,
                _ => false
            };
    }

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

