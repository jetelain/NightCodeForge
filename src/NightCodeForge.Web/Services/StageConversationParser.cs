using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Helper class for parsing, pairing, and formatting conversation turns and workflow stages.
/// Isolates conversation turn extraction, tool call/result pairing, and optimization payload parsing
/// from the Blazor rendering layer to facilitate reuse and automated testing.
/// </summary>
public static class StageConversationParser
{
    /// <summary>
    /// Extracts the CallId from a conversation turn's MetadataJson, if present.
    /// </summary>
    public static string? ExtractCallId(ConversationTurn? turn)
    {
        if (turn == null || string.IsNullOrWhiteSpace(turn.MetadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(turn.MetadataJson);
            if (doc.RootElement.TryGetProperty("CallId", out var prop))
            {
                return prop.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed metadata JSON
        }
        return null;
    }

    /// <summary>
    /// Pairs every tool result turn with the matching tool call turn (by CallId when available, or FIFO of same tool name)
    /// so a call and its result render as a single card.
    /// </summary>
    public static (Dictionary<int, ConversationTurn> Merged, HashSet<int> Hidden) PairToolResults(IReadOnlyList<ConversationTurn>? turns)
    {
        var merged = new Dictionary<int, ConversationTurn>();
        var hidden = new HashSet<int>();
        if (turns == null || turns.Count == 0) return (merged, hidden);

        var open = new List<ConversationTurn>();

        foreach (var turn in turns)
        {
            if (turn.Role == StageTurnRole.Tool)
            {
                open.Add(turn);
            }
            else if (turn.Role == StageTurnRole.ToolResult)
            {
                var turnCallId = ExtractCallId(turn);
                ConversationTurn? call = null;
                if (!string.IsNullOrEmpty(turnCallId))
                {
                    call = open.FirstOrDefault(c =>
                        string.Equals(c.ToolName, turn.ToolName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ExtractCallId(c), turnCallId, StringComparison.Ordinal));
                }

                call ??= open.FirstOrDefault(c => string.Equals(c.ToolName, turn.ToolName, StringComparison.OrdinalIgnoreCase));

                if (call != null)
                {
                    open.Remove(call);
                    merged[call.Id] = turn;
                    hidden.Add(turn.Id);
                }
            }
        }

        return (merged, hidden);
    }

    /// <summary>
    /// Formats a workflow stage ID into a human-readable name, respecting custom definitions if provided.
    /// </summary>
    public static string FormatStageName(string stageId, IReadOnlyList<StageDefinition>? workflowStages = null)
    {
        if (workflowStages != null)
        {
            var match = workflowStages.FirstOrDefault(s => string.Equals(s.StageId, stageId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match.SequenceOrder > 0 ? $"{match.SequenceOrder}. {match.DisplayName}" : match.DisplayName;
            }
        }

        return stageId switch
        {
            StandardStageIds.TaskReview => "ForgeTask Review",
            StandardStageIds.PreImplementationBaseline => "Pre-Implementation Baseline",
            StandardStageIds.Implementation => "Implementation",
            StandardStageIds.PostImplementationGates => "Post-Implementation Gates",
            StandardStageIds.CodeReviewAndFix => "Code Review & Fix",
            StandardStageIds.PostReviewVerification => "Post-Review Verification",
            _ => stageId
        };
    }

    /// <summary>
    /// Returns the CSS badge class for a given stage execution status.
    /// </summary>
    public static string GetStatusClass(StageExecutionStatus status)
    {
        return status switch
        {
            StageExecutionStatus.Completed => "badge-emerald",
            StageExecutionStatus.Running => "badge-amber",
            StageExecutionStatus.WaitingForHuman => "badge-amber",
            StageExecutionStatus.Failed => "badge-crimson",
            StageExecutionStatus.Skipped => "badge-zinc",
            StageExecutionStatus.Pending => "badge-zinc",
            StageExecutionStatus.Superseded => "badge-zinc",
            _ => "badge-zinc"
        };
    }

    /// <summary>
    /// Parses token savings and action descriptions from an optimization turn.
    /// </summary>
    public static (string ActionType, string Description, int TokensSaved, string? Details) ParseOptimizationPayload(ConversationTurn turn)
    {
        var actionType = "ContextOptimization";
        var desc = turn.Content ?? "Optimized conversation memory";
        var tokensSaved = 0;
        string? details = null;

        if (!string.IsNullOrWhiteSpace(turn.MetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(turn.MetadataJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("ActionType", out var atProp))
                {
                    actionType = atProp.GetString() ?? actionType;
                }
                if (root.TryGetProperty("TokensSaved", out var tsProp))
                {
                    tokensSaved = tsProp.GetInt32();
                }
                else if (root.TryGetProperty("CharactersSaved", out var csProp))
                {
                    tokensSaved = csProp.GetInt32();
                }
                details = turn.MetadataJson;
            }
            catch (JsonException)
            {
                // Fallback to defaults
            }
        }

        return (actionType, desc, tokensSaved, details);
    }

    /// <summary>
    /// Extracts structured questions from a tool turn's arguments or metadata JSON, supporting multiple array keys and single-question objects.
    /// </summary>
    public static IReadOnlyList<QuestionItem> ExtractQuestions(ConversationTurn turn)
    {
        var rawJson = !string.IsNullOrWhiteSpace(turn.ToolArguments)
            ? turn.ToolArguments
            : turn.MetadataJson;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new[] { new QuestionItem(turn.Content ?? "Clarification requested") };
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                var arrayKeys = new[] { "questions", "Questions", "items", "Items", "question_items", "questionItems" };
                foreach (var key in arrayKeys)
                {
                    if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        return ParseQuestionArray(arr);
                    }
                }

                if (root.TryGetProperty("prompt", out var p) || root.TryGetProperty("Prompt", out p) ||
                    root.TryGetProperty("question", out p) || root.TryGetProperty("Question", out p))
                {
                    var prompt = p.GetString();
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        string? rationale = null;
                        if (root.TryGetProperty("rationale", out var r) || root.TryGetProperty("Rationale", out r))
                        {
                            rationale = r.GetString();
                        }
                        List<string>? options = null;
                        if ((root.TryGetProperty("options", out var o) || root.TryGetProperty("Options", out o)) && o.ValueKind == JsonValueKind.Array)
                        {
                            options = new List<string>();
                            foreach (var opt in o.EnumerateArray())
                            {
                                var optStr = opt.GetString();
                                if (optStr != null) options.Add(optStr);
                            }
                        }
                        else if ((root.TryGetProperty("optionsJson", out var oj) || root.TryGetProperty("OptionsJson", out oj)) && oj.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(oj.GetString()))
                        {
                            try
                            {
                                options = JsonSerializer.Deserialize<List<string>>(oj.GetString()!);
                            }
                            catch (JsonException)
                            {
                                // Ignore malformed options JSON
                            }
                        }
                        return new[] { new QuestionItem(prompt, rationale, options) };
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                return ParseQuestionArray(root);
            }
        }
        catch (JsonException)
        {
            // Fallback to raw content
        }

        return new[] { new QuestionItem(turn.Content ?? turn.ToolArguments ?? "Clarification requested") };
    }

    private static List<QuestionItem> ParseQuestionArray(JsonElement arrayElement)
    {
        var list = new List<QuestionItem>();
        foreach (var el in arrayElement.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                list.Add(new QuestionItem(el.GetString() ?? string.Empty));
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                string prompt = string.Empty;
                string? rationale = null;
                List<string>? options = null;

                if (el.TryGetProperty("prompt", out var p) || el.TryGetProperty("Prompt", out p))
                {
                    prompt = p.GetString() ?? string.Empty;
                }
                if (el.TryGetProperty("rationale", out var r) || el.TryGetProperty("Rationale", out r))
                {
                    rationale = r.GetString();
                }
                if ((el.TryGetProperty("options", out var o) || el.TryGetProperty("Options", out o)) && o.ValueKind == JsonValueKind.Array)
                {
                    options = new List<string>();
                    foreach (var opt in o.EnumerateArray())
                    {
                        var optStr = opt.GetString();
                        if (optStr != null) options.Add(optStr);
                    }
                }
                else if ((el.TryGetProperty("optionsJson", out var oj) || el.TryGetProperty("OptionsJson", out oj)) && oj.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(oj.GetString()))
                {
                    try
                    {
                        options = JsonSerializer.Deserialize<List<string>>(oj.GetString()!);
                    }
                    catch (JsonException)
                    {
                        // Ignore malformed options JSON
                    }
                }

                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    list.Add(new QuestionItem(prompt, rationale, options));
                }
            }
        }
        return list.Count > 0 ? list : new List<QuestionItem> { new QuestionItem("Clarification requested") };
    }

    /// <summary>
    /// Resolves the list of questions that belong to the active or latest pending batch for a stage.
    /// </summary>
    public static IReadOnlyList<Question> ResolvePendingBatchQuestions(IEnumerable<Question>? allQuestions, string? stageId)
    {
        if (allQuestions == null)
        {
            return Array.Empty<Question>();
        }

        var list = allQuestions.ToList();
        if (list.Count == 0)
        {
            return Array.Empty<Question>();
        }

        var stageQuestions = !string.IsNullOrEmpty(stageId)
            ? list
                .Where(q => string.Equals(q.StageId, stageId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(q => q.Id)
                .ToList()
            : new List<Question>();

        if (stageQuestions.Count == 0)
        {
            stageQuestions = list.OrderBy(q => q.Id).ToList();
        }

        var latestPending = stageQuestions.LastOrDefault(q => q.Status == QuestionStatus.Pending);
        var activeBatchId = latestPending?.BatchId;

        return !string.IsNullOrEmpty(activeBatchId)
            ? stageQuestions.Where(q => string.Equals(q.BatchId, activeBatchId, StringComparison.OrdinalIgnoreCase)).OrderBy(q => q.Id).ToList()
            : stageQuestions.Where(q => q.Status == QuestionStatus.Pending).OrderBy(q => q.Id).ToList();
    }

    /// <summary>
    /// Maps a 0-indexed dictionary of answers to target Question IDs.
    /// </summary>
    public static Dictionary<int, string> MapAnswersToQuestionIds(
        IReadOnlyList<Question>? batchQuestions,
        IReadOnlyDictionary<int, string>? answersByIndex)
    {
        var dict = new Dictionary<int, string>();
        if (batchQuestions == null || batchQuestions.Count == 0 || answersByIndex == null || answersByIndex.Count == 0)
        {
            return dict;
        }

        foreach (var (index, ans) in answersByIndex)
        {
            if (string.IsNullOrWhiteSpace(ans)) continue;

            Question? targetQuestion = null;
            if (index < batchQuestions.Count)
            {
                targetQuestion = batchQuestions[index];
            }
            else
            {
                targetQuestion = batchQuestions.FirstOrDefault(q => q.Status == QuestionStatus.Pending && !dict.ContainsKey(q.Id));
            }

            if (targetQuestion != null && targetQuestion.Status == QuestionStatus.Pending && !dict.ContainsKey(targetQuestion.Id))
            {
                dict[targetQuestion.Id] = ans.Trim();
            }
        }

        return dict;
    }

    /// <summary>
    /// Parses structured QuestionAnswerEntry records from a turn's MetadataJson, keyed by QuestionIndex.
    /// </summary>
    public static Dictionary<int, QuestionAnswerEntry> ExtractStructuredAnswers(string? metadataJson)
    {
        var result = new Dictionary<int, QuestionAnswerEntry>();
        if (string.IsNullOrWhiteSpace(metadataJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("Answers", out var ansProp) && ansProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in ansProp.EnumerateArray())
                {
                    var qIdx = el.TryGetProperty("QuestionIndex", out var qiProp) ? qiProp.GetInt32() : -1;
                    int? qId = el.TryGetProperty("QuestionId", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : null;
                    int? optIdx = el.TryGetProperty("SelectedOptionIndex", out var oiProp) && oiProp.ValueKind == JsonValueKind.Number ? oiProp.GetInt32() : null;
                    string? optText = el.TryGetProperty("SelectedOption", out var soProp) && soProp.ValueKind == JsonValueKind.String ? soProp.GetString() : null;
                    string ansText = el.TryGetProperty("AnswerText", out var atProp) && atProp.ValueKind == JsonValueKind.String ? atProp.GetString() ?? string.Empty : string.Empty;

                    if (qIdx >= 0)
                    {
                        result[qIdx] = new QuestionAnswerEntry(qIdx, qId, optIdx, optText, ansText);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed metadata
        }

        return result;
    }

    /// <summary>
    /// Formats answered questions dictionary into a single submission answer string.
    /// </summary>
    public static string FormatSingleSubmissionAnswer(IReadOnlyDictionary<int, string> answeredDict)
    {
        if (answeredDict == null || answeredDict.Count == 0) return string.Empty;

        if (answeredDict.Count == 1 && answeredDict.TryGetValue(0, out var firstAns))
        {
            return firstAns;
        }

        return string.Join("\n\n", answeredDict.OrderBy(kv => kv.Key).Select(kv => $"Q{kv.Key + 1}: {kv.Value}"));
    }

    /// <summary>
    /// Checks whether an option was selected by inspecting structured answers, with fallback to clean normalized matching.
    /// </summary>
    public static bool IsOptionSelected(
        IReadOnlyDictionary<int, QuestionAnswerEntry>? structuredAnswers,
        string? rawAnswer,
        int questionIndex,
        int optionIndex,
        string optionText)
    {
        if (structuredAnswers != null && structuredAnswers.TryGetValue(questionIndex, out var entry))
        {
            if (entry.SelectedOptionIndex.HasValue)
            {
                return entry.SelectedOptionIndex.Value == optionIndex;
            }
            if (!string.IsNullOrEmpty(entry.SelectedOption))
            {
                return string.Equals(entry.SelectedOption.Trim(), optionText.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(entry.AnswerText.Trim(), optionText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(rawAnswer))
        {
            return string.Equals(rawAnswer.Trim(), optionText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

