using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Agents.Compaction;

/// <summary>
/// High-performance context compaction strategy inspired by the Pi coding agent.
/// Unlike standard MAF truncation strategies, this strategy guarantees:
/// 1. Task Anchor Protection: Never evicts the initial User task specification or System prompt.
/// 2. Dynamic Headroom Budgeting: Triggers when contextTokens &gt; ContextWindowTokens - ReserveTokens.
/// 3. Recent Window Preservation: Keeps recent turns within KeepRecentTokens verbatim.
/// 4. Tool Result Clamping &amp; Serialization: Clamps tool outputs to 2,000 chars during summarization.
/// 5. Lossless File Tracking: Scans tool calls and attaches &lt;read-files&gt; and &lt;modified-files&gt;.
/// 6. Structured Context Checkpoints: Injects strict markdown summaries.
/// </summary>
public sealed class PiStyleCrucibleCompactionStrategy : CompactionStrategy
{
    public const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\nDo NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";

    public const string SummarizationPromptTemplate =
        @"The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

Use this EXACT format:

## Goal
[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

## Constraints & Preferences
- [Any constraints, preferences, or requirements mentioned by user]
- [Or ""(none)"" if none were mentioned]

## Progress
### Done
- [x] [Completed tasks/changes]

### In Progress
- [ ] [Current work]

### Blocked
- [Issues preventing progress, if any]

## Key Decisions
- **[Decision]**: [Brief rationale]

## Next Steps
1. [Ordered list of what should happen next]

## Critical Context
- [Any data, examples, or references needed to continue]
- [Or ""(none)"" if not applicable]

Keep each section concise. Preserve exact file paths, function names, and error messages.";

    public const string UpdateSummarizationPromptTemplate =
        @"The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

Update the existing structured summary with new information. RULES:
- PRESERVE all existing information from the previous summary
- ADD new progress, decisions, and context from the new messages
- UPDATE the Progress section: move items from ""In Progress"" to ""Done"" when completed
- UPDATE ""Next Steps"" based on what was accomplished
- PRESERVE exact file paths, function names, and error messages
- If something is no longer relevant, you may remove it

Use this EXACT format:

## Goal
[Preserve existing goals, add new ones if the task expanded]

## Constraints & Preferences
- [Preserve existing, add new ones discovered]

## Progress
### Done
- [x] [Include previously done items AND newly completed items]

### In Progress
- [ ] [Current work - update based on progress]

### Blocked
- [Current blockers - remove if resolved]

## Key Decisions
- **[Decision]**: [Brief rationale] (preserve all previous, add new)

## Next Steps
1. [Update based on current state]

## Critical Context
- [Preserve important context, add new if needed]

Keep each section concise. Preserve exact file paths, function names, and error messages.";

    public const string CheckpointPreamble =
        "[CONTEXT COMPACTION CHECKPOINT]\nThe conversation history prior to this point was compacted into the following summary:\n\n";

    public const string CompactionExcludeReason = "PiStyleCompaction";

    /// <summary>Maximum characters kept per user message in the deterministic fallback summary.</summary>
    public const int MaxDeterministicGoalChars = 500;

    private readonly CrucibleCompactionOptions _options;
    private readonly IChatClient? _summarizerClient;
    private int _lastCompactedTurnCount = -1;
    private int _lastCompactedTokenCount = 0;

    public CrucibleCompactionOptions Options => _options;
    public int LastCompactedTurnCount => _lastCompactedTurnCount;
    public int LastCompactedTokenCount => _lastCompactedTokenCount;

    public PiStyleCrucibleCompactionStrategy(CrucibleCompactionOptions options, IChatClient? summarizerClient = null)
        : base(
            CompactionTriggers.TokensExceed(RequireOptions(options).TriggerThresholdTokens),
            CompactionTriggers.TokensBelow(options.TriggerThresholdTokens))
    {
        _options = options;
        _summarizerClient = summarizerClient;
    }

    private static CrucibleCompactionOptions RequireOptions(CrucibleCompactionOptions? options) =>
        options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Calculates the dynamic budget of recent tokens to preserve verbatim.
    /// Invariant: AnchorTokens + EffectiveKeepRecent + CheckpointSummary &lt;= TriggerThreshold - HeadroomMargin.
    /// </summary>
    public int CalculateEffectiveKeepRecentTokens(int anchorTokens)
    {
        int effectiveSafetyMargin = _options.SafetyMarginTokens;
        if (_options.TriggerThresholdTokens <= _options.SafetyMarginTokens)
        {
            effectiveSafetyMargin = Math.Max(0, _options.TriggerThresholdTokens / 4);
        }

        int dynamicBudget = Math.Max(0, _options.TriggerThresholdTokens - anchorTokens - effectiveSafetyMargin);
        return Math.Min(_options.KeepRecentTokens, dynamicBudget);
    }

    protected override async ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return false;

        // Check if active (included) context token count exceeds trigger threshold
        if (index.IncludedTokenCount <= _options.TriggerThresholdTokens)
        {
            return false;
        }

        var groups = index.Groups;
        if (groups.Count <= 2)
        {
            // Too few groups to compact without destroying prompt anchors
            return false;
        }

        // Count assistant turns across all groups in index (strictly monotonic)
        int totalAssistantTurns = groups.Sum(g => g.Messages.Count(m => m.Role == ChatRole.Assistant));

        // Evaluate emergency context limit on active context window (IncludedTokenCount)
        bool isEmergency = index.IncludedTokenCount >= (int)(_options.ContextWindowTokens * _options.EmergencyThresholdRatio);

        if (!isEmergency && _lastCompactedTurnCount >= 0)
        {
            int turnsSinceLastCompaction = totalAssistantTurns - _lastCompactedTurnCount;
            if (turnsSinceLastCompaction < _options.DebounceTurnCooldown)
            {
                logger.LogDebug(
                    "Compaction skipped by turn cooldown. Turns since last: {Turns}, Cooldown: {Cooldown}",
                    turnsSinceLastCompaction, _options.DebounceTurnCooldown);
                return false;
            }

            int tokensAddedSinceLastCompaction = Math.Max(0, index.TotalTokenCount - _lastCompactedTokenCount);
            if (tokensAddedSinceLastCompaction < _options.DebounceTokenDelta)
            {
                logger.LogDebug(
                    "Compaction skipped by token delta debounce. Tokens added: {TokensAdded}, Min delta: {MinDelta}",
                    tokensAddedSinceLastCompaction, _options.DebounceTokenDelta);
                return false;
            }
        }

        // Tier 2 (Compaction Emergency): Clamp oversized tool results in active groups
        ClampOversizedToolResults(groups, _options.EmergencyToolResultMaxChars, _options.ToolResultMaxChars, logger);

        // 1. Locate Task Anchor (System messages + initial User message)
        int anchorEndIndex = -1;
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.Kind is CompactionGroupKind.System or CompactionGroupKind.User)
            {
                anchorEndIndex = i;
                if (g.Kind == CompactionGroupKind.User)
                {
                    // Found the primary initial user task specification
                    break;
                }
            }
        }

        if (anchorEndIndex < 0 || anchorEndIndex >= groups.Count - 2)
        {
            return false;
        }

        int tokensAnchor = 0;
        for (int i = 0; i <= anchorEndIndex; i++)
        {
            tokensAnchor += groups[i].TokenCount;
        }

        int effectiveKeepRecent = CalculateEffectiveKeepRecentTokens(tokensAnchor);

        // Check if there is an existing active (non-excluded) summary group from a prior compaction pass
        CompactionMessageGroup? existingSummaryGroup = null;
        string? previousSummary = null;
        for (int i = anchorEndIndex + 1; i < groups.Count; i++)
        {
            if (groups[i].Kind == CompactionGroupKind.Summary && !groups[i].IsExcluded)
            {
                existingSummaryGroup = groups[i];
                var rawText = existingSummaryGroup.Messages.FirstOrDefault()?.Text ?? string.Empty;
                previousSummary = StripCheckpointPreamble(rawText);
                break;
            }
        }

        // 2. Identify Cut Point for Recent Window (walking backwards from newest group)
        int accumulatedRecentTokens = 0;
        int cutIndex = -1;

        for (int i = groups.Count - 1; i > anchorEndIndex; i--)
        {
            var g = groups[i];
            if (g.IsExcluded) continue; // Already compacted groups do not occupy the live context window

            accumulatedRecentTokens += g.TokenCount;

            if (accumulatedRecentTokens >= effectiveKeepRecent)
            {
                cutIndex = i;
                break;
            }
        }

        if (cutIndex < 0)
        {
            // The recent budget could not be filled: cut at newest group.
            cutIndex = groups.Count - 1;
        }

        // If the cut point cannot preserve any recent groups (e.g. an oversized older group consumes KeepRecentTokens),
        // advance cutIndex to preserve at least the newest turn(s) so the oversized older history can be compacted.
        if (cutIndex <= anchorEndIndex + 1 && anchorEndIndex + 2 <= groups.Count - 1)
        {
            cutIndex = anchorEndIndex + 2;
        }

        // Adjust cut point to prevent orphaning tool results.
        // A cut point must NEVER be an orphan tool result.
        // Tool calls and their subsequent tool results form an atomic unit.
        cutIndex = AdjustCutPointForToolAtomicity(groups, cutIndex, anchorEndIndex);

        if (cutIndex <= anchorEndIndex || cutIndex >= groups.Count)
        {
            return false;
        }

        // 3. Select Groups to Summarize (strictly between Anchor/PreviousSummary and Cut Point)
        var groupsToSummarize = new List<CompactionMessageGroup>();
        for (int i = anchorEndIndex + 1; i < cutIndex; i++)
        {
            var g = groups[i];
            if (!g.IsExcluded && g.Kind != CompactionGroupKind.Summary)
            {
                groupsToSummarize.Add(g);
            }
        }

        if (groupsToSummarize.Count == 0)
        {
            return false;
        }

        // 4. Extract File Operations (seeding with previous summary's file ops for lossless tracking)
        var fileTracker = new FileOperationTracker();
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            fileTracker.SeedFromSummaryXml(previousSummary);
        }

        foreach (var g in groupsToSummarize)
        {
            fileTracker.RecordMessages(g.Messages);
        }
        var fileOpsXml = fileTracker.FormatFileOperationsXml();

        // 5. Pre-Serialize Messages to Text with Clamped Tool Results
        var messagesToSummarize = groupsToSummarize.SelectMany(g => g.Messages).ToList();
        var serializedTranscript = ConversationSerializer.SerializeConversation(messagesToSummarize, _options.ToolResultMaxChars);

        // 6. Generate Summary (via LLM or deterministic fallback)
        string summaryBody;
        if (_summarizerClient != null)
        {
            try
            {
                summaryBody = await GenerateLlmSummaryAsync(serializedTranscript, previousSummary, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LLM summarization failed during context compaction. Falling back to deterministic structured summary.");
                summaryBody = GenerateDeterministicSummary(messagesToSummarize, previousSummary);
            }
        }
        else
        {
            summaryBody = GenerateDeterministicSummary(messagesToSummarize, previousSummary);
        }

        var fullSummary = $"{summaryBody}{fileOpsXml}".Trim();

        // 7. Exclude summarized groups from active LLM context
        foreach (var g in groupsToSummarize)
        {
            g.IsExcluded = true;
            g.ExcludeReason = CompactionExcludeReason;
        }

        if (existingSummaryGroup != null)
        {
            existingSummaryGroup.IsExcluded = true;
            existingSummaryGroup.ExcludeReason = CompactionExcludeReason;
        }

        // 8. Insert Structured Summary Group immediately after Task Anchor
        var summaryMessage = new ChatMessage(
            ChatRole.User,
            $"{CheckpointPreamble}{fullSummary}");

        index.InsertGroup(anchorEndIndex + 1, CompactionGroupKind.Summary, new[] { summaryMessage }, null);

        _lastCompactedTurnCount = totalAssistantTurns;
        _lastCompactedTokenCount = index.TotalTokenCount;

        logger.LogInformation(
            "Pi-style context compaction succeeded. Summarized {GroupCount} groups ({MessageCount} messages). Injected summary with {FileOpsLength} file ops characters.",
            groupsToSummarize.Count,
            messagesToSummarize.Count,
            fileOpsXml.Length);

        return true;
    }

    private static void ClampOversizedToolResults(
        IList<CompactionMessageGroup> groups,
        int emergencyMaxChars,
        int clampedTargetChars,
        ILogger logger)
    {
        foreach (var group in groups)
        {
            if (group.IsExcluded) continue;

            foreach (var msg in group.Messages)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionResultContent frc && frc.Result != null)
                    {
                        var resStr = frc.Result.ToString();
                        if (resStr != null && resStr.Length > emergencyMaxChars)
                        {
                            int targetLen = Math.Min(clampedTargetChars, resStr.Length);
                            logger.LogInformation("Clamping emergency tool result in call {CallId} from {Length} to {Target} characters",
                                frc.CallId, resStr.Length, targetLen);
                            frc.Result = resStr[..targetLen] + $"\n... [Output clamped to {targetLen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters]";
                        }
                    }
                    else if (content is TextContent tc && tc.Text != null && msg.Role == ChatRole.Tool)
                    {
                        if (tc.Text.Length > emergencyMaxChars)
                        {
                            int targetLen = Math.Min(clampedTargetChars, tc.Text.Length);
                            logger.LogInformation("Clamping emergency tool text content from {Length} to {Target} characters",
                                tc.Text.Length, targetLen);
                            tc.Text = tc.Text[..targetLen] + $"\n... [Output clamped to {targetLen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters]";
                        }
                    }
                }
            }
        }
    }

    private static int AdjustCutPointForToolAtomicity(
        IList<CompactionMessageGroup> groups,
        int cutIndex,
        int anchorEndIndex)
    {
        // If cutIndex is on an orphan tool result (a group with tool results but without the originating call),
        // we must not split the tool call and its results.
        while (cutIndex < groups.Count && ContainsToolResult(groups[cutIndex]))
        {
            // Find the preceding assistant message that made the tool call
            int callIndex = -1;
            for (int j = cutIndex - 1; j > anchorEndIndex; j--)
            {
                if (groups[j].Messages.Any(m => m.Role == ChatRole.Assistant && m.Contents.Any(c => c is FunctionCallContent)))
                {
                    callIndex = j;
                    break;
                }
            }

            if (callIndex > anchorEndIndex + 1)
            {
                // We can safely keep the call and all its results together in the recent window!
                cutIndex = callIndex;
                break;
            }
            else
            {
                // Moving back would leave no non-anchor groups to summarize before the tool call.
                // Try advancing cutIndex past all tool results to the next valid turn (User or Assistant).
                int nextValidCut = -1;
                for (int j = cutIndex + 1; j < groups.Count; j++)
                {
                    if (!ContainsToolResult(groups[j]))
                    {
                        nextValidCut = j;
                        break;
                    }
                }

                if (nextValidCut > 0 && nextValidCut < groups.Count)
                {
                    // Both the call and its results will be summarized together into the checkpoint,
                    // and the recent window starts at the subsequent turn.
                    cutIndex = nextValidCut;
                    break;
                }
                else
                {
                    // No subsequent turn exists to keep (trailing tool results at end of conversation).
                    // We cannot safely compact without orphaning or dropping the entire active turn.
                    return -1;
                }
            }
        }

        // Final sanity check: the chosen cut group must never be an orphan tool result
        if (cutIndex >= groups.Count || ContainsToolResult(groups[cutIndex]))
        {
            return -1;
        }

        return cutIndex;
    }

    private async Task<string> GenerateLlmSummaryAsync(
        string serializedTranscript,
        string? previousSummary,
        CancellationToken cancellationToken)
    {
        string promptText = $"<conversation>\n{serializedTranscript}\n</conversation>\n\n";
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            promptText += $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n{UpdateSummarizationPromptTemplate}";
        }
        else
        {
            promptText += SummarizationPromptTemplate;
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SummarizationSystemPrompt),
            new(ChatRole.User, promptText)
        };

        var options = new ChatOptions
        {
            ModelId = _options.SummarizerModelId,
            MaxOutputTokens = Math.Min(8192, Math.Max(1024, (int)(_options.ReserveTokens * 0.8)))
        };

        var response = await _summarizerClient!.GetResponseAsync(messages, options, cancellationToken);

        // Guard against accidental tool calls in summarizer response
        if (response.Messages.SelectMany(m => m.Contents).Any(c => c is FunctionCallContent))
        {
            throw new InvalidOperationException("Summarizer attempted to call a tool.");
        }

        var text = response.Text?.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Summarizer returned an empty response.");
        }

        return text;
    }

    private static string GenerateDeterministicSummary(
        IReadOnlyList<ChatMessage> messages,
        string? previousSummary)
    {
        var userGoals = messages
            .Where(m => m.Role == ChatRole.User
                     && !string.IsNullOrWhiteSpace(m.Text)
                     && !m.Text.StartsWith("[CONTEXT COMPACTION CHECKPOINT]", StringComparison.OrdinalIgnoreCase))
            .Select(m => ConversationSerializer.TruncateForSummary(m.Text!, MaxDeterministicGoalChars))
            .Take(3)
            .ToList();

        var assistantActions = messages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(fc => fc.Name)
            .Distinct()
            .ToList();

        string goalSummary;
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            var prevGoal = ExtractSection(previousSummary, "## Goal", "## Constraints");
            if (!string.IsNullOrWhiteSpace(prevGoal))
            {
                goalSummary = userGoals.Count > 0 ? $"{prevGoal}\n- Additional goals: {string.Join("; ", userGoals)}" : prevGoal;
            }
            else
            {
                goalSummary = userGoals.Count > 0 ? string.Join("; ", userGoals) : "Continue task implementation and verification.";
            }
        }
        else
        {
            goalSummary = userGoals.Count > 0 ? string.Join("; ", userGoals) : "Continue task implementation and verification.";
        }

        var actionsSummary = assistantActions.Count > 0
            ? string.Join(", ", assistantActions)
            : "Explored codebase and executed planned steps.";

        var doneSection = assistantActions.Count > 0
            ? $"- Executed actions: {actionsSummary}"
            : "- Prior completed actions preserved in checkpoint.";

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            var prevDone = ExtractSection(previousSummary, "### Done", "### In Progress");
            if (!string.IsNullOrWhiteSpace(prevDone))
            {
                doneSection = $"{prevDone}\n{doneSection}".Trim();
            }
        }

        return $@"## Goal
{goalSummary}

