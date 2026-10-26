using System.Text;
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
    /// Determines whether a live running tool execution has completed based on persisted conversation turns.
    /// Avoids falsely matching older ToolResult turns from prior calls of the same tool (e.g. repeated bash calls or retries).
    /// </summary>
    public static bool IsRunningToolCompleted(
        IEnumerable<ConversationTurn>? turns,
        string? runningToolName,
        DateTime? runningToolStartedAt = null)
    {
        if (turns == null || string.IsNullOrWhiteSpace(runningToolName))
            return false;

        // Find turns matching this tool name, ordered by TurnIndex then Timestamp
        var matchingTurns = turns
            .Where(t => string.Equals(t.ToolName, runningToolName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.TurnIndex)
            .ThenBy(t => t.Timestamp)
            .ToList();

        if (matchingTurns.Count == 0)
            return false;

        // If the latest matching turn in the conversation is a Tool turn (call), check if it carries an embedded result or is awaiting one.
        var latestTurn = matchingTurns[^1];
        if (latestTurn.Role == StageTurnRole.Tool)
        {
            if (!string.IsNullOrWhiteSpace(latestTurn.ToolResult))
            {
                if (runningToolStartedAt.HasValue && latestTurn.Timestamp < runningToolStartedAt.Value)
                    return false;
                return true;
            }
            return false;
        }

        // If the latest matching turn is a ToolResult:
        // If we have a start timestamp for the current invocation, ensure this result was persisted at or after that time.
        if (runningToolStartedAt.HasValue && latestTurn.Timestamp < runningToolStartedAt.Value)
            return false;

        return latestTurn.Role == StageTurnRole.ToolResult;
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
            var root = doc.RootElement;
            JsonElement ansProp = default;
            bool foundAnswers = (root.TryGetProperty("Answers", out ansProp) || root.TryGetProperty("answers", out ansProp))
                && ansProp.ValueKind == JsonValueKind.Array;

            if (foundAnswers)
            {
                foreach (var el in ansProp.EnumerateArray())
                {
                    int qIdx = -1;
                    if (el.TryGetProperty("QuestionIndex", out var qiProp) || el.TryGetProperty("questionIndex", out qiProp))
                    {
                        qIdx = qiProp.GetInt32();
                    }

                    int? qId = null;
                    if ((el.TryGetProperty("QuestionId", out var idProp) || el.TryGetProperty("questionId", out idProp))
                        && idProp.ValueKind == JsonValueKind.Number)
                    {
                        qId = idProp.GetInt32();
                    }

                    int? optIdx = null;
                    if ((el.TryGetProperty("SelectedOptionIndex", out var oiProp) || el.TryGetProperty("selectedOptionIndex", out oiProp))
                        && oiProp.ValueKind == JsonValueKind.Number)
                    {
                        optIdx = oiProp.GetInt32();
                    }

                    string? optText = null;
                    if ((el.TryGetProperty("SelectedOption", out var soProp) || el.TryGetProperty("selectedOption", out soProp))
                        && soProp.ValueKind == JsonValueKind.String)
                    {
                        optText = soProp.GetString();
                    }

                    string ansText = string.Empty;
                    if ((el.TryGetProperty("AnswerText", out var atProp) || el.TryGetProperty("answerText", out atProp))
                        && atProp.ValueKind == JsonValueKind.String)
                    {
                        ansText = atProp.GetString() ?? string.Empty;
                    }

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
    /// Builds structured answers metadata JSON from answered questions dictionary and optional question definitions.
    /// </summary>
    public static string BuildStructuredAnswersJson(
        IReadOnlyDictionary<int, string> answersDict,
        IReadOnlyList<QuestionItem>? questions = null)
    {
        if (answersDict == null || answersDict.Count == 0) return string.Empty;

        var entries = new List<object>();
        foreach (var (idx, ans) in answersDict.OrderBy(kv => kv.Key))
        {
            int? optIdx = null;
            string? selectedOpt = null;
            var cleanAns = ans?.Trim();
            if (questions != null && idx < questions.Count && questions[idx].Options != null)
            {
                var options = questions[idx].Options!;
                for (int i = 0; i < options.Count; i++)
                {
                    var opt = options[i];
                    if (opt != null && string.Equals(opt.Trim(), cleanAns, StringComparison.OrdinalIgnoreCase))
                    {
                        optIdx = i;
                        selectedOpt = opt;
                        break;
                    }
                }
            }

            entries.Add(new
            {
                QuestionIndex = idx,
                SelectedOptionIndex = optIdx,
                SelectedOption = selectedOpt,
                AnswerText = ans ?? string.Empty
            });
        }

        return JsonSerializer.Serialize(new { Answers = entries });
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

    /// <summary>
    /// Resolves answered human clarification answers from a ForgeTask's Questions collection,
    /// serving as a fallback when a ToolResult conversation turn has not yet been paired.
    /// </summary>
    public static (string? Answer, string? MetadataJson) ResolveAnswerFromTask(
        ForgeTask? task,
        string? stageId,
        IReadOnlyList<QuestionItem>? turnQuestions = null)
    {
        if (task == null) return (null, null);

        // 1. Check Questions collection
        if (task.Questions != null && task.Questions.Any(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer)))
        {
            // CRITICAL: Strictly filter by stageId if provided. Never fall back to questions from other stages!
            var stageQuestions = !string.IsNullOrEmpty(stageId)
                ? task.Questions.Where(q => string.Equals(q.StageId, stageId, StringComparison.OrdinalIgnoreCase)).OrderBy(q => q.Id).ToList()
                : task.Questions.OrderBy(q => q.Id).ToList();

            if (stageQuestions.Count == 0)
            {
                return (null, null);
            }

            List<Question> matchingAnsweredQuestions;
            if (turnQuestions != null && turnQuestions.Count > 0)
            {
                matchingAnsweredQuestions = new List<Question>();
                foreach (var tq in turnQuestions)
                {
                    var match = stageQuestions.FirstOrDefault(q =>
                        string.Equals(q.Prompt?.Trim(), tq.Prompt?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match != null && match.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(match.Answer))
                    {
                        matchingAnsweredQuestions.Add(match);
                    }
                    else
                    {
                        // Any question in this turn is pending or not answered
                        return (null, null);
                    }
                }
            }
            else
            {
                // If any question in this stage is still Pending, the stage is actively waiting
                if (stageQuestions.Any(q => q.Status == QuestionStatus.Pending))
                {
                    return (null, null);
                }

                matchingAnsweredQuestions = stageQuestions
                    .Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer))
                    .ToList();
            }

            if (matchingAnsweredQuestions.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < matchingAnsweredQuestions.Count; i++)
                {
                    if (i > 0) sb.AppendLine();
                    if (matchingAnsweredQuestions.Count > 1)
                    {
                        sb.AppendLine($"{i + 1}. Question: {matchingAnsweredQuestions[i].Prompt}");
                        sb.AppendLine($"   Answer: {matchingAnsweredQuestions[i].Answer}");
                    }
                    else
                    {
                        sb.Append(matchingAnsweredQuestions[i].Answer);
                    }
                }
                var formatted = sb.ToString().TrimEnd();

                var answerEntries = new List<QuestionAnswerEntry>();
                for (int i = 0; i < matchingAnsweredQuestions.Count; i++)
                {
                    var q = matchingAnsweredQuestions[i];
                    var ans = q.Answer ?? string.Empty;
                    int? selectedOptionIndex = null;
                    string? selectedOption = null;

                    if (!string.IsNullOrWhiteSpace(q.OptionsJson))
                    {
                        try
                        {
                            var options = JsonSerializer.Deserialize<List<string>>(q.OptionsJson);
                            if (options != null)
                            {
                                for (int optIdx = 0; optIdx < options.Count; optIdx++)
                                {
                                    if (string.Equals(options[optIdx]?.Trim(), ans.Trim(), StringComparison.OrdinalIgnoreCase))
                                    {
                                        selectedOptionIndex = optIdx;
                                        selectedOption = options[optIdx];
                                        break;
                                    }
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // Ignore malformed options JSON
                        }
                    }

                    answerEntries.Add(new QuestionAnswerEntry(
                        QuestionIndex: i,
                        QuestionId: q.Id,
                        SelectedOptionIndex: selectedOptionIndex,
                        SelectedOption: selectedOption,
                        AnswerText: ans));
                }

                var metaDict = new Dictionary<string, object?>
                {
                    ["CallId"] = Guid.NewGuid().ToString("N"),
                    ["Answers"] = answerEntries
                };

                return (formatted, JsonSerializer.Serialize(metaDict));
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Determines whether the given stage execution or conversation is actively waiting for human clarification.
    /// Returns false if answers were provided (task resumed/queued) or all stage questions are answered.
    /// </summary>
    public static bool IsStageWaitingForAnswer(Conversation? conversation, ForgeTask? task)
    {
        if (conversation == null || conversation.Status != StageExecutionStatus.WaitingForHuman)
        {
            return false;
        }

        if (task != null && !task.IsWaitingForHuman)
        {
            return false;
        }

        if (task?.Questions != null)
        {
            var stageQuestions = !string.IsNullOrEmpty(conversation.StageId)
                ? task.Questions.Where(q => string.Equals(q.StageId, conversation.StageId, StringComparison.OrdinalIgnoreCase)).ToList()
                : task.Questions.ToList();

            if (stageQuestions.Any(q => q.Status == QuestionStatus.Pending))
            {
                return true;
            }

            if (stageQuestions.Count > 0 && stageQuestions.All(q => q.Status == QuestionStatus.Answered))
            {
                return false;
            }
        }

        var lastAskTurn = conversation.Turns?
            .LastOrDefault(t => t.Role == StageTurnRole.Tool && string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase));

        if (lastAskTurn != null)
        {
            var lastAskCallId = ConversationHistorySerializer.ExtractCallId(lastAskTurn);
            bool hasResultForLastAsk = conversation.Turns != null && conversation.Turns.Any(t =>
                t.Role == StageTurnRole.ToolResult &&
                string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase) &&
                t.TurnIndex > lastAskTurn.TurnIndex &&
                (!string.IsNullOrEmpty(lastAskCallId)
                    ? (t.MetadataJson != null && t.MetadataJson.Contains(lastAskCallId))
                    : true));

            if (hasResultForLastAsk)
            {
                return false;
            }

            return true;
        }

        return conversation.Status == StageExecutionStatus.WaitingForHuman;
    }

    /// <summary>
    /// Determines whether a specific ask interruption turn is actively waiting for a human answer.
    /// Returns false if an answer is already present or if the task is no longer waiting for human.
    /// </summary>
    public static bool IsWaitingForHumanAnswer(Conversation? conversation, ForgeTask? task, string? answer)
    {
        if (!string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        if (task != null && !task.IsWaitingForHuman)
        {
            return false;
        }

        return conversation?.Status == StageExecutionStatus.WaitingForHuman;
    }

    /// <summary>
    /// Evaluates whether a conversation turn represents a synthetic primed execution from a prior deterministic stage.
    /// </summary>
    public static bool IsPrimedTurn(ConversationTurn? turn)
    {
        if (turn == null || string.IsNullOrWhiteSpace(turn.MetadataJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(turn.MetadataJson);
            if (doc.RootElement.TryGetProperty("IsPrimed", out var prop))
            {
                return prop.ValueKind == JsonValueKind.True;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed metadata
        }

        return false;
    }

    /// <summary>
    /// Returns the UI badge text for a primed conversation turn (e.g. forward pass or loopback diagnostic),
    /// or null if the turn is not primed.
    /// </summary>
    public static string? GetPrimingBadge(ConversationTurn? turn)
    {
        if (!IsPrimedTurn(turn) || string.IsNullOrWhiteSpace(turn!.MetadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(turn.MetadataJson);
            if (doc.RootElement.TryGetProperty("PrimingType", out var typeProp))
            {
                var type = typeProp.GetString();
                if (string.Equals(type, "ForwardPass", StringComparison.OrdinalIgnoreCase))
                {
                    return "⚡ Quality Gate Handover";
                }
                if (string.Equals(type, "LoopbackDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    return "🔁 Quality Gate Diagnostic";
                }
            }
            return "⚡ Quality Gate Handover";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the informative tooltip explaining the deterministic origin of a primed conversation turn,
    /// or null if the turn is not primed.
    /// </summary>
    public static string? GetPrimingTooltip(ConversationTurn? turn)
    {
        if (!IsPrimedTurn(turn) || string.IsNullOrWhiteSpace(turn!.MetadataJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(turn.MetadataJson);
            if (doc.RootElement.TryGetProperty("PrimingType", out var typeProp))
            {
                var type = typeProp.GetString();
                if (string.Equals(type, "LoopbackDiagnostic", StringComparison.OrdinalIgnoreCase))
                {
                    return "Deterministic verification failure diagnostic from prior stage without GPU inference.";
                }
            }
            return "Verified in prior deterministic stage without GPU inference.";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

