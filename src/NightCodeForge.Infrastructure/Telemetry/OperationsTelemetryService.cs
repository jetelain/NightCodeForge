using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Hubs;

namespace NightCodeForge.Infrastructure.Telemetry;

/// <summary>
/// Thread-safe singleton circular ring buffer and SignalR broadcaster for system-wide
/// LLM inference calls and shell / CLI commands.
/// </summary>
public class OperationsTelemetryService : IOperationsTelemetryService
{
    public const int DefaultCapacity = 50;

    private readonly IHubContext<OrchestratorHub, IOrchestratorHubClient> _hubContext;
    private readonly ILogger<OperationsTelemetryService> _logger;
    private readonly int _maxLlmCalls;
    private readonly int _maxShellCommands;

    private readonly object _llmLock = new();
    private readonly List<LlmCallRecord> _llmCalls = new();

    private readonly object _shellLock = new();
    private readonly List<ShellCommandRecord> _shellCommands = new();

    public OperationsTelemetryService(
        IHubContext<OrchestratorHub, IOrchestratorHubClient> hubContext,
        ILogger<OperationsTelemetryService> logger)
        : this(hubContext, logger, DefaultCapacity, DefaultCapacity)
    {
    }

    public OperationsTelemetryService(
        IHubContext<OrchestratorHub, IOrchestratorHubClient> hubContext,
        ILogger<OperationsTelemetryService> logger,
        int maxLlmCalls,
        int maxShellCommands)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxLlmCalls = maxLlmCalls > 0 ? maxLlmCalls : DefaultCapacity;
        _maxShellCommands = maxShellCommands > 0 ? maxShellCommands : DefaultCapacity;
    }

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

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallStarted(record);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallStarted for {CallId}", record.CallId);
            }
        });
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

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallProgress(callId, completionTokens, durationMs, tokensPerSecond, reasoningTokens);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallProgress for {CallId}", callId);
            }
        });
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

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallCompleted(record);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallCompleted for {CallId}", record.CallId);
            }
        });
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

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveShellCommandStarted(record);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveShellCommandStarted for {CommandId}", record.CommandId);
            }
        });
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
                    var updatedErr = AppendPreview(existing.ErrorPreview, outputChunk, 10_000);
                    _shellCommands[idx] = existing with { ErrorPreview = updatedErr };
                }
                else
                {
                    var updatedOut = AppendPreview(existing.OutputPreview, outputChunk, 10_000);
                    _shellCommands[idx] = existing with { OutputPreview = updatedOut };
                }
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveShellCommandOutput(commandId, outputChunk, isError);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveShellCommandOutput for {CommandId}", commandId);
            }
        });
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

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveShellCommandCompleted(record);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveShellCommandCompleted for {CommandId}", record.CommandId);
            }
        });
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

    private readonly object _taskLock = new();
    private readonly Dictionary<int, ActiveTaskSummary> _activeTasks = new();

    public event Action<ActiveTaskSummary?>? OnActiveTaskChanged;

    public void RecordActiveTask(ActiveTaskSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!AutonomousTaskClassifier.IsAutonomousTask(summary))
        {
            return;
        }

        lock (_taskLock)
        {
            _activeTasks[summary.TaskId] = summary;
        }

        OnActiveTaskChanged?.Invoke(summary);

        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveActiveTask(summary);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast ReceiveActiveTask for task #{TaskId}", summary.TaskId);
            }
        });
    }

    public void ClearActiveTask(int? taskId = null)
    {
        if (taskId.HasValue && taskId.Value <= 0)
        {
            return;
        }

        bool cleared = false;
        ActiveTaskSummary? remaining = null;
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

            remaining = _activeTasks.Values
                .Where(AutonomousTaskClassifier.IsAutonomousTask)
                .MaxBy(t => t.StartedAt);
        }

        if (cleared)
        {
            OnActiveTaskChanged?.Invoke(remaining);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _hubContext.Clients.All.ReceiveActiveTask(remaining);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to broadcast ReceiveActiveTask (cleared)");
                }
            });
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
