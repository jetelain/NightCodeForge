using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Workflow;
using Pmad.AI.Tokens;

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
        ConversationHistorySerializer.CompactionCheckpointHeader + "\nThe conversation history prior to this point was compacted into the following summary:\n\n";

    public const string CompactionExcludeReason = "PiStyleCompaction";

    /// <summary>Maximum characters kept per user message in the deterministic fallback summary.</summary>
    public const int MaxDeterministicGoalChars = 500;

    private static readonly Func<IList<ChatMessage>, Microsoft.ML.Tokenizers.Tokenizer?, CompactionMessageIndex> _createIndexDelegate = CreateIndexFactory();

    private static Func<IList<ChatMessage>, Microsoft.ML.Tokenizers.Tokenizer?, CompactionMessageIndex> CreateIndexFactory()
    {
        var method = typeof(CompactionMessageIndex).GetMethod(
            "Create",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IList<ChatMessage>), typeof(Microsoft.ML.Tokenizers.Tokenizer)])
            ?? throw new InvalidOperationException("Failed to locate CompactionMessageIndex.Create(IList<ChatMessage>, Tokenizer?) factory method.");

        return method.CreateDelegate<Func<IList<ChatMessage>, Microsoft.ML.Tokenizers.Tokenizer?, CompactionMessageIndex>>();
    }

    /// <summary>
    /// Creates a <see cref="CompactionMessageIndex"/> from a list of <see cref="ChatMessage"/> instances using MAF's internal grouping engine.
    /// </summary>
    public static CompactionMessageIndex CreateIndex(IList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return _createIndexDelegate(messages, null);
    }

    public sealed record CompactionCheckpointNotification(
        ChatMessage SummaryMessage,
        IReadOnlyList<ChatMessage> IncludedMessages,
        string? FirstKeptCallId,
        int KeptRecentMessageCount,
        int KeptRecentGroupCount,
        int SummarizedGroupCount,
        int SummarizedMessageCount,
        int PreCompactionTokens,
        int PostCompactionTokens,
        double DurationMs);

    private sealed class MeasuredTokenState
    {
        public readonly object Lock = new();
        public int InputTokens;
        public int OutputTokens;
        public int MessageCount;
        public int ResponseMessageCount;
        public bool ColdResumeArmed;
    }

    private readonly CrucibleCompactionOptions _options;
    private readonly IChatClient? _summarizerClient;
    private readonly ILogger _logger;
    private readonly MeasuredTokenState _measurementState;
    private int _lastCompactedTurnCount = -1;
    private int _lastCompactedIncludedTurnCount = -1;
    private int _lastCompactedTokenCount = 0;
    private int _lastCompactedIncludedTokenCount = 0;
    private long _totalSummarizerInputTokens = 0;
    private long _totalSummarizerOutputTokens = 0;

    public CrucibleCompactionOptions Options => _options;
    public int LastCompactedTurnCount => _lastCompactedTurnCount;
    public int LastCompactedTokenCount => _lastCompactedTokenCount;
    public long TotalSummarizerInputTokens => Interlocked.Read(ref _totalSummarizerInputTokens);
    public long TotalSummarizerOutputTokens => Interlocked.Read(ref _totalSummarizerOutputTokens);
    public Func<int, int, CancellationToken, Task>? OnCompacting { get; set; }
    public Func<CompactionCheckpointNotification, CancellationToken, Task>? OnCompacted { get; set; }

    public bool IsColdResumeCompactionArmed
    {
        get { lock (_measurementState.Lock) { return _measurementState.ColdResumeArmed; } }
    }

    public int LastMeasuredInputTokens
    {
        get { lock (_measurementState.Lock) { return _measurementState.InputTokens; } }
    }

    public int LastMeasuredOutputTokens
    {
        get { lock (_measurementState.Lock) { return _measurementState.OutputTokens; } }
    }

    public int LastMeasuredMessageCount
    {
        get { lock (_measurementState.Lock) { return _measurementState.MessageCount; } }
    }

    public int LastMeasuredResponseMessageCount
    {
        get { lock (_measurementState.Lock) { return _measurementState.ResponseMessageCount; } }
    }

    public PiStyleCrucibleCompactionStrategy(
        CrucibleCompactionOptions options,
        IChatClient? summarizerClient,
        ILogger logger)
        : this(RequireOptions(options), summarizerClient, logger ?? throw new ArgumentNullException(nameof(logger)), new MeasuredTokenState())
    {
    }

    private PiStyleCrucibleCompactionStrategy(
        CrucibleCompactionOptions options,
        IChatClient? summarizerClient,
        ILogger logger,
        MeasuredTokenState measurementState)
        : base(CreateTrigger(options, measurementState))
    {
        _options = options;
        _summarizerClient = summarizerClient;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _measurementState = measurementState;
    }

    private static CompactionTrigger CreateTrigger(
        CrucibleCompactionOptions options,
        MeasuredTokenState measurementState)
    {
        return index => ShouldCompact(index, options, measurementState);
    }

    private static CrucibleCompactionOptions RequireOptions(CrucibleCompactionOptions? options) =>
        options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Arms a one-shot cold-cache resumption compaction gate. When armed, the strategy will compact
    /// before the next LLM call if the restored history meets or exceeds the cold-resume threshold (128k ceiling),
    /// even when total tokens are still below the warm-cache <see cref="CrucibleCompactionOptions.TriggerThresholdTokens"/>.
    /// </summary>
    public void ArmColdResumeCompaction()
    {
        lock (_measurementState.Lock)
        {
            _measurementState.ColdResumeArmed = true;
        }
    }

    /// <summary>
    /// Disarms the cold-cache resumption compaction gate.
    /// </summary>
    public void DisarmColdResumeCompaction() => DisarmColdResumeCompaction(_measurementState);

    private static void DisarmColdResumeCompaction(MeasuredTokenState measurementState)
    {
        lock (measurementState.Lock)
        {
            measurementState.ColdResumeArmed = false;
        }
    }

    private static int GetWindowCeilingTokens(CrucibleCompactionOptions options)
    {
        int triggerCeiling = Math.Max(1, options.TriggerThresholdTokens);
        return options.ContextWindowTokens > 0
            ? Math.Min(options.ContextWindowTokens, triggerCeiling)
            : triggerCeiling;
    }

    public static int GetColdResumeThresholdTokens(CrucibleCompactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int coldThreshold = options.ColdResumeThresholdTokens > 0
            ? options.ColdResumeThresholdTokens
            : CrucibleCompactionOptions.DefaultColdResumeThresholdTokens;
        return Math.Min(GetWindowCeilingTokens(options), coldThreshold);
    }

    public static int GetEffectiveCompactedCeilingTokens(CrucibleCompactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        int maxCompacted = options.MaxCompactedContextTokens > 0
            ? options.MaxCompactedContextTokens
            : CrucibleCompactionOptions.DefaultMaxCompactedContextTokens;
        return Math.Min(GetWindowCeilingTokens(options), maxCompacted);
    }

    /// <summary>
    /// Evaluates whether the conversation index requires compaction based on the projected prompt token count
    /// for the next LLM request (<see cref="CalculateProjectedPromptTokens(CompactionMessageIndex)"/> &gt; <see cref="CrucibleCompactionOptions.TriggerThresholdTokens"/>),
    /// or when a cold-cache resumption gate is armed and the context meets or exceeds the cold-resume threshold (128k ceiling).
    /// </summary>
    public bool ShouldCompact(CompactionMessageIndex index) =>
        ShouldCompact(index, _options, _measurementState);

    private static bool ShouldCompact(
        CompactionMessageIndex index,
        CrucibleCompactionOptions options,
        MeasuredTokenState measurementState)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!options.Enabled) return false;

        int projected = CalculateProjectedPromptTokens(index, options, measurementState);
        bool coldResumeArmed;
        lock (measurementState.Lock)
        {
            coldResumeArmed = measurementState.ColdResumeArmed;
        }

        if (coldResumeArmed)
        {
            if (projected >= GetColdResumeThresholdTokens(options))
            {
                return true;
            }

            // Below cold-resume threshold on resume: disarm and resume directly without redundant compaction.
            DisarmColdResumeCompaction(measurementState);
        }

        return projected > options.TriggerThresholdTokens;
    }

    /// <summary>
    /// Records the server-measured token usage from the latest completed LLM call so the next turn's compaction check
    /// projects from the authoritative server baseline plus the <c>Pmad.AI</c> delta of newly appended messages.
    /// </summary>
    public void RecordMeasuredCallTokens(
        int inputTokens,
        int outputTokens,
        int sentIncludedMessageCount,
        int responseMessageCount = 1)
    {
        lock (_measurementState.Lock)
        {
            _measurementState.InputTokens = Math.Max(0, inputTokens);
            _measurementState.OutputTokens = Math.Max(0, outputTokens);
            _measurementState.MessageCount = Math.Max(0, sentIncludedMessageCount);
            _measurementState.ResponseMessageCount = Math.Max(1, responseMessageCount);
        }
    }

    /// <summary>
    /// Resets the server-measured token baseline (e.g. immediately after compaction rewrites the included history).
    /// </summary>
    public void ResetMeasuredBaseline()
    {
        lock (_measurementState.Lock)
        {
            _measurementState.InputTokens = 0;
            _measurementState.OutputTokens = 0;
            _measurementState.MessageCount = 0;
            _measurementState.ResponseMessageCount = 0;
        }
    }

    /// <summary>
    /// Resolves the model-calibrated <see cref="HeuristicTokenEstimator"/> for this strategy's model.
    /// </summary>
    public HeuristicTokenEstimator ResolveTokenEstimator() =>
        ModelCalibratedTokenEstimator.Resolve(_options.SummarizerModelId);

    /// <summary>
    /// Calculates the projected prompt token count for the next LLM request by combining the previous turn's
    /// server-measured prompt/output tokens with the model-calibrated <see cref="HeuristicTokenEstimator"/> delta
    /// for any newly appended messages (or full-history estimation when no baseline is present).
    /// </summary>
    public int CalculateProjectedPromptTokens(CompactionMessageIndex index) =>
        CalculateProjectedPromptTokens(index, _options, _measurementState);

    private static int CalculateProjectedPromptTokens(
        CompactionMessageIndex index,
        CrucibleCompactionOptions options,
        MeasuredTokenState measurementState)
    {
        ArgumentNullException.ThrowIfNull(index);
        var estimator = ModelCalibratedTokenEstimator.Resolve(options.SummarizerModelId);

        var includedMessages = new List<ChatMessage>();
        int fullEstimatedTokens = 0;

        foreach (var group in index.Groups)
        {
            if (group.IsExcluded) continue;
            fullEstimatedTokens += ModelCalibratedTokenEstimator.EstimateGroupTokens(group, estimator);
            foreach (var msg in group.Messages)
            {
                includedMessages.Add(msg);
            }
        }

        int measuredInput;
        int measuredOutput;
        int measuredMsgCount;
        int measuredRespMsgCount;
        lock (measurementState.Lock)
        {
            measuredInput = measurementState.InputTokens;
            measuredOutput = measurementState.OutputTokens;
            measuredMsgCount = measurementState.MessageCount;
            measuredRespMsgCount = measurementState.ResponseMessageCount;
        }

        if (measuredInput > 0 && includedMessages.Count >= measuredMsgCount)
        {
            int projected = measuredInput;
            int responseCount = Math.Min(measuredRespMsgCount, Math.Max(0, includedMessages.Count - measuredMsgCount));

            if (measuredOutput > 0)
            {
                projected += measuredOutput;
            }
            else if (responseCount > 0)
            {
                for (int i = measuredMsgCount; i < measuredMsgCount + responseCount; i++)
                {
                    projected += ModelCalibratedTokenEstimator.EstimateMessageTokens(includedMessages[i], estimator);
                }
            }

            int accountedMessageCount = measuredMsgCount + responseCount;
            for (int i = accountedMessageCount; i < includedMessages.Count; i++)
            {
                projected += ModelCalibratedTokenEstimator.EstimateMessageTokens(includedMessages[i], estimator);
            }

            return Math.Max(projected, fullEstimatedTokens);
        }

        return fullEstimatedTokens;
    }

    /// <summary>
    /// Calculates the dynamic budget of recent tokens to preserve verbatim.
    /// Invariant: AnchorTokens + EffectiveKeepRecent + CheckpointSummary &lt;= Math.Min(TriggerThresholdTokens, MaxCompactedContextTokens) - HeadroomMargin.
    /// </summary>
    public int CalculateEffectiveKeepRecentTokens(int anchorTokens)
    {
        int targetCeiling = GetEffectiveCompactedCeilingTokens(_options);
        int effectiveSafetyMargin = _options.SafetyMarginTokens;
        if (targetCeiling <= _options.SafetyMarginTokens)
        {
            effectiveSafetyMargin = Math.Max(0, targetCeiling / 4);
        }

        int dynamicBudget = Math.Max(0, targetCeiling - anchorTokens - effectiveSafetyMargin);
        return Math.Min(_options.KeepRecentTokens, dynamicBudget);
    }

    /// <summary>
    /// Executes an on-demand emergency compaction pass over an active <see cref="CompactionMessageIndex"/>,
    /// bypassing regular trigger thresholds (<see cref="CrucibleCompactionOptions.TriggerThresholdTokens"/>, <see cref="CrucibleCompactionOptions.ColdResumeThresholdTokens"/>)
    /// and debounce cooldowns (<see cref="CrucibleCompactionOptions.DebounceTurnCooldown"/>, <see cref="CrucibleCompactionOptions.DebounceTokenDelta"/>),
    /// while preserving all task anchor, tool atomicity, tool result clamping, and 96k headroom invariants.
    /// </summary>
    public Task<bool> CompactEmergencyAsync(CompactionMessageIndex index, CancellationToken cancellationToken = default) =>
        CompactEmergencyAsync(index, _logger, cancellationToken);

    /// <summary>
    /// Executes an on-demand emergency compaction pass over an active <see cref="CompactionMessageIndex"/> with a specified logger.
    /// </summary>
    public async Task<bool> CompactEmergencyAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (!_options.EnableTimeoutRecoveryCompaction) return false;

        var effectiveLogger = logger ?? _logger;
        try
        {
            return await ExecuteCompactionPassAsync(
                index,
                isEmergencyPass: true,
                isColdResumeCompaction: false,
                effectiveLogger,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            effectiveLogger.LogError(ex, "Emergency context compaction pass encountered an unhandled exception.");
            return false;
        }
    }

    /// <summary>
    /// Executes an on-demand emergency compaction pass directly over an active conversation history (<see cref="IList{ChatMessage}"/>),
    /// constructing a <see cref="CompactionMessageIndex"/>, executing emergency compaction, and mutating the list in place with the compacted history.
    /// </summary>
    public async Task<bool> CompactEmergencyAsync(
        IList<ChatMessage> chatHistory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);
        if (!_options.EnableTimeoutRecoveryCompaction) return false;

        try
        {
            var index = CreateIndex(chatHistory);
            bool success = await CompactEmergencyAsync(index, _logger, cancellationToken);
            if (success)
            {
                var included = index.GetIncludedMessages().ToList();
                if (!chatHistory.SequenceEqual(included))
                {
                    chatHistory.Clear();
                    foreach (var msg in included)
                    {
                        chatHistory.Add(msg);
                    }
                }
            }
            return success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Emergency context compaction failed during index creation or execution.");
            return false;
        }
    }

    protected override async ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var effectiveLogger = _logger;
        if (!_options.Enabled) return false;

        int projectedPromptTokens = CalculateProjectedPromptTokens(index);
        bool isColdResumeCompaction = false;
        if (IsColdResumeCompactionArmed)
        {
            if (projectedPromptTokens >= GetColdResumeThresholdTokens(_options))
            {
                isColdResumeCompaction = true;
            }
            DisarmColdResumeCompaction();
        }

        // Check if projected prompt token count for the next request exceeds trigger threshold (or cold-resume gate)
        if (!isColdResumeCompaction && projectedPromptTokens <= _options.TriggerThresholdTokens)
        {
            return false;
        }

        return await ExecuteCompactionPassAsync(
            index,
            isEmergencyPass: false,
            isColdResumeCompaction: isColdResumeCompaction,
            effectiveLogger,
            cancellationToken);
    }

    private async ValueTask<bool> ExecuteCompactionPassAsync(
        CompactionMessageIndex index,
        bool isEmergencyPass,
        bool isColdResumeCompaction,
        ILogger effectiveLogger,
        CancellationToken cancellationToken)
    {
        int projectedPromptTokens = CalculateProjectedPromptTokens(index);
        if (!isEmergencyPass && !isColdResumeCompaction && projectedPromptTokens <= _options.TriggerThresholdTokens)
        {
            return false;
        }

        var groups = index.Groups;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (!groups[i].IsExcluded
                && groups[i].Kind == CompactionGroupKind.ToolCall
                && groups[i].Messages.Count >= 4)
            {
                SplitConsecutiveToolCallChain(index, i);
            }
        }
        groups = index.Groups;

        if (groups.Count <= 2)
        {
            // Too few groups to compact without destroying prompt anchors
            return false;
        }

        var estimator = ResolveTokenEstimator();

        // Count assistant turns and estimated tokens across active (non-excluded) groups so deltas remain
        // invariant whether CompactionMessageIndex retains excluded groups or is rebuilt from included messages.
        int totalAssistantTurns = groups.Sum(g => g.Messages.Count(m => m.Role == ChatRole.Assistant));
        int includedAssistantTurns = groups
            .Where(g => !g.IsExcluded)
            .Sum(g => g.Messages.Count(m => m.Role == ChatRole.Assistant));
        int includedEstimatedTokens = groups
            .Where(g => !g.IsExcluded)
            .Sum(g => ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator));

        bool historyReset = _lastCompactedIncludedTurnCount >= 0
            && includedAssistantTurns < _lastCompactedIncludedTurnCount;
        int turnsSinceLastCompaction = (_lastCompactedIncludedTurnCount >= 0 && !historyReset)
            ? includedAssistantTurns - _lastCompactedIncludedTurnCount
            : includedAssistantTurns;
        int tokensAddedSinceLastCompaction = (_lastCompactedIncludedTurnCount >= 0 && !historyReset)
            ? Math.Max(0, includedEstimatedTokens - _lastCompactedIncludedTokenCount)
            : includedEstimatedTokens;

        // Evaluate emergency context limit on projected prompt tokens
        int emergencyThresholdTokens = Math.Min(
            _options.ContextWindowTokens,
            (int)(_options.ContextWindowTokens * _options.EmergencyThresholdRatio));

        bool hasMeasuredServerBaseline = LastMeasuredInputTokens > 0;
        bool isEmergency = isEmergencyPass
            || isColdResumeCompaction
            || projectedPromptTokens >= emergencyThresholdTokens
            || (hasMeasuredServerBaseline
                && projectedPromptTokens > _options.TriggerThresholdTokens
                && tokensAddedSinceLastCompaction >= _options.DebounceTokenDelta);

        if (!isEmergency && _lastCompactedTurnCount >= 0 && !historyReset)
        {
            if (turnsSinceLastCompaction < _options.DebounceTurnCooldown)
            {
                effectiveLogger.LogDebug(
                    "Compaction skipped by turn cooldown. Turns since last: {Turns}, Cooldown: {Cooldown}",
                    turnsSinceLastCompaction, _options.DebounceTurnCooldown);
                return false;
            }

            if (tokensAddedSinceLastCompaction < _options.DebounceTokenDelta)
            {
                effectiveLogger.LogDebug(
                    "Compaction skipped by token delta debounce. Tokens added: {TokensAdded}, Min delta: {MinDelta}",
                    tokensAddedSinceLastCompaction, _options.DebounceTokenDelta);
                return false;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Tier 2 (Compaction Emergency): Clamp oversized tool results in active groups
        ClampOversizedToolResults(groups, _options.EmergencyToolResultMaxChars, _options.ToolResultMaxChars, effectiveLogger);

        // 1. Locate Task Anchor (System messages + initial User message)
        int anchorEndIndex = -1;
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.Kind is CompactionGroupKind.System or CompactionGroupKind.User && !IsSummaryGroup(g))
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
            tokensAnchor += ModelCalibratedTokenEstimator.EstimateGroupTokens(groups[i], estimator);
        }

        int effectiveKeepRecent = CalculateEffectiveKeepRecentTokens(tokensAnchor);

        // Check if there is an existing active (non-excluded) summary group from a prior compaction pass
        CompactionMessageGroup? existingSummaryGroup = null;
        string? previousSummary = null;
        for (int i = anchorEndIndex + 1; i < groups.Count; i++)
        {
            if (IsSummaryGroup(groups[i]) && !groups[i].IsExcluded)
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

            accumulatedRecentTokens += ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator);

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

        // If the cut point cannot preserve any groups to summarize before it, advance cutIndex to preserve at least
        // the newest turn(s) so the older history can be compacted.
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
            if (!g.IsExcluded && !IsSummaryGroup(g))
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

        // 5. Pre-Serialize Messages to Text with Clamped Tool Results & 96k Summarizer Token Budget
        var messagesToSummarize = groupsToSummarize.SelectMany(g => g.Messages).ToList();
        int maxSummarizerTokens = _options.MaxSummarizerInputTokens > 0
            ? _options.MaxSummarizerInputTokens
            : CrucibleCompactionOptions.DefaultMaxSummarizerInputTokens;
        int overheadTokens = EstimateSummarizerOverheadTokens(previousSummary, estimator);
        int maxTranscriptTokens = Math.Max(1024, maxSummarizerTokens - overheadTokens);
        var serializedTranscript = ConversationSerializer.SerializeConversation(
            messagesToSummarize,
            _options.ToolResultMaxChars,
            maxTranscriptTokens,
            _options.SummarizerModelId);

        effectiveLogger.LogInformation(
            "Starting Pi-style context compaction (Emergency: {IsEmergency}, ColdResume: {IsColdResume}, PreCompactionTokens: {PreTokens}, TriggerThreshold: {Threshold}, SummarizingGroups: {GroupCount}, SummarizingMessages: {MessageCount}).",
            isEmergencyPass,
            isColdResumeCompaction,
            projectedPromptTokens,
            _options.TriggerThresholdTokens,
            groupsToSummarize.Count,
            messagesToSummarize.Count);

        if (OnCompacting != null)
        {
            await OnCompacting(projectedPromptTokens, messagesToSummarize.Count, cancellationToken);
        }

        // 6. Generate Summary (via LLM or deterministic fallback)
        string summaryBody;
        if (_summarizerClient != null)
        {
            try
            {
                summaryBody = await GenerateLlmSummaryAsync(serializedTranscript, previousSummary, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                effectiveLogger.LogWarning(ex, "LLM summarization failed during context compaction. Falling back to deterministic structured summary.");
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

        // 9. Enforce Post-Compaction 96k Token Ceiling on active included context
        EnforcePostCompactionTokenCeiling(groups, anchorEndIndex + 1, estimator, effectiveLogger);

        int postCompactionTokens = groups
            .Where(g => !g.IsExcluded)
            .Sum(g => ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator));

        if (isEmergencyPass && (postCompactionTokens >= includedEstimatedTokens || postCompactionTokens >= projectedPromptTokens))
        {
            index.Groups.RemoveAt(anchorEndIndex + 1);
            foreach (var g in groupsToSummarize)
            {
                g.IsExcluded = false;
                g.ExcludeReason = null;
            }
            if (existingSummaryGroup != null)
            {
                existingSummaryGroup.IsExcluded = false;
                existingSummaryGroup.ExcludeReason = null;
            }

            effectiveLogger.LogWarning(
                "Emergency context compaction did not reduce context size: pre {PreTokens} (estimated {PreEstimatedTokens}), post {PostTokens}",
                projectedPromptTokens, includedEstimatedTokens, postCompactionTokens);
            return false;
        }

        sw.Stop();
        _lastCompactedTurnCount = totalAssistantTurns;
        _lastCompactedIncludedTurnCount = groups
            .Where(g => !g.IsExcluded)
            .Sum(g => g.Messages.Count(m => m.Role == ChatRole.Assistant));
        _lastCompactedIncludedTokenCount = postCompactionTokens;
        _lastCompactedTokenCount = groups.Sum(g => ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator));
        ResetMeasuredBaseline();
        DisarmColdResumeCompaction();

        effectiveLogger.LogInformation(
            "Pi-style context compaction succeeded in {DurationMs:F0}ms. Pre-compaction: {PreTokens} tokens -> Post-compaction: {PostTokens} tokens. Summarized {GroupCount} groups ({MessageCount} messages). Injected summary with {FileOpsLength} file ops characters.",
            sw.Elapsed.TotalMilliseconds,
            projectedPromptTokens,
            _lastCompactedIncludedTokenCount,
            groupsToSummarize.Count,
            messagesToSummarize.Count,
            fileOpsXml.Length);

        var keptGroups = groups
            .Skip(anchorEndIndex + 2)
            .Where(g => !g.IsExcluded && !IsSummaryGroup(g))
            .ToList();
        var keptMessages = keptGroups.SelectMany(g => g.Messages).ToList();
        var firstKeptCallId = keptMessages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(fc => fc.CallId)
            .FirstOrDefault(cid => !string.IsNullOrWhiteSpace(cid));
        var includedMessagesSnapshot = index.GetIncludedMessages().ToList();

        if (OnCompacted != null)
        {
            await OnCompacted(
                new CompactionCheckpointNotification(
                    SummaryMessage: summaryMessage,
                    IncludedMessages: includedMessagesSnapshot,
                    FirstKeptCallId: firstKeptCallId,
                    KeptRecentMessageCount: keptMessages.Count,
                    KeptRecentGroupCount: keptGroups.Count,
                    SummarizedGroupCount: groupsToSummarize.Count,
                    SummarizedMessageCount: messagesToSummarize.Count,
                    PreCompactionTokens: projectedPromptTokens,
                    PostCompactionTokens: _lastCompactedIncludedTokenCount,
                    DurationMs: sw.Elapsed.TotalMilliseconds),
                cancellationToken);
        }

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
                            frc.Result = resStr[..targetLen] + $"\n{ModelCalibratedTokenEstimator.ClampedOutputMarker}{targetLen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters]";
                        }
                    }
                    else if (content is TextContent tc && tc.Text != null && msg.Role == ChatRole.Tool)
                    {
                        if (tc.Text.Length > emergencyMaxChars)
                        {
                            int targetLen = Math.Min(clampedTargetChars, tc.Text.Length);
                            logger.LogInformation("Clamping emergency tool text content from {Length} to {Target} characters",
                                tc.Text.Length, targetLen);
                            tc.Text = tc.Text[..targetLen] + $"\n{ModelCalibratedTokenEstimator.ClampedOutputMarker}{targetLen.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters]";
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

    private static void SplitConsecutiveToolCallChain(CompactionMessageIndex index, int groupIndex)
    {
        var originalGroup = index.Groups[groupIndex];
        var messages = originalGroup.Messages.ToList();

        var stepBatches = new List<List<ChatMessage>>();
        var currentBatch = new List<ChatMessage>();
        bool seenToolResultInBatch = false;

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Assistant && seenToolResultInBatch && currentBatch.Count > 0)
            {
                stepBatches.Add(currentBatch);
                currentBatch = new List<ChatMessage>();
                seenToolResultInBatch = false;
            }

            currentBatch.Add(msg);
            if (msg.Role == ChatRole.Tool || msg.Contents.Any(c => c is FunctionResultContent))
            {
                seenToolResultInBatch = true;
            }
        }

        if (currentBatch.Count > 0)
        {
            stepBatches.Add(currentBatch);
        }

        if (stepBatches.Count <= 1)
        {
            return;
        }

        index.Groups.RemoveAt(groupIndex);
        for (int i = 0; i < stepBatches.Count; i++)
        {
            index.InsertGroup(groupIndex + i, CompactionGroupKind.ToolCall, stepBatches[i], null);
        }
    }

    private void EnforcePostCompactionTokenCeiling(
        IList<CompactionMessageGroup> groups,
        int summaryGroupIndex,
        HeuristicTokenEstimator estimator,
        ILogger logger)
    {
        int ceiling = GetEffectiveCompactedCeilingTokens(_options);
        int currentTokens = groups
            .Where(g => !g.IsExcluded)
            .Sum(g => ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator));

        if (currentTokens < ceiling)
        {
            return;
        }

        // Step 1: Progressively clamp tool outputs in kept-recent groups
        int[] progressiveCaps = [2000, 1000, 500, 250];
        foreach (var cap in progressiveCaps)
        {
            ClampOversizedToolResults(groups, cap, cap, logger);
            currentTokens = groups
                .Where(g => !g.IsExcluded)
                .Sum(g => ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator));
            if (currentTokens < ceiling)
            {
                return;
            }
        }

        // Step 2: Clamp oversized tool arguments or assistant/user message payloads in kept-recent groups
        for (int i = summaryGroupIndex + 1; i < groups.Count && currentTokens >= ceiling; i++)
        {
            var group = groups[i];
            if (group.IsExcluded) continue;

            foreach (var msg in group.Messages)
            {
                for (int cIdx = 0; cIdx < msg.Contents.Count; cIdx++)
                {
                    if (msg.Contents[cIdx] is TextContent tc && !string.IsNullOrEmpty(tc.Text) && tc.Text.Length > 1000)
                    {
                        tc.Text = ConversationSerializer.TruncateForSummary(tc.Text, 1000);
                    }
                }
            }

            currentTokens = groups
                .Where(g => !g.IsExcluded)
                .Sum(g => ModelCalibratedTokenEstimator.EstimateGroupTokens(g, estimator));
        }
    }

    private int EstimateSummarizerOverheadTokens(string? previousSummary, HeuristicTokenEstimator estimator)
    {
        int systemTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(SummarizationSystemPrompt, estimator) + 16;
        int templateTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(
            string.IsNullOrWhiteSpace(previousSummary) ? SummarizationPromptTemplate : UpdateSummarizationPromptTemplate,
            estimator) + 32;
        int prevSummaryTokens = string.IsNullOrWhiteSpace(previousSummary)
            ? 0
            : ModelCalibratedTokenEstimator.EstimateTextTokens(previousSummary, estimator) + 32;
        return systemTokens + templateTokens + prevSummaryTokens;
    }

    private async Task<string> GenerateLlmSummaryAsync(
        string serializedTranscript,
        string? previousSummary,
        CancellationToken cancellationToken)
    {
        var estimator = ResolveTokenEstimator();
        int maxSummarizerTokens = _options.MaxSummarizerInputTokens > 0
            ? _options.MaxSummarizerInputTokens
            : CrucibleCompactionOptions.DefaultMaxSummarizerInputTokens;

        string effectivePreviousSummary = previousSummary ?? string.Empty;
        int overheadTokens = EstimateSummarizerOverheadTokens(effectivePreviousSummary, estimator);
        if (overheadTokens > maxSummarizerTokens / 2 && !string.IsNullOrWhiteSpace(effectivePreviousSummary))
        {
            int maxPrevSummaryChars = Math.Max(1000, (maxSummarizerTokens / 4) * 2);
            effectivePreviousSummary = ConversationSerializer.TruncateForSummary(effectivePreviousSummary, maxPrevSummaryChars);
            overheadTokens = EstimateSummarizerOverheadTokens(effectivePreviousSummary, estimator);
        }

        int maxTranscriptTokens = Math.Max(512, maxSummarizerTokens - overheadTokens);
        string effectiveTranscript = serializedTranscript;
        if (ModelCalibratedTokenEstimator.EstimateTextTokens(effectiveTranscript, estimator) > maxTranscriptTokens)
        {
            var paragraphs = effectiveTranscript.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            effectiveTranscript = ConversationSerializer.ElideMiddlePartsToFitTokenBudget(paragraphs, maxTranscriptTokens, estimator);
        }

        string promptText = $"<conversation>\n{effectiveTranscript}\n</conversation>\n\n";
        if (!string.IsNullOrWhiteSpace(effectivePreviousSummary))
        {
            promptText += $"<previous-summary>\n{effectivePreviousSummary}\n</previous-summary>\n\n{UpdateSummarizationPromptTemplate}";
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
            MaxOutputTokens = Math.Min(8192, Math.Max(1024, (int)(_options.ReserveTokens * 0.8))),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false,
                ["reasoning_effort"] = "low"
            }
        };

        var response = await _summarizerClient!.GetResponseAsync(messages, options, cancellationToken);
        if (response.Usage != null)
        {
            if (response.Usage.InputTokenCount.HasValue && response.Usage.InputTokenCount.Value > 0)
            {
                Interlocked.Add(ref _totalSummarizerInputTokens, response.Usage.InputTokenCount.Value);
            }
            if (response.Usage.OutputTokenCount.HasValue && response.Usage.OutputTokenCount.Value > 0)
            {
                Interlocked.Add(ref _totalSummarizerOutputTokens, response.Usage.OutputTokenCount.Value);
            }
        }

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
                     && !m.Text.StartsWith(ConversationHistorySerializer.CompactionCheckpointHeader, StringComparison.OrdinalIgnoreCase))
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
        var idx = text.IndexOf(ConversationHistorySerializer.CompactionCheckpointHeader, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Returns true if the message is a compaction checkpoint summary message.
    /// </summary>
    public static bool IsCompactionCheckpointMessage(ChatMessage? message)
    {
        if (message == null) return false;
        if (!string.IsNullOrWhiteSpace(message.Text)
            && message.Text.TrimStart().StartsWith(ConversationHistorySerializer.CompactionCheckpointHeader, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.Contents != null)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent tc
                    && !string.IsNullOrWhiteSpace(tc.Text)
                    && tc.Text.TrimStart().StartsWith(ConversationHistorySerializer.CompactionCheckpointHeader, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the group is a compaction checkpoint summary group, either by <see cref="CompactionGroupKind.Summary"/>
    /// or by containing a checkpoint message after <see cref="CompactionMessageIndex"/> is rebuilt from flat <see cref="ChatMessage"/>s.
    /// </summary>
    public static bool IsSummaryGroup(CompactionMessageGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Kind == CompactionGroupKind.Summary)
        {
            return true;
        }

        foreach (var message in group.Messages)
        {
            if (IsCompactionCheckpointMessage(message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True only for orphan tool results: a group holding results but not the originating call (call+result groups are atomic and safe to cut at).</summary>
    private static bool ContainsToolResult(CompactionMessageGroup group) =>
        group.Messages.Any(m => m.Role == ChatRole.Tool || m.Contents.Any(c => c is FunctionResultContent))
        && !group.Messages.Any(m => m.Contents.Any(c => c is FunctionCallContent));
}
