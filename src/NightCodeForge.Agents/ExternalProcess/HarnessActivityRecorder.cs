using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.ExternalProcess;

/// <summary>
/// Cohesive collaborator for external-process harnesses that broadcasts live agent activity,
/// persists <see cref="Conversation"/> / <see cref="ConversationTurn"/> rows in <see cref="ITaskRepository"/>,
/// and saves markdown session transcripts.
/// </summary>
public class HarnessActivityRecorder : IHarnessActivityRecorder
{
    private const string TranscriptRoleUser = "User";
    private const string TranscriptRoleAssistant = "Assistant";
    private const string TranscriptRoleThinking = "THINKING";
    private const string TranscriptRoleTool = "TOOL";
    private const string TranscriptRoleToolResult = "TOOL_RESULT";
    private const string TranscriptRoleToolError = "TOOL_ERROR";
    private const string CancelledSupersededSummary = "Cancelled superseded paused conversation";
    private const string DocsFolder = "docs";
    private const string TranscriptsFolder = "transcripts";

    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly ISessionTranscriptService _transcriptService;
    private readonly ITaskRepository _taskRepository;
    private readonly ILogger<HarnessActivityRecorder> _logger;
    private readonly ConcurrentDictionary<string, RecorderSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _beginLock = new(1, 1);

    public HarnessActivityRecorder(
        IAgentActivityStreamer activityStreamer,
        ISessionTranscriptService transcriptService,
        ITaskRepository taskRepository,
        ILogger<HarnessActivityRecorder> logger)
    {
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken cancellationToken)
        => BroadcastAsync(sessionId, taskId, stageId, kind, content, metadata: null, cancellationToken);

