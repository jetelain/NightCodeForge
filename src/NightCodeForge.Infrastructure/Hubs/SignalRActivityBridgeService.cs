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
    public const string DeterministicCpuHardwareTarget = "Deterministic CPU";
    public const string LocalGpuHardwareTarget = "Local GPU";
    public const string AntigravityAcpHardwareTarget = "Antigravity ACP";

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
                    var toolName = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.ToolName, out var tn) == true && tn != null
                        ? tn.ToString()!
                        : (evt.Content.Contains(':') ? evt.Content.Split(':')[0].Trim() : "tool");
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveToolExecution(evt.TaskId, toolName, evt.Content, "Running", null);
                    break;

                case AgentActivityKind.ToolCompleted:
                    var completedTool = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.ToolName, out var ctn) == true && ctn != null
                        ? ctn.ToString()!
                        : (evt.Content.Contains(':') ? evt.Content.Split(':')[0].Trim() : "tool");
                    int? durationMs = null;
                    if (evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.DurationMs, out var dur) == true && dur != null)
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
                    var kind = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.StageKind, out var sk) == true && sk != null
                        ? sk.ToString()!
                        : "Agentic";
                    var isAutonomousTask = AutonomousTaskClassifier.IsAutonomousTask(evt);

                    if (isAutonomousTask)
                    {
                        await _hubContext.Clients.All
                            .ReceiveStageTransition(evt.TaskId, evt.StageId, kind, status);
                    }

                    if (evt.Kind == AgentActivityKind.StageStarted)
                    {
                        var isRestart = evt.Metadata?.TryGetValue("IsRestart", out var irObj) == true && irObj is bool ir && ir;
                        if (isRestart || !isAutonomousTask)
                        {
                            break;
                        }

                        var existing = _telemetryService.GetActiveTask(evt.TaskId);
                        var title = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.TaskTitle, out var tObj) == true && tObj != null
                            ? tObj.ToString()!
                            : (existing != null ? existing.TaskTitle : $"Task #{evt.TaskId}");
                        var defaultPriority = existing != null ? existing.Priority : TaskPriority.Normal;
                        object? pObj = null;
                        evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.Priority, out pObj);
                        int priority = TaskPriority.ParseOrDefault(pObj, defaultPriority);
                        var queue = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.QueueType, out var qObj) == true && qObj != null
                            ? qObj.ToString()!
                            : (evt.Metadata?.TryGetValue("QueueName", out var oldQObj) == true && oldQObj != null
                                ? oldQObj.ToString()!
                                : (existing != null ? existing.QueueType : (kind.Equals("Deterministic", StringComparison.OrdinalIgnoreCase) ? "cpu-deterministic" : "llm-inference")));
                        var startedAt = existing != null ? existing.StartedAt : evt.Timestamp;
                        var hardwareTarget = ResolveHardwareTarget(kind, evt.Metadata, existing);

                        _telemetryService.RecordActiveTask(new ActiveTaskSummary(
                            evt.TaskId,
                            title,
                            evt.StageId,
                            priority,
                            queue,
                            startedAt,
                            hardwareTarget));

                        await _hubContext.Clients.All
                            .ReceiveWorkerStatus(WorkerStatusConstants.Running, evt.TaskId, evt.StageId, hardwareTarget);
                    }
                    else if (isAutonomousTask &&
                             (string.Equals(evt.StageId, StandardStageIds.Triage, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(evt.StageId, StandardStageIds.Merge, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(evt.StageId, StandardStageIds.PostReviewVerification, StringComparison.OrdinalIgnoreCase)))
                    {
                        await ClearActiveTaskAndBroadcastWorkerStatusAsync(evt.TaskId);
                    }
                    break;

                case AgentActivityKind.Interrupted:
                    var isPauseInterruption = evt.Content?.StartsWith(AgentActivityConstants.OperationsPausedPrefix, StringComparison.OrdinalIgnoreCase) == true;

                    if (AutonomousTaskClassifier.IsAutonomousTask(evt))
                    {
                        _telemetryService.ClearActiveTask(evt.TaskId);
                        var remainingAfterInterrupted = GetRemainingActiveTask();
                        if (remainingAfterInterrupted != null)
                        {
                            await _hubContext.Clients.All
                                .ReceiveWorkerStatus(WorkerStatusConstants.Running, remainingAfterInterrupted.TaskId, remainingAfterInterrupted.CurrentStageId, remainingAfterInterrupted.HardwareTarget);
                        }
                        else
                        {
                            var workerStatus = isPauseInterruption ? WorkerStatusConstants.Idle : WorkerStatusConstants.WaitingForHuman;
                            await _hubContext.Clients.All
                                .ReceiveWorkerStatus(workerStatus, evt.TaskId, evt.StageId, LocalGpuHardwareTarget);
                        }

                        if (!string.IsNullOrWhiteSpace(evt.StageId))
                        {
                            var stageKind = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.StageKind, out var interruptedKind) == true && interruptedKind != null
                                ? interruptedKind.ToString()!
                                : WorkflowStageKind.Agentic.ToString();

                            var transitionStatus = isPauseInterruption
                                ? StageExecutionStatus.Interrupted.ToString()
                                : StageExecutionStatus.WaitingForHuman.ToString();

                            await _hubContext.Clients.All
                                .ReceiveStageTransition(evt.TaskId, evt.StageId, stageKind, transitionStatus);
                        }
                    }

                    if (!isPauseInterruption && evt.TaskId > 0)
                    {
                        await _hubContext.Clients.Group(groupName)
                            .ReceiveToolExecution(evt.TaskId, CanonicalToolNames.Ask, evt.Content ?? string.Empty, WorkerStatusConstants.WaitingForHuman, null);
                    }
                    break;

                case AgentActivityKind.Error:
                    if (AutonomousTaskClassifier.IsAutonomousTask(evt))
                    {
                        await ClearActiveTaskAndBroadcastWorkerStatusAsync(evt.TaskId);
                    }
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveExecutionLog(evt.TaskId, evt.StageId, "Error", evt.Content, evt.Timestamp);
                    break;

                case AgentActivityKind.TurnCompleted:
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveExecutionLog(evt.TaskId, evt.StageId, "Info", $"Turn completed: {evt.Content}", evt.Timestamp);
                    break;

                case AgentActivityKind.OptimizationAction:
                    var actionType = evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.ActionType, out var at) == true && at != null
                        ? at.ToString()!
                        : "Compaction";
                    var tokensSaved = (evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.TokensSaved, out var ts) == true && ts != null && int.TryParse(ts.ToString(), out var parsedTs))
                        ? parsedTs
                        : ((evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.TokensOrBytesSaved, out var tobs) == true && tobs != null && int.TryParse(tobs.ToString(), out var parsedTobs))
                            ? parsedTobs
                            : 0);
                    await _hubContext.Clients.Group(groupName)
                        .ReceiveOptimizationAction(evt.TaskId, evt.StageId, actionType, evt.Content, tokensSaved, evt.Timestamp);
                    break;

                case AgentActivityKind.Notice:
                    if (IsMemoryUpdatedNotice(evt))
                    {
                        // Memory notices are informational: Info level (never Warning) to avoid warning-log spam.
                        await _hubContext.Clients.Group(groupName)
                            .ReceiveExecutionLog(evt.TaskId, evt.StageId, ResilienceConstants.LogLevelInfo, evt.Content, evt.Timestamp);
                        await BroadcastMemoryUpdatedAsync(evt);
                        break;
                    }

                    await _hubContext.Clients.Group(groupName)
                        .ReceiveExecutionLog(evt.TaskId, evt.StageId, ResilienceConstants.LogLevelWarning, evt.Content, evt.Timestamp);

                    if (evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.ActionType, out var actionObj) == true
                        && string.Equals(actionObj?.ToString(), AgentActivityConstants.ActionTypeTaskSuspended, StringComparison.OrdinalIgnoreCase))
                    {
                        if (AutonomousTaskClassifier.IsAutonomousTask(evt))
                        {
                            await ClearActiveTaskAndBroadcastWorkerStatusAsync(evt.TaskId);
                        }

                        var stageKind = evt.Metadata.TryGetValue(AgentActivityMetadataKeys.StageKind, out var suspendedKind) == true && suspendedKind != null
                            ? suspendedKind.ToString()!
                            : WorkflowStageKind.Agentic.ToString();

                        await _hubContext.Clients.All
                            .ReceiveStageTransition(evt.TaskId, evt.StageId, stageKind, AgentActivityConstants.StageStatusSuspended);
                        await _hubContext.Clients.All
                            .ReceiveBacklogMutation(evt.TaskId, AgentActivityConstants.ActionTypeTaskSuspended, evt.Content);
                    }
                    else if (evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.ToolName, out var noticeTool) == true
                        && noticeTool?.ToString() is { } noticeToolName
                        && (CanonicalToolNames.BacklogMutationTools.Contains(noticeToolName, StringComparer.OrdinalIgnoreCase) ||
                            string.Equals(noticeToolName, CanonicalToolNames.ProposeTasks, StringComparison.OrdinalIgnoreCase)))
                    {
                        await _hubContext.Clients.All.ReceiveBacklogMutation(evt.TaskId, noticeToolName, evt.Content);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispatch SignalR activity event {Kind} for task #{TaskId}; client may have disconnected.", evt.Kind, evt.TaskId);
        }
    }

    private static bool IsMemoryUpdatedNotice(AgentActivityEvent evt) =>
        evt.Metadata?.TryGetValue(AgentActivityMetadataKeys.ActionType, out var action) == true &&
        string.Equals(action?.ToString(), AgentActivityMetadataKeys.ActionTypeMemoryUpdated, StringComparison.Ordinal);

    private async Task BroadcastMemoryUpdatedAsync(AgentActivityEvent evt)
    {
        var metadata = evt.Metadata!;
        if (!TryGetMemoryMetadata(metadata, AgentActivityMetadataKeys.MemoryScope, out var scope) ||
            !TryGetMemoryMetadata(metadata, AgentActivityMetadataKeys.MemoryRelativePath, out var relativePath) ||
            !TryGetMemoryMetadata(metadata, AgentActivityMetadataKeys.MemoryChangeKind, out var changeKind) ||
            !metadata.TryGetValue(AgentActivityMetadataKeys.WorkspaceId, out var rawWorkspaceId) ||
            !int.TryParse(rawWorkspaceId?.ToString(), out var workspaceId))
        {
            _logger.LogWarning("Dropped MemoryUpdated activity notice lacking required memory metadata: {Content}", evt.Content);
            return;
        }

        await _hubContext.Clients.All.ReceiveMemoryUpdated(
            workspaceId,
            evt.TaskId > 0 ? evt.TaskId : null,
            evt.StageId,
            scope,
            relativePath,
            changeKind,
            evt.Content);
    }

    private static bool TryGetMemoryMetadata(IReadOnlyDictionary<string, object?> metadata, string key, out string value)
    {
        value = metadata.TryGetValue(key, out var raw) ? raw?.ToString() ?? string.Empty : string.Empty;
        return value.Length > 0;
    }

    private async Task ClearActiveTaskAndBroadcastWorkerStatusAsync(int taskId)
    {
        _telemetryService.ClearActiveTask(taskId);
        var remainingActive = GetRemainingActiveTask();
        if (remainingActive != null)
        {
            await _hubContext.Clients.All
                .ReceiveWorkerStatus(WorkerStatusConstants.Running, remainingActive.TaskId, remainingActive.CurrentStageId, remainingActive.HardwareTarget);
        }
        else
        {
            await _hubContext.Clients.All
                .ReceiveWorkerStatus(WorkerStatusConstants.Idle, null, null, LocalGpuHardwareTarget);
        }
    }

    private ActiveTaskSummary? GetRemainingActiveTask()
    {
        var activeTasks = _telemetryService.GetActiveTasks();
        if (activeTasks is { Count: > 0 })
        {
            return activeTasks[^1];
        }

        return _telemetryService.GetActiveTask();
    }

    private static string ResolveHardwareTarget(
        string stageKind,
        IReadOnlyDictionary<string, object?>? metadata,
        ActiveTaskSummary? existing)
    {
        if (stageKind.Equals("Deterministic", StringComparison.OrdinalIgnoreCase))
        {
            return DeterministicCpuHardwareTarget;
        }

        if (metadata != null &&
            metadata.TryGetValue(AgentActivityMetadataKeys.HarnessId, out var harnessObj) &&
            harnessObj != null &&
            !string.IsNullOrWhiteSpace(harnessObj.ToString()))
        {
            return string.Equals(harnessObj.ToString(), StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase)
                ? AntigravityAcpHardwareTarget
                : LocalGpuHardwareTarget;
        }

        if (existing != null &&
            !string.IsNullOrWhiteSpace(existing.HardwareTarget) &&
            !string.Equals(existing.HardwareTarget, DeterministicCpuHardwareTarget, StringComparison.OrdinalIgnoreCase))
        {
            return existing.HardwareTarget;
        }

        return LocalGpuHardwareTarget;
    }
}