## Constraints & Preferences
- (none)

## Progress
### Done
{doneSection}

### In Progress
- Resuming active stage execution from checkpoint.

### Blocked
- (none)

## Key Decisions
- **Compacted Context**: History prior to this turn was compacted to preserve context headroom.

## Next Steps
1. Continue executing remaining tool calls and task verification.";
    }

    private static string? ExtractSection(string text, string startHeader, string endHeader)
    {
        var startIdx = text.IndexOf(startHeader, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return null;
        startIdx += startHeader.Length;

        var endIdx = text.IndexOf(endHeader, startIdx, StringComparison.OrdinalIgnoreCase);
        var section = endIdx >= 0 ? text[startIdx..endIdx] : text[startIdx..];
        return section.Trim();
    }

    private static string StripCheckpointPreamble(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        const string marker = "[CONTEXT COMPACTION CHECKPOINT]";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var headerIdx = text.IndexOf("## Goal", idx, StringComparison.OrdinalIgnoreCase);
            if (headerIdx >= 0)
            {
                return text[headerIdx..].Trim();
            }
            var nlIdx = text.IndexOf("\n\n", idx, StringComparison.Ordinal);
            if (nlIdx >= 0)
            {
                return text[(nlIdx + 2)..].Trim();
            }
        }
        return text.Trim();
    }

    /// <summary>True only for orphan tool results: a group holding results but not the originating call (call+result groups are atomic and safe to cut at).</summary>
    private static bool ContainsToolResult(CompactionMessageGroup group) =>
        group.Messages.Any(m => m.Role == ChatRole.Tool || m.Contents.Any(c => c is FunctionResultContent))
        && !group.Messages.Any(m => m.Contents.Any(c => c is FunctionCallContent));
}