    public Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        IReadOnlyDictionary<string, object?>? metadata,
        CancellationToken cancellationToken)
    {
        var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow, metadata);
        return _activityStreamer.BroadcastAsync(evt, cancellationToken);
    }

    public async Task BeginConversationAsync(
        string sessionId,
        string harnessId,
        AgentSessionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(context);

        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        await _beginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.ContainsKey(sessionId))
            {
                return;
            }

            int attemptIndex = (context.PipelineContext?.StageRetries.GetValueOrDefault(context.StageId, 0) ?? 0) + 1;
            int? persistedTaskId = context.ForgeTask.Id > 0 ? context.ForgeTask.Id : null;
            Conversation? conversation = null;

            if (persistedTaskId.HasValue)
            {
                var resumable = await _taskRepository.GetResumableConversationAsync(persistedTaskId.Value, context.StageId, cancellationToken)
                    .ConfigureAwait(false);
                if (resumable != null)
                {
                    if (resumable.Status == StageExecutionStatus.WaitingForHuman)
                    {
                        if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
                        {
                            conversation = resumable;
                            await _taskRepository.ResumeConversationAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await _taskRepository.CompleteConversationAsync(
                                resumable.Id,
                                StageExecutionStatus.Cancelled,
                                summary: CancelledSupersededSummary,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else if (resumable.Status == StageExecutionStatus.Interrupted)
                    {
                        conversation = resumable;
                        await _taskRepository.ResumeConversationAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (conversation == null)
                {
                    var existing = await _taskRepository.GetConversationsAsync(persistedTaskId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (existing != null)
                    {
                        var maxExistingAttempt = existing
                            .Where(c => string.Equals(c.StageId, context.StageId, StringComparison.OrdinalIgnoreCase))
                            .Select(c => c.AttemptIndex)
                            .DefaultIfEmpty(0)
                            .Max();
                        attemptIndex = Math.Max(attemptIndex, maxExistingAttempt + 1);
                    }
                }
            }

            if (conversation == null)
            {
                conversation = await _taskRepository.StartConversationAsync(
                    persistedTaskId,
                    context.StageId,
                    WorkflowStageKind.Agentic,
                    attemptIndex,
                    context.Profile.Id,
                    harnessId,
                    context.Profile.ModelName,
                    workspaceId: context.Workspace?.Id,
                    title: context.ForgeTask.Title,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (conversation == null)
            {
                return;
            }

            if (conversation.AttemptIndex > 0)
            {
                attemptIndex = conversation.AttemptIndex;
            }

            var hadExistingTurns = conversation.Turns is { Count: > 0 };
            _sessions[sessionId] = new RecorderSessionState(conversation, attemptIndex, hadExistingTurns, _taskRepository, _logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start/resume SQLite Conversation for {HarnessId} session '{SessionId}'", harnessId, sessionId);
        }
        finally
        {
            _beginLock.Release();
        }
    }

    public async Task RecordUserTurnAsync(
        string sessionId,
        string harnessId,
        AgentSessionContext context,
        string promptText,
        CancellationToken cancellationToken)
    {
        await BeginConversationAsync(sessionId, harnessId, context, cancellationToken).ConfigureAwait(false);

        if (!_sessions.TryGetValue(sessionId, out var state) || string.IsNullOrEmpty(promptText))
        {
            return;
        }

        if (!state.TryMarkUserTurnRecorded())
        {
            return;
        }

        state.AddTranscriptEntry(new TranscriptEntry(DateTime.UtcNow, TranscriptRoleUser, promptText));

        if (state.HadExistingTurns)
        {
            if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
            {
                state.EnqueueTurn(
                    StageTurnRole.User,
                    $"Failed to record Human Answer User turn for conversation #{state.Conversation.Id}",
                    content: $"[Human Answer]: {context.PendingHumanAnswer}");
            }
            return;
        }

        state.EnqueueTurn(
            StageTurnRole.User,
            $"Failed to record User turn for conversation #{state.Conversation.Id}",
            content: promptText);
    }

    public Task RecordAssistantTurnAsync(
        string sessionId,
        string? content,
        string? thinkingTrace,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.CompletedTask;
        }

        var hasContent = !string.IsNullOrWhiteSpace(content);
        var hasThinking = !string.IsNullOrWhiteSpace(thinkingTrace);
        if (!hasContent && !hasThinking)
        {
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        if (hasThinking)
        {
            state.AddTranscriptEntry(new TranscriptEntry(now, TranscriptRoleThinking, thinkingTrace!));
        }
        if (hasContent)
        {
            state.AddTranscriptEntry(new TranscriptEntry(now, TranscriptRoleAssistant, content!));
        }

        state.EnqueueTurn(
            StageTurnRole.Assistant,
            $"Failed to record Assistant turn for conversation #{state.Conversation.Id}",
            content: hasContent ? content : null,
            thinkingTrace: hasThinking ? thinkingTrace : null);

        return Task.CompletedTask;
    }

    public Task RecordToolCallTurnAsync(
        string sessionId,
        string toolName,
        string? toolArguments,
        string? callId,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.CompletedTask;
        }

        var effectiveTool = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
        var metadataJson = !string.IsNullOrWhiteSpace(callId)
            ? JsonSerializer.Serialize(new { CallId = callId })
            : null;

        state.AddTranscriptEntry(new TranscriptEntry(
            DateTime.UtcNow,
            TranscriptRoleTool,
            $"Invoke `{effectiveTool}`",
            ToolName: effectiveTool,
            ToolArguments: toolArguments,
            CallId: callId));

        state.EnqueueTurn(
            StageTurnRole.Tool,
            $"Failed to record Tool turn '{effectiveTool}' for conversation #{state.Conversation.Id}",
            content: $"Invoke {effectiveTool}",
            toolName: effectiveTool,
            toolArguments: toolArguments,
            metadataJson: metadataJson);

        return Task.CompletedTask;
    }

    public Task RecordToolResultTurnAsync(
        string sessionId,
        string toolName,
        string? toolResult,
        bool isError,
        string? callId,
        long? durationMs,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return Task.CompletedTask;
        }

        var effectiveTool = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
        var metadataJson = !string.IsNullOrWhiteSpace(callId)
            ? JsonSerializer.Serialize(new { CallId = callId })
            : null;

        state.AddTranscriptEntry(new TranscriptEntry(
            DateTime.UtcNow,
            isError ? TranscriptRoleToolError : TranscriptRoleToolResult,
            toolResult ?? string.Empty,
            ToolName: effectiveTool,
            ToolResult: toolResult,
            CallId: callId));

        state.EnqueueTurn(
            StageTurnRole.ToolResult,
            $"Failed to record ToolResult turn '{effectiveTool}' for conversation #{state.Conversation.Id}",
            content: toolResult,
            toolName: effectiveTool,
            toolResult: toolResult,
            durationMs: durationMs,
            isError: isError,
            metadataJson: metadataJson);

        return Task.CompletedTask;
    }

    public Task SaveTranscriptAsync(
        string harnessId,
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken)
        => SaveTranscriptAsync(
            harnessId,
            context,
            workspacePath,
            promptText,
            summaryText,
            elapsedMs,
            promptTokens,
            completionTokens,
            status,
            cancellationToken,
            sessionId: null,
            cachedInputTokens: null);

    public async Task SaveTranscriptAsync(
        string harnessId,
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken,
        string? sessionId,
        int? cachedInputTokens)
    {
        RecorderSessionState? state = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessions.TryRemove(sessionId, out state);
        }

        if (state != null)
        {
            await state.DrainAsync().ConfigureAwait(false);
        }

        string? transcriptFileName = null;
        try
        {
            var entries = BuildTranscriptEntries(state, promptText, summaryText);
            var totalTokens = promptTokens + completionTokens;
            var meta = new TranscriptMetadata(
                context.ForgeTask.Id,
                context.StageId,
                context.Profile.Name,
                context.Profile.ModelName,
                elapsedMs,
                new AgentRunMetrics(
                    DurationMs: elapsedMs,
                    PromptTokens: promptTokens,
                    CompletionTokens: completionTokens,
                    TotalTokens: totalTokens,
                    CachedInputTokens: cachedInputTokens),
                context.OptimizationFlags ?? new LocalOptimizationFlags(),
                status,
                AttemptIndex: state?.AttemptIndex ?? 1);

            var repoRoot = context.Workspace?.RepositoryPath ?? workspacePath;
            var transcriptDir = Path.Combine(repoRoot, DocsFolder, TranscriptsFolder);
            var savedPath = await _transcriptService.SaveTranscriptAsync(meta, entries, transcriptDir, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(savedPath))
            {
                transcriptFileName = Path.GetFileName(savedPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist markdown transcript in {HarnessId} harness", harnessId);
        }

        if (state == null)
        {
            return;
        }

        if (promptTokens > 0 || completionTokens > 0 || (cachedInputTokens ?? 0) > 0)
        {
            try
            {
                var usageDelta = new SessionUsageDelta(
                    InputTokens: (int)Math.Min(int.MaxValue, promptTokens),
                    OutputTokens: (int)Math.Min(int.MaxValue, completionTokens),
                    CachedInputTokens: cachedInputTokens ?? 0,
                    Cost: null,
                    ActualCloudCost: null,
                    ElectricityCost: null,
                    CloudSavings: null,
                    Currency: null);
                await _taskRepository.UpdateConversationUsageAsync(state.Conversation.Id, usageDelta, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist token usage for {HarnessId} conversation #{ConversationId}", harnessId, state.Conversation.Id);
            }
        }

        try
        {
            var stageStatus = status switch
            {
                AgentRunStatus.Completed => StageExecutionStatus.Completed,
                AgentRunStatus.WaitingForHuman => StageExecutionStatus.WaitingForHuman,
                AgentRunStatus.Cancelled => StageExecutionStatus.Cancelled,
                AgentRunStatus.Interrupted => StageExecutionStatus.Interrupted,
                _ => StageExecutionStatus.Failed
            };

            await _taskRepository.CompleteConversationAsync(
                state.Conversation.Id,
                stageStatus,
                summary: summaryText,
                transcriptFileName: transcriptFileName,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to complete {HarnessId} conversation #{ConversationId}", harnessId, state.Conversation.Id);
        }
    }

    private static List<TranscriptEntry> BuildTranscriptEntries(
        RecorderSessionState? state,
        string promptText,
        string? summaryText)
    {
        if (state != null)
        {
            var snapshot = state.GetTranscriptEntriesSnapshot();
            if (snapshot.Count > 0)
            {
                if (!string.IsNullOrEmpty(summaryText) &&
                    !snapshot.Any(e => string.Equals(e.Role, TranscriptRoleAssistant, StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(e.Content, summaryText, StringComparison.Ordinal)))
                {
                    snapshot.Add(new TranscriptEntry(DateTime.UtcNow, TranscriptRoleAssistant, summaryText));
                }
                return snapshot;
            }
        }

        var entries = new List<TranscriptEntry>();
        var now = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(promptText))
        {
            entries.Add(new TranscriptEntry(now, TranscriptRoleUser, promptText));
        }
        if (!string.IsNullOrEmpty(summaryText))
        {
            entries.Add(new TranscriptEntry(now, TranscriptRoleAssistant, summaryText));
        }
        return entries;
    }

    private sealed class RecorderSessionState
    {
        private readonly ITaskRepository _repository;
        private readonly ILogger _logger;
        private readonly List<TranscriptEntry> _transcriptEntries = [];
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;
        private bool _userTurnRecorded;

        public RecorderSessionState(
            Conversation conversation,
            int attemptIndex,
            bool hadExistingTurns,
            ITaskRepository repository,
            ILogger logger)
        {
            Conversation = conversation;
            AttemptIndex = attemptIndex;
            HadExistingTurns = hadExistingTurns;
            _repository = repository;
            _logger = logger;
        }

        public Conversation Conversation { get; }
        public int AttemptIndex { get; }
        public bool HadExistingTurns { get; }

        public bool TryMarkUserTurnRecorded()
        {
            lock (_gate)
            {
                if (_userTurnRecorded)
                {
                    return false;
                }
                _userTurnRecorded = true;
                return true;
            }
        }

        public void AddTranscriptEntry(TranscriptEntry entry)
        {
            lock (_gate)
            {
                _transcriptEntries.Add(entry);
            }
        }

        public List<TranscriptEntry> GetTranscriptEntriesSnapshot()
        {
            lock (_gate)
            {
                return [.. _transcriptEntries];
            }
        }

        public void EnqueueTurn(
            StageTurnRole role,
            string failureMessage,
            string? content = null,
            string? thinkingTrace = null,
            string? toolName = null,
            string? toolArguments = null,
            string? toolResult = null,
            long? durationMs = null,
            bool isError = false,
            string? metadataJson = null)
        {
            var conversationId = Conversation.Id;
            lock (_gate)
            {
                _tail = RunAfterAsync(
                    _tail,
                    () => _repository.AddConversationTurnAsync(
                        conversationId,
                        role,
                        content: content,
                        thinkingTrace: thinkingTrace,
                        toolName: toolName,
                        toolArguments: toolArguments,
                        toolResult: toolResult,
                        durationMs: durationMs,
                        isError: isError,
                        metadataJson: metadataJson,
                        cancellationToken: CancellationToken.None),
                    failureMessage);
            }
        }

        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _tail;
            }
        }

        private async Task RunAfterAsync(Task previous, Func<Task> write, string failureMessage)
        {
            await previous.ConfigureAwait(false);
            try
            {
                await write().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Message}", failureMessage);
            }
        }
    }
}
