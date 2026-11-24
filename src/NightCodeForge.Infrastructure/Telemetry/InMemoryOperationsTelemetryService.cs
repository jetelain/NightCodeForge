using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Telemetry;

/// <summary>
/// Thread-safe singleton circular ring buffer and event dispatcher for system-wide
/// LLM inference calls and shell / CLI commands without requiring SignalR or EF Core.
/// </summary>
public class InMemoryOperationsTelemetryService : IOperationsTelemetryService
{
    public const int DefaultCapacity = 50;
    private const int MaxCommandOutputPreviewLength = 10_000;

    private readonly ILogger _logger;
    private readonly int _maxLlmCalls;
    private readonly int _maxShellCommands;

    private readonly object _llmLock = new();
    private readonly List<LlmCallRecord> _llmCalls = new();

    private readonly object _shellLock = new();
    private readonly List<ShellCommandRecord> _shellCommands = new();

    private readonly object _taskLock = new();
    private readonly Dictionary<int, ActiveTaskSummary> _activeTasks = new();

    private readonly object _preloadLock = new();
    private ModelPreloadTelemetryRecord? _latestModelPreload;

    public event Action<LlmCallRecord>? OnLlmCallChanged;
    public event Action<ShellCommandRecord>? OnShellCommandChanged;
    public event Action<string, string, bool>? OnShellCommandOutput;
    public event Action<ActiveTaskSummary?>? OnActiveTaskChanged;
    public event Action<IReadOnlyList<ActiveTaskSummary>>? OnActiveTasksChanged;
    public event Action<int, int>? OnPendingCountsChanged;
    public event Action<ModelPreloadTelemetryRecord?>? OnModelPreloadChanged;

    [ActivatorUtilitiesConstructor]
    public InMemoryOperationsTelemetryService(ILogger<InMemoryOperationsTelemetryService> logger)
        : this(logger, DefaultCapacity, DefaultCapacity)
    {
    }

