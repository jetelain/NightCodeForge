using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
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
