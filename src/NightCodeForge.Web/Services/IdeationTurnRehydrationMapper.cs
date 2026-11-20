using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Result of projecting persisted <see cref="ConversationTurn"/> records into Web presentation models.
/// </summary>
public sealed record ProjectedIdeationConversation(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<IdeationProposal> Proposals);

/// <summary>
/// Result of projecting a live <see cref="IdeationInFlightTurnSnapshot"/> into Web presentation buffers.
/// </summary>
public sealed record ProjectedIdeationInFlightState(
    string ThinkingBuffer,
    string MessageBuffer,
    IReadOnlyList<IdeationToolCallViewModel> ToolCalls,
    IReadOnlyList<ChatContentPart> Parts,
    IReadOnlyList<IdeationQuestion> PendingQuestions);

/// <summary>
/// Maps Core domain <see cref="Conversation"/> turns and live <see cref="IdeationInFlightTurnSnapshot"/>
/// payloads into Blazor presentation view models, keeping parsing and rehydration outside Razor components.
/// </summary>
public static class IdeationTurnRehydrationMapper
{
    private const string UserAnsweredPrefix = "User answered: ";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string? SerializeUserAttachmentMetadata(IReadOnlyList<ConversationAttachmentChipViewModel>? chips)
    {
        if (chips == null || chips.Count == 0)
        {
            return null;
        }

        var attachmentIds = chips
            .Where(c => c.AttachmentId is > 0)
            .Select(c => c.AttachmentId!.Value)
            .Distinct()
            .ToList();

        return attachmentIds.Count > 0
            ? JsonSerializer.Serialize(new { AttachmentIds = attachmentIds })
            : null;
    }

    public static ProjectedIdeationConversation ProjectConversation(
        Conversation conversation,
        IReadOnlyList<TaskAttachment>? workspaceAttachments,
        IReadOnlyList<ForgeTask>? workspaceTasks,
        int? maxTurnIndexInclusive,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(logger);

        var messages = new List<ChatMessage>();
        var discoveredProposals = new List<IdeationProposal>();
        var pendingTools = new List<IdeationToolCallViewModel>();
        var pendingParts = new List<ChatContentPart>();
        bool sawThinkingTurn = false;

        void FlushPendingAssistantParts(DateTime? fallbackTimestamp)
        {
            if (pendingParts.Count == 0 && pendingTools.Count == 0)
            {
                return;
            }

            var thinkingSummary = JoinThinkingParts(pendingParts);
            messages.Add(new ChatMessage(
                "assistant",
                string.Empty,
                string.IsNullOrWhiteSpace(thinkingSummary) ? null : thinkingSummary,
                fallbackTimestamp ?? DateTime.UtcNow,
                pendingTools.Count > 0 ? new List<IdeationToolCallViewModel>(pendingTools) : null,
                pendingParts.Count > 0 ? new List<ChatContentPart>(pendingParts) : null));

            pendingTools.Clear();
            pendingParts.Clear();
            sawThinkingTurn = false;
        }

        var orderedTurns = (conversation.Turns ?? Enumerable.Empty<ConversationTurn>())
            .Where(t => !maxTurnIndexInclusive.HasValue || t.TurnIndex <= maxTurnIndexInclusive.Value)
            .OrderBy(t => t.TurnIndex);

        foreach (var turn in orderedTurns)
        {
            switch (turn.Role)
            {
                case StageTurnRole.User:
                    FlushPendingAssistantParts(turn.Timestamp);
                    var restoredChips = ResolveUserTurnChips(turn, workspaceAttachments);
                    messages.Add(new ChatMessage(
                        "user",
                        turn.Content ?? string.Empty,
                        Timestamp: turn.Timestamp,
                        Parts: [new TextContentPart(turn.Content ?? string.Empty)],
                        AttachmentChips: restoredChips.Count > 0 ? restoredChips : null));
                    break;

                case StageTurnRole.Thinking:
                    var thinkingText = turn.ThinkingTrace ?? turn.Content;
                    if (!string.IsNullOrEmpty(thinkingText))
                    {
                        if (pendingParts.LastOrDefault() is ThinkingContentPart existingThink)
                        {
                            pendingParts[^1] = new ThinkingContentPart(existingThink.Thinking + thinkingText);
                        }
                        else
                        {
                            pendingParts.Add(new ThinkingContentPart(thinkingText));
                        }
                        sawThinkingTurn = true;
                    }
                    break;

                case StageTurnRole.Tool:
                    ProjectToolCallTurn(turn, pendingTools, pendingParts);
                    break;

                case StageTurnRole.ToolResult:
                    ProjectToolResultTurn(turn, pendingTools, pendingParts);
                    break;

                case StageTurnRole.Optimization:
                    ProjectOptimizationTurn(turn, pendingParts);
                    break;

                case StageTurnRole.Assistant:
                    ProjectAssistantTurn(
                        turn,
                        pendingTools,
                        pendingParts,
                        ref sawThinkingTurn,
                        messages,
                        discoveredProposals,
                        logger);
                    break;
            }
        }

        FlushPendingAssistantParts(DateTime.UtcNow);
        return new ProjectedIdeationConversation(messages, discoveredProposals);
    }

