using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Hubs;

namespace NightCodeForge.Infrastructure.Telemetry;

/// <summary>
/// Thread-safe singleton circular ring buffer and SignalR broadcaster for system-wide
/// LLM inference calls and shell / CLI commands.
/// </summary>
public class OperationsTelemetryService : InMemoryOperationsTelemetryService
{
    private readonly IHubContext<OrchestratorHub, IOrchestratorHubClient> _hubContext;

    [ActivatorUtilitiesConstructor]
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
        : base(logger, maxLlmCalls, maxShellCommands)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    }

    protected override void NotifyLlmCallStarted(LlmCallRecord record)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallStarted(record);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallStarted for {CallId}", record.CallId);
            }
        });
    }

    protected override void NotifyLlmCallRetry(LlmCallRecord updatedRecord)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallStarted(updatedRecord);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallStarted on retry for {CallId}", updatedRecord.CallId);
            }
        });
    }

    protected override void NotifyLlmCallProgress(string callId, int completionTokens, double durationMs, double tokensPerSecond, int reasoningTokens)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallProgress(callId, completionTokens, durationMs, tokensPerSecond, reasoningTokens);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallProgress for {CallId}", callId);
            }
        });
    }

    protected override void NotifyLlmCallCompleted(LlmCallRecord record)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveLlmCallCompleted(record);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveLlmCallCompleted for {CallId}", record.CallId);
            }
        });
    }

    protected override void NotifyShellCommandStarted(ShellCommandRecord record)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveShellCommandStarted(record);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveShellCommandStarted for {CommandId}", record.CommandId);
            }
        });
    }

    protected override void NotifyShellCommandOutput(string commandId, string outputChunk, bool isError)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveShellCommandOutput(commandId, outputChunk, isError);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveShellCommandOutput for {CommandId}", commandId);
            }
        });
    }

    protected override void NotifyShellCommandCompleted(ShellCommandRecord record)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveShellCommandCompleted(record);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveShellCommandCompleted for {CommandId}", record.CommandId);
            }
        });
    }

    protected override void NotifyPendingCounts(int pendingQuestionCount, int pendingReviewCount)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceivePendingCounts(pendingQuestionCount, pendingReviewCount);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceivePendingCounts");
            }
        });
    }

    protected override void NotifyActiveTaskRecorded(ActiveTaskSummary summary)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveActiveTask(summary);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveActiveTask for task #{TaskId}", summary.TaskId);
            }
        });
    }

    protected override void NotifyActiveTaskCleared(ActiveTaskSummary? remaining)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.ReceiveActiveTask(remaining);
            }
            catch (Exception ex)
            {
                TelemetryLogger.LogDebug(ex, "Failed to broadcast ReceiveActiveTask (cleared)");
            }
        });
    }
}