    public InMemoryOperationsTelemetryService(
        ILogger logger,
        int maxLlmCalls,
        int maxShellCommands)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxLlmCalls = maxLlmCalls > 0 ? maxLlmCalls : DefaultCapacity;
        _maxShellCommands = maxShellCommands > 0 ? maxShellCommands : DefaultCapacity;
    }

    protected ILogger TelemetryLogger => _logger;

    public void RecordLlmCallStarted(LlmCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _logger.LogInformation(
            "LLM call started: Model: {Model}, Endpoint: {Endpoint}, Thinking Effort: {ThinkingEffort}",
            record.Model,
            record.Endpoint ?? "unknown",
            record.ThinkingEffort ?? "none");

        lock (_llmLock)
        {
            _llmCalls.Insert(0, record);
            while (_llmCalls.Count > _maxLlmCalls)
            {
                _llmCalls.RemoveAt(_llmCalls.Count - 1);
            }
        }

        OnLlmCallChanged?.Invoke(record);
        NotifyLlmCallStarted(record);
    }

    public void RecordLlmCallRetry(RetryNotificationContext context, int? taskId = null, string? stageId = null, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var reason = context.StatusCode.HasValue
            ? $"HTTP {(int)context.StatusCode.Value} {context.StatusCode.Value}"
            : context.ErrorMessage;

        LlmCallRecord? updatedRecord = null;
        lock (_llmLock)
        {
            var idx = -1;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                idx = _llmCalls.FindIndex(r => r.Status == LlmCallStatus.Running && string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            }
            if (idx < 0 && taskId.HasValue && taskId.Value > 0)
            {
                idx = _llmCalls.FindIndex(r => r.Status == LlmCallStatus.Running && r.TaskId == taskId.Value);
            }
            if (idx < 0)
            {
                idx = _llmCalls.FindIndex(r => r.Status == LlmCallStatus.Running);
            }

            if (idx >= 0)
            {
                var existing = _llmCalls[idx];
                updatedRecord = existing with
                {
                    StartTime = DateTime.UtcNow,
                    DurationMs = 0,
                    RetryAttempt = context.Attempt,
                    MaxRetries = context.MaxRetries,
                    LastRetryReason = reason,
                    SessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : existing.SessionId,
                    TaskId = taskId.HasValue && taskId.Value > 0 ? taskId : existing.TaskId,
                    StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : existing.StageId
                };
                _llmCalls[idx] = updatedRecord;
            }
        }

        if (updatedRecord != null)
        {
            _logger.LogWarning(
                "LLM call retrying (attempt {Attempt}/{MaxRetries}): CallId: {CallId}, Model: {Model}, Reason: {Reason}",
                context.Attempt,
                context.MaxRetries,
                updatedRecord.CallId,
                updatedRecord.Model,
                reason);

            OnLlmCallChanged?.Invoke(updatedRecord);
            NotifyLlmCallRetry(updatedRecord);
        }
    }

    public void UpdateLlmCallProgress(string callId, int completionTokens, double durationMs, double tokensPerSecond, int reasoningTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(callId)) return;

        lock (_llmLock)
        {
            var idx = _llmCalls.FindIndex(r => r.CallId == callId);
            if (idx >= 0)
            {
                var existing = _llmCalls[idx];
                _llmCalls[idx] = existing with
                {
                    CompletionTokens = completionTokens,
                    DurationMs = durationMs,
                    TokensPerSecond = tokensPerSecond,
                    ReasoningTokens = reasoningTokens
                };
            }
        }

        NotifyLlmCallProgress(callId, completionTokens, durationMs, tokensPerSecond, reasoningTokens);
    }

    public void RecordLlmCallCompleted(LlmCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Status == LlmCallStatus.Completed)
        {
            _logger.LogInformation(
                "LLM call completed: Model: {Model}, Endpoint: {Endpoint}, Thinking Effort: {ThinkingEffort}, Duration: {DurationMs:F0}ms, Tokens: {CompletionTokens} ({TokensPerSecond:F1} t/s)",
                record.Model,
                record.Endpoint ?? "unknown",
                record.ThinkingEffort ?? "none",
                record.DurationMs,
                record.CompletionTokens,
                record.TokensPerSecond);
        }
        else
        {
            _logger.LogWarning(
                "LLM call {Status}: Model: {Model}, Endpoint: {Endpoint}, Thinking Effort: {ThinkingEffort}, Duration: {DurationMs:F0}ms, Error: {ErrorMessage}",
                record.Status,
                record.Model,
                record.Endpoint ?? "unknown",
                record.ThinkingEffort ?? "none",
                record.DurationMs,
                record.ErrorMessage);
        }

        lock (_llmLock)
        {
            var idx = _llmCalls.FindIndex(r => r.CallId == record.CallId);
            if (idx >= 0)
            {
                var existing = _llmCalls[idx];
                record = record with
                {
                    RetryAttempt = record.RetryAttempt > 0 ? record.RetryAttempt : existing.RetryAttempt,
                    MaxRetries = record.MaxRetries > 0 ? record.MaxRetries : existing.MaxRetries,
                    LastRetryReason = record.LastRetryReason ?? existing.LastRetryReason,
                    SessionId = record.SessionId ?? existing.SessionId,
                    TaskId = record.TaskId ?? existing.TaskId,
                    StageId = record.StageId ?? existing.StageId
                };
                _llmCalls[idx] = record;
            }
            else
            {
                _llmCalls.Insert(0, record);
                while (_llmCalls.Count > _maxLlmCalls)
                {
                    _llmCalls.RemoveAt(_llmCalls.Count - 1);
                }
            }
        }

        OnLlmCallChanged?.Invoke(record);
        NotifyLlmCallCompleted(record);
    }

    public void RecordShellCommandStarted(ShellCommandRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _logger.LogInformation(
            "Shell command started: '{Command}' in '{WorkingDirectory}'",
            record.CommandLine,
            record.WorkingDirectory);

        lock (_shellLock)
        {
            _shellCommands.Insert(0, record);
            while (_shellCommands.Count > _maxShellCommands)
            {
                _shellCommands.RemoveAt(_shellCommands.Count - 1);
            }
        }

        OnShellCommandChanged?.Invoke(record);
        NotifyShellCommandStarted(record);
    }

    public void UpdateShellCommandOutput(string commandId, string outputChunk, bool isError)
    {
        if (string.IsNullOrWhiteSpace(commandId) || string.IsNullOrEmpty(outputChunk)) return;

        lock (_shellLock)
        {
            var idx = _shellCommands.FindIndex(r => r.CommandId == commandId);
            if (idx >= 0)
            {
                var existing = _shellCommands[idx];
                if (isError)
                {
                    var updatedErr = AppendPreview(existing.ErrorPreview, outputChunk, MaxCommandOutputPreviewLength);
                    _shellCommands[idx] = existing with { ErrorPreview = updatedErr };
                }
                else
                {
                    var updatedOut = AppendPreview(existing.OutputPreview, outputChunk, MaxCommandOutputPreviewLength);
                    _shellCommands[idx] = existing with { OutputPreview = updatedOut };
                }
            }
        }

        OnShellCommandOutput?.Invoke(commandId, outputChunk, isError);
        NotifyShellCommandOutput(commandId, outputChunk, isError);
    }

    public void RecordShellCommandCompleted(ShellCommandRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.ExitCode == 0)
        {
            _logger.LogInformation(
                "Shell command completed: '{Command}' with exit code {ExitCode}",
                record.CommandLine,
                record.ExitCode);
        }
        else
        {
            var output = !string.IsNullOrWhiteSpace(record.ErrorPreview)
                ? (!string.IsNullOrWhiteSpace(record.OutputPreview) ? $"{record.OutputPreview}\n{record.ErrorPreview}" : record.ErrorPreview)
                : (record.OutputPreview ?? string.Empty);

            if (string.IsNullOrWhiteSpace(output))
            {
                output = "(no output)";
            }

            _logger.LogWarning(
                "Shell command failed: '{Command}' with exit code {ExitCode}. Output:\n{Output}",
                record.CommandLine,
                record.ExitCode,
                output);
        }

        lock (_shellLock)
        {
            var idx = _shellCommands.FindIndex(r => r.CommandId == record.CommandId);
            if (idx >= 0)
            {
                _shellCommands[idx] = record;
            }
            else
            {
                _shellCommands.Insert(0, record);
                while (_shellCommands.Count > _maxShellCommands)
                {
                    _shellCommands.RemoveAt(_shellCommands.Count - 1);
                }
            }
        }

        OnShellCommandChanged?.Invoke(record);
        NotifyShellCommandCompleted(record);
    }

    public IReadOnlyList<LlmCallRecord> GetRecentLlmCalls(int count = 25)
    {
        lock (_llmLock)
        {
            return _llmCalls.Take(count).ToList();
        }
    }

    public LlmCallRecord? GetActiveLlmCall()
    {
        lock (_llmLock)
        {
            return _llmCalls.FirstOrDefault(r => r.Status == LlmCallStatus.Running);
        }
    }

    public IReadOnlyList<LlmCallRecord> GetActiveLlmCalls()
    {
        lock (_llmLock)
        {
            return _llmCalls.Where(r => r.Status == LlmCallStatus.Running).OrderBy(r => r.StartTime).ToList();
        }
    }

    public IReadOnlyList<ShellCommandRecord> GetRecentShellCommands(int count = 25)
    {
        lock (_shellLock)
        {
            return _shellCommands.Take(count).ToList();
        }
    }

    public ShellCommandRecord? GetActiveShellCommand()
    {
        lock (_shellLock)
        {
            return _shellCommands.FirstOrDefault(r => r.Status == ShellCommandStatus.Running);
        }
    }

    public IReadOnlyList<ShellCommandRecord> GetActiveShellCommands()
    {
        lock (_shellLock)
        {
            return _shellCommands.Where(r => r.Status == ShellCommandStatus.Running).OrderBy(r => r.StartTime).ToList();
        }
    }

    public void RecordModelPreload(ModelPreloadTelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_preloadLock)
        {
            _latestModelPreload = record;
        }

        OnModelPreloadChanged?.Invoke(record);
    }

    public ModelPreloadTelemetryRecord? GetLatestModelPreload()
    {
        lock (_preloadLock)
        {
            return _latestModelPreload;
        }
    }

    public void RecordPendingCounts(int pendingQuestionCount, int pendingReviewCount)
    {
        OnPendingCountsChanged?.Invoke(pendingQuestionCount, pendingReviewCount);
        NotifyPendingCounts(pendingQuestionCount, pendingReviewCount);
    }

    public void RecordActiveTask(ActiveTaskSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!AutonomousTaskClassifier.IsAutonomousTask(summary))
        {
            return;
        }

        IReadOnlyList<ActiveTaskSummary> snapshot;
        lock (_taskLock)
        {
            _activeTasks[summary.TaskId] = summary;
            snapshot = _activeTasks.Values
                .Where(AutonomousTaskClassifier.IsAutonomousTask)
                .OrderBy(t => t.StartedAt)
                .ToList();
        }

        OnActiveTaskChanged?.Invoke(summary);
        OnActiveTasksChanged?.Invoke(snapshot);
        NotifyActiveTaskRecorded(summary);
    }

    public void ClearActiveTask(int? taskId = null)
    {
        if (taskId.HasValue && taskId.Value <= 0)
        {
            return;
        }

        bool cleared;
        ActiveTaskSummary? remaining;
        IReadOnlyList<ActiveTaskSummary> snapshot;
        lock (_taskLock)
        {
            if (taskId.HasValue)
            {
                cleared = _activeTasks.Remove(taskId.Value);
            }
            else
            {
                cleared = _activeTasks.Count > 0;
                _activeTasks.Clear();
            }

            snapshot = _activeTasks.Values
                .Where(AutonomousTaskClassifier.IsAutonomousTask)
                .OrderBy(t => t.StartedAt)
                .ToList();
            remaining = snapshot.Count > 0 ? snapshot[^1] : null;
        }

        if (cleared)
        {
            OnActiveTaskChanged?.Invoke(remaining);
            OnActiveTasksChanged?.Invoke(snapshot);
            NotifyActiveTaskCleared(remaining);
        }
    }

    public ActiveTaskSummary? GetActiveTask(int? taskId = null)
    {
        lock (_taskLock)
        {
            if (taskId.HasValue)
            {
                if (taskId.Value <= 0) return null;
                var task = _activeTasks.GetValueOrDefault(taskId.Value);
                return AutonomousTaskClassifier.IsAutonomousTask(task) ? task : null;
            }

            return _activeTasks.Values
                .Where(AutonomousTaskClassifier.IsAutonomousTask)
                .MaxBy(t => t.StartedAt);
        }
    }

    public IReadOnlyList<ActiveTaskSummary> GetActiveTasks()
    {
        lock (_taskLock)
        {
            return _activeTasks.Values
                .Where(AutonomousTaskClassifier.IsAutonomousTask)
                .OrderBy(t => t.StartedAt)
                .ToList();
        }
    }

    protected virtual void NotifyLlmCallStarted(LlmCallRecord record) { }
    protected virtual void NotifyLlmCallRetry(LlmCallRecord updatedRecord) { }
    protected virtual void NotifyLlmCallProgress(string callId, int completionTokens, double durationMs, double tokensPerSecond, int reasoningTokens) { }
    protected virtual void NotifyLlmCallCompleted(LlmCallRecord record) { }
    protected virtual void NotifyShellCommandStarted(ShellCommandRecord record) { }
    protected virtual void NotifyShellCommandOutput(string commandId, string outputChunk, bool isError) { }
    protected virtual void NotifyShellCommandCompleted(ShellCommandRecord record) { }
    protected virtual void NotifyPendingCounts(int pendingQuestionCount, int pendingReviewCount) { }
    protected virtual void NotifyActiveTaskRecorded(ActiveTaskSummary summary) { }
    protected virtual void NotifyActiveTaskCleared(ActiveTaskSummary? remaining) { }

    private static string AppendPreview(string? existing, string chunk, int maxLength)
    {
        if (string.IsNullOrEmpty(existing))
        {
            return chunk.Length > maxLength ? chunk[^maxLength..] : chunk;
        }

        var combined = existing + chunk;
        return combined.Length > maxLength ? combined[^maxLength..] : combined;
    }
}