    public static ProjectedIdeationInFlightState ProjectInFlightSnapshot(IdeationInFlightTurnSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var toolViewModels = new Dictionary<string, IdeationToolCallViewModel>(StringComparer.Ordinal);
        var toolCalls = new List<IdeationToolCallViewModel>(snapshot.CurrentToolCalls.Count);

        foreach (var tc in snapshot.CurrentToolCalls)
        {
            var vm = new IdeationToolCallViewModel
            {
                ToolCallId = tc.Info.ToolCallId,
                ToolName = tc.Info.ToolName,
                Arguments = tc.Info.Arguments,
                Result = tc.Info.Result,
                IsCompleted = tc.IsCompleted,
                IsError = tc.Info.IsError,
                DurationMs = tc.Info.DurationMs
            };
            toolCalls.Add(vm);
            if (!string.IsNullOrEmpty(vm.ToolCallId))
            {
                toolViewModels[vm.ToolCallId] = vm;
            }
        }

        var parts = new List<ChatContentPart>(snapshot.CurrentParts.Count);
        var pendingQuestions = new List<IdeationQuestion>();

        foreach (var part in snapshot.CurrentParts)
        {
            switch (part.Kind)
            {
                case IdeationInFlightPartKind.Thinking when !string.IsNullOrEmpty(part.Text):
                    parts.Add(new ThinkingContentPart(part.Text));
                    break;

                case IdeationInFlightPartKind.Text when !string.IsNullOrEmpty(part.Text):
                    parts.Add(new TextContentPart(part.Text));
                    break;

                case IdeationInFlightPartKind.ToolCall when part.ToolCall != null:
                    var tcId = part.ToolCall.ToolCallId ?? string.Empty;
                    if (string.IsNullOrEmpty(tcId) || !toolViewModels.TryGetValue(tcId, out var vm))
                    {
                        vm = new IdeationToolCallViewModel
                        {
                            ToolCallId = tcId,
                            ToolName = part.ToolCall.ToolName,
                            Arguments = part.ToolCall.Arguments,
                            Result = part.ToolCall.Result,
                            IsCompleted = part.IsToolCompleted,
                            IsError = part.ToolCall.IsError,
                            DurationMs = part.ToolCall.DurationMs
                        };
                        if (!string.IsNullOrEmpty(tcId))
                        {
                            toolViewModels[tcId] = vm;
                        }
                    }
                    parts.Add(new ToolCallContentPart(vm));
                    break;

                case IdeationInFlightPartKind.Question:
                    var mappedQuestions = IdeationPresentationHelper.MapToQuestionItems(part.Questions);
                    var qp = new QuestionContentPart
                    {
                        GroupId = part.QuestionGroupId ?? Guid.NewGuid(),
                        Questions = mappedQuestions,
                        PendingQuestions = part.Questions,
                        Answer = part.QuestionAnswer,
                        MetadataJson = part.QuestionMetadataJson
                    };
                    parts.Add(qp);
                    if (!qp.IsAnswered && part.Questions is { Count: > 0 })
                    {
                        pendingQuestions.AddRange(part.Questions.Where(q => !q.IsAnswered));
                    }
                    break;

                case IdeationInFlightPartKind.Optimization:
                    parts.Add(new OptimizationContentPart(
                        part.Text ?? "Context compacted",
                        part.PreCompactionTokens,
                        part.PostCompactionTokens,
                        part.DurationMs));
                    break;
            }
        }

        return new ProjectedIdeationInFlightState(
            snapshot.CurrentThinkingBuffer,
            snapshot.CurrentMessageBuffer,
            toolCalls,
            parts,
            pendingQuestions);
    }

