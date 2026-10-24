using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Hubs;

/// <summary>
/// Background hosted service that bridges in-memory agent activity events from
/// <see cref="IAgentActivityStreamer"/> to SignalR clients and targeted task groups.
/// </summary>
public class SignalRActivityBridgeService : BackgroundService
{
    private readonly IAgentActivityStreamer _streamer;
    private readonly IHubContext<OrchestratorHub, IOrchestratorHubClient> _hubContext;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly IOperationsControlService _operationsControl;
    private readonly ILogger<SignalRActivityBridgeService> _logger;

    public SignalRActivityBridgeService(
        IAgentActivityStreamer streamer,
        IHubContext<OrchestratorHub, IOrchestratorHubClient> hubContext,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControl,
        ILogger<SignalRActivityBridgeService> logger)
    {
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _operationsControl = operationsControl ?? throw new ArgumentNullException(nameof(operationsControl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _operationsControl.OnOperationsStateChanged += HandleOperationsStateChanged;
    }

    private async void HandleOperationsStateChanged(bool isPaused)
    {
        try
        {
            await _hubContext.Clients.All.ReceiveOperationsStatusChanged(
                isPaused,
                _operationsControl.ReclaimStatus.ToString(),
                _operationsControl.LastReclaimError);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast ReceiveOperationsStatusChanged to SignalR clients.");
        }
    }

    public override void Dispose()
    {
        _operationsControl.OnOperationsStateChanged -= HandleOperationsStateChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalRActivityBridgeService starting event subscription loop.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in _streamer.SubscribeAsync(cancellationToken: stoppingToken))
                {
                    await DispatchActivityEventAsync(evt);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SignalRActivityBridgeService; retrying in 2 seconds.");
                try
                {
                    await Task.Delay(2000, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("SignalRActivityBridgeService event subscription loop terminated.");
    }

    /// <summary>
    /// Dispatches a single activity event to the appropriate SignalR clients or group.
    /// Internal for unit test validation.
    /// </summary>
    public async Task DispatchActivityEventAsync(AgentActivityEvent evt)
    {
        try
        {
            var groupName = OrchestratorHub.GetTaskGroupName(evt.TaskId);

            switch (evt.Kind)
            {
                case AgentActivityKind.ThinkingDelta:
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveTokenDelta(evt.TaskId, evt.Content, isThinking: true);
                    break;

                case AgentActivityKind.MessageDelta:
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveTokenDelta(evt.TaskId, evt.Content, isThinking: false);
                    break;

                case AgentActivityKind.ToolStarted:
                    var toolName = evt.Metadata?.TryGetValue("ToolName", out var tn) == true && tn != null
                        ? tn.ToString()!
                        : (evt.Content.Contains(':') ? evt.Content.Split(':')[0].Trim() : "tool");
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveToolExecution(evt.TaskId, toolName, evt.Content, "Running", null);
                    break;

                case AgentActivityKind.ToolCompleted:
                    var completedTool = evt.Metadata?.TryGetValue("ToolName", out var ctn) == true && ctn != null
                        ? ctn.ToString()!
                        : (evt.Content.Contains(':') ? evt.Content.Split(':')[0].Trim() : "tool");
                    int? durationMs = null;
                    if (evt.Metadata?.TryGetValue("DurationMs", out var dur) == true && dur != null)
                    {
                        if (dur is int iVal)
                        {
                            durationMs = iVal;
                        }
                        else if (int.TryParse(dur.ToString(), out var parsedDur))
                        {
                            durationMs = parsedDur;
                        }
                    }
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveToolExecution(evt.TaskId, completedTool, evt.Content, "Completed", durationMs);
                    break;

                case AgentActivityKind.StageStarted:
                case AgentActivityKind.StageCompleted:
                    var status = evt.Kind == AgentActivityKind.StageStarted ? "Running" : "Completed";
                    var kind = evt.Metadata?.TryGetValue("StageKind", out var sk) == true && sk != null
                        ? sk.ToString()!
                        : "Agentic";
                    await _hubContext.Clients.All
                        .ReceiveStageTransition(evt.TaskId, evt.StageId, kind, status);
                    if (evt.Kind == AgentActivityKind.StageStarted)
                    {
                        await _hubContext.Clients.All
                            .ReceiveWorkerStatus("Running", evt.TaskId, evt.StageId, kind.Equals("Deterministic", StringComparison.OrdinalIgnoreCase) ? "Deterministic CPU" : "Local GPU");

                        var title = evt.Metadata?.TryGetValue("TaskTitle", out var tObj) == true && tObj != null ? tObj.ToString()! : $"Task #{evt.TaskId}";
                        var priority = evt.Metadata?.TryGetValue("Priority", out var pObj) == true && int.TryParse(pObj?.ToString(), out var pVal) ? pVal : 1;
                        var queue = evt.Metadata?.TryGetValue("QueueName", out var qObj) == true && qObj != null ? qObj.ToString()! : (kind.Equals("Deterministic", StringComparison.OrdinalIgnoreCase) ? "cpu-deterministic" : "llm-inference");
                        var existing = _telemetryService.GetActiveTask();
                        var startedAt = existing != null && existing.TaskId == evt.TaskId ? existing.StartedAt : evt.Timestamp;
                        _telemetryService.RecordActiveTask(new ActiveTaskSummary(
                            evt.TaskId,
                            title,
                            evt.StageId,
                            priority,
                            queue,
                            startedAt));
                    }
                    else if (string.Equals(evt.StageId, StandardStageIds.Merge, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(evt.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase))
                    {
                        _telemetryService.ClearActiveTask(evt.TaskId);
                        await _hubContext.Clients.All
                            .ReceiveWorkerStatus("Idle", null, null, "Local GPU");
                    }
                    break;

                case AgentActivityKind.Interrupted:
                    _telemetryService.ClearActiveTask(evt.TaskId);
                    await _hubContext.Clients.All
                        .ReceiveWorkerStatus("WaitingForHuman", evt.TaskId, evt.StageId, "Local GPU");
                    break;

                case AgentActivityKind.Error:
                    _telemetryService.ClearActiveTask(evt.TaskId);
                    await _hubContext.Clients.All
                        .ReceiveWorkerStatus("Idle", null, null, "Local GPU");
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveExecutionLog(evt.TaskId, evt.StageId, "Error", evt.Content, evt.Timestamp);
                    break;

                case AgentActivityKind.TurnCompleted:
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveExecutionLog(evt.TaskId, evt.StageId, "Info", $"Turn completed: {evt.Content}", evt.Timestamp);
                    break;

                case AgentActivityKind.OptimizationAction:
                    var actionType = evt.Metadata?.TryGetValue("ActionType", out var at) == true && at != null
                        ? at.ToString()!
                        : "Compaction";
                    var tokensSaved = evt.Metadata?.TryGetValue("TokensSaved", out var ts) == true && ts != null && int.TryParse(ts.ToString(), out var parsedTs)
                        ? parsedTs
                        : 0;
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveOptimizationAction(evt.TaskId, evt.StageId, actionType, evt.Content, tokensSaved, evt.Timestamp);
                    break;

                case AgentActivityKind.Notice:
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveExecutionLog(evt.TaskId, evt.StageId, ResilienceConstants.LogLevelWarning, evt.Content, evt.Timestamp);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispatch SignalR activity event {Kind} for task #{TaskId}; client may have disconnected.", evt.Kind, evt.TaskId);
        }
    }
}