    private static IReadOnlyList<ConversationAttachmentChipViewModel> ResolveUserTurnChips(
        ConversationTurn turn,
        IReadOnlyList<TaskAttachment>? workspaceAttachments)
    {
        var result = new List<ConversationAttachmentChipViewModel>();
        if (workspaceAttachments is { Count: > 0 })
        {
            foreach (var id in ExtractAttachmentIds(turn.MetadataJson))
            {
                var att = workspaceAttachments.FirstOrDefault(a => a.Id == id);
                if (att != null)
                {
                    AttachmentPresentationHelper.UpsertChip(result, ConversationAttachmentChipViewModel.FromAttachment(att));
                }
            }
        }

        var inlineChips = AttachmentPresentationHelper.ResolveRestoredMessageChips(turn.Content, workspaceAttachments);
        foreach (var chip in inlineChips)
        {
            AttachmentPresentationHelper.UpsertChip(result, chip);
        }

        return result;
    }

    private static IReadOnlyList<int> ExtractAttachmentIds(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return Array.Empty<int>();
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("AttachmentIds", out var prop) ||
                 doc.RootElement.TryGetProperty("attachmentIds", out prop)) &&
                prop.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<int>();
                foreach (var el in prop.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var id) && id > 0)
                    {
                        ids.Add(id);
                    }
                }
                return ids;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed attachment metadata
        }

        return Array.Empty<int>();
    }

    private static void ProjectToolCallTurn(
        ConversationTurn turn,
        List<IdeationToolCallViewModel> pendingTools,
        List<ChatContentPart> pendingParts)
    {
        var callId = StageConversationParser.ExtractCallId(turn);
        if (string.Equals(turn.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
        {
            var questions = StageConversationParser.ExtractQuestions(turn);
            pendingParts.Add(new QuestionContentPart
            {
                ToolCallId = callId,
                Questions = questions,
                Answer = string.IsNullOrWhiteSpace(turn.ToolResult)
                    ? null
                    : ResolveCleanQuestionAnswer(turn.ToolResult, turn.MetadataJson),
                MetadataJson = turn.MetadataJson
            });
            return;
        }

        var vm = new IdeationToolCallViewModel
        {
            ToolCallId = !string.IsNullOrWhiteSpace(callId) ? callId : Guid.NewGuid().ToString("N")[..8],
            ToolName = turn.ToolName ?? "tool",
            Arguments = turn.ToolArguments ?? string.Empty,
            Result = turn.ToolResult ?? turn.Content,
            IsCompleted = !string.IsNullOrEmpty(turn.ToolResult),
            IsError = turn.IsError,
            DurationMs = turn.DurationMs ?? 0
        };
        pendingTools.Add(vm);
        pendingParts.Add(new ToolCallContentPart(vm));
    }

    private static void ProjectToolResultTurn(
        ConversationTurn turn,
        List<IdeationToolCallViewModel> pendingTools,
        List<ChatContentPart> pendingParts)
    {
        var callId = StageConversationParser.ExtractCallId(turn);
        var resultPayload = turn.ToolResult ?? turn.Content;

        if (string.Equals(turn.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
        {
            var existingQPart = !string.IsNullOrWhiteSpace(callId)
                ? pendingParts.OfType<QuestionContentPart>().FirstOrDefault(q =>
                    string.Equals(q.ToolCallId, callId, StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(q.Answer))
                : null;

            existingQPart ??= pendingParts.OfType<QuestionContentPart>()
                .FirstOrDefault(q => string.IsNullOrWhiteSpace(q.Answer));

            var cleanAnswer = ResolveCleanQuestionAnswer(resultPayload, turn.MetadataJson);
            if (existingQPart != null)
            {
                existingQPart.Answer = cleanAnswer;
                if (!string.IsNullOrWhiteSpace(turn.MetadataJson))
                {
                    existingQPart.MetadataJson = turn.MetadataJson;
                }
            }
            else
            {
                var questions = StageConversationParser.ExtractQuestions(turn);
                pendingParts.Add(new QuestionContentPart
                {
                    ToolCallId = callId,
                    Questions = questions,
                    Answer = cleanAnswer,
                    MetadataJson = turn.MetadataJson
                });
            }
            return;
        }

        var existingTool = !string.IsNullOrWhiteSpace(callId)
            ? pendingTools.FirstOrDefault(t => !t.IsCompleted && string.Equals(t.ToolCallId, callId, StringComparison.Ordinal))
            : null;
        existingTool ??= pendingTools.FirstOrDefault(t =>
            !t.IsCompleted &&
            string.Equals(t.ToolName, turn.ToolName, StringComparison.OrdinalIgnoreCase));

        if (existingTool != null)
        {
            existingTool.Result = resultPayload;
            existingTool.IsCompleted = true;
            existingTool.IsError = turn.IsError;
            existingTool.DurationMs = turn.DurationMs ?? 0;
        }
        else
        {
            var vm = new IdeationToolCallViewModel
            {
                ToolCallId = !string.IsNullOrWhiteSpace(callId) ? callId : Guid.NewGuid().ToString("N")[..8],
                ToolName = turn.ToolName ?? "tool",
                Arguments = turn.ToolArguments ?? string.Empty,
                Result = resultPayload,
                IsCompleted = true,
                IsError = turn.IsError,
                DurationMs = turn.DurationMs ?? 0
            };
            pendingTools.Add(vm);
            pendingParts.Add(new ToolCallContentPart(vm));
        }
    }

    private static void ProjectOptimizationTurn(ConversationTurn turn, List<ChatContentPart> pendingParts)
    {
        var checkpointMeta = ConversationHistorySerializer.ParseCompactionCheckpointMetadata(turn.MetadataJson);
        if (checkpointMeta != null)
        {
            pendingParts.Add(new OptimizationContentPart(
                turn.Content ?? "Context compacted",
                checkpointMeta.PreCompactionTokens,
                checkpointMeta.PostCompactionTokens,
                checkpointMeta.DurationMs ?? turn.DurationMs));
            return;
        }

        var (_, description, _, _) = StageConversationParser.ParseOptimizationPayload(turn);
        pendingParts.Add(new OptimizationContentPart(
            description,
            DurationMs: turn.DurationMs));
    }

    private static void ProjectAssistantTurn(
        ConversationTurn turn,
        List<IdeationToolCallViewModel> pendingTools,
        List<ChatContentPart> pendingParts,
        ref bool sawThinkingTurn,
        List<ChatMessage> messages,
        List<IdeationProposal> discoveredProposals,
        ILogger logger)
    {
        var parts = new List<ChatContentPart>();
        if (!sawThinkingTurn && !string.IsNullOrEmpty(turn.ThinkingTrace))
        {
            parts.Add(new ThinkingContentPart(turn.ThinkingTrace));
        }

        var tools = new List<IdeationToolCallViewModel>(pendingTools);
        parts.AddRange(pendingParts);
        pendingTools.Clear();
        pendingParts.Clear();
        sawThinkingTurn = false;

        if (!string.IsNullOrEmpty(turn.ToolName) &&
            !tools.Any(t => t.ToolName == turn.ToolName && t.Arguments == turn.ToolArguments))
        {
            var vm = new IdeationToolCallViewModel
            {
                ToolCallId = Guid.NewGuid().ToString("N")[..8],
                ToolName = turn.ToolName,
                Arguments = turn.ToolArguments ?? string.Empty,
                Result = turn.ToolResult,
                IsCompleted = true,
                IsError = turn.IsError,
                DurationMs = turn.DurationMs ?? 0
            };
            tools.Add(vm);
            parts.Add(new ToolCallContentPart(vm));
        }

        if (!string.IsNullOrEmpty(turn.Content))
        {
            parts.Add(new TextContentPart(turn.Content));
        }

        var effectiveThinking = !string.IsNullOrEmpty(turn.ThinkingTrace)
            ? turn.ThinkingTrace
            : JoinThinkingParts(parts);

        messages.Add(new ChatMessage(
            "assistant",
            turn.Content ?? string.Empty,
            string.IsNullOrWhiteSpace(effectiveThinking) ? null : effectiveThinking,
            turn.Timestamp,
            tools.Count > 0 ? tools : null,
            parts.Count > 0 ? parts : null));

        if (!string.IsNullOrWhiteSpace(turn.MetadataJson))
        {
            try
            {
                var proposals = JsonSerializer.Deserialize<List<IdeationProposal>>(turn.MetadataJson, JsonOptions);
                if (proposals != null)
                {
                    discoveredProposals.AddRange(proposals);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize ideation proposals metadata for turn #{TurnIndex}", turn.TurnIndex);
            }
        }
    }

    private static string? JoinThinkingParts(IEnumerable<ChatContentPart> parts)
    {
        var chunks = parts
            .OfType<ThinkingContentPart>()
            .Select(p => p.Thinking)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        return chunks.Count > 0 ? string.Concat(chunks) : null;
    }

    internal static string? ResolveCleanQuestionAnswer(string? rawPayload, string? metadataJson)
    {
        var structured = StageConversationParser.ExtractStructuredAnswers(metadataJson);
        if (structured.Count > 0)
        {
            return StageConversationParser.FormatSingleSubmissionAnswer(
                structured.ToDictionary(kv => kv.Key, kv => kv.Value.AnswerText));
        }

        if (TryExtractAnswerDictionaryFromJson(metadataJson, out var metaAnswers) && metaAnswers.Count > 0)
        {
            return StageConversationParser.FormatSingleSubmissionAnswer(metaAnswers);
        }

        if (TryExtractAnswerDictionaryFromJson(rawPayload, out var payloadAnswers) && payloadAnswers.Count > 0)
        {
            return StageConversationParser.FormatSingleSubmissionAnswer(payloadAnswers);
        }

        return StripUserAnsweredPrefix(rawPayload);
    }

    private static bool TryExtractAnswerDictionaryFromJson(string? json, out Dictionary<int, string> answersByIndex)
    {
        answersByIndex = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        var trimmed = json.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!doc.RootElement.TryGetProperty("Answers", out var ansProp) &&
                !doc.RootElement.TryGetProperty("answers", out ansProp))
            {
                return false;
            }

            if (ansProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in ansProp.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out var idx) &&
                        idx >= 0 &&
                        prop.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        answersByIndex[idx] = prop.Value.GetString()!;
                    }
                }
                return answersByIndex.Count > 0;
            }

            if (ansProp.ValueKind == JsonValueKind.Array)
            {
                int seqIdx = 0;
                foreach (var el in ansProp.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        seqIdx++;
                        continue;
                    }

                    if ((el.TryGetProperty("AnswerText", out var textProp) ||
                         el.TryGetProperty("answerText", out textProp) ||
                         el.TryGetProperty("Answer", out textProp) ||
                         el.TryGetProperty("answer", out textProp)) &&
                        textProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(textProp.GetString()))
                    {
                        int idx = (el.TryGetProperty("QuestionIndex", out var qiProp) || el.TryGetProperty("questionIndex", out qiProp)) &&
                                  qiProp.ValueKind == JsonValueKind.Number
                            ? qiProp.GetInt32()
                            : seqIdx;
                        if (idx >= 0)
                        {
                            answersByIndex[idx] = textProp.GetString()!;
                        }
                    }
                    seqIdx++;
                }
                return answersByIndex.Count > 0;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string? StripUserAnsweredPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.StartsWith(UserAnsweredPrefix, StringComparison.OrdinalIgnoreCase)
            ? value[UserAnsweredPrefix.Length..].Trim()
            : value;
    }
}
