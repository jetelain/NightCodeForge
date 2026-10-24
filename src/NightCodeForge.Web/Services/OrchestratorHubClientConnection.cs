using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Client-side connection manager for the <see cref="NightCodeForge.Infrastructure.Hubs.OrchestratorHub"/>.
/// Encapsulates connection lifecycle, exponential auto-reconnect, and typed C# event delegates for Blazor components.
/// </summary>
public class OrchestratorHubClientConnection : IAsyncDisposable
{
    private readonly NavigationManager _navigationManager;
    private readonly Microsoft.Extensions.Logging.ILogger<OrchestratorHubClientConnection> _logger;
    private readonly WorkerStateService _workerStateService;
    private readonly HashSet<int> _activeTaskGroups = new();
    private readonly object _taskGroupsLock = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private HubConnection? _hubConnection;

    public HubConnection? HubConnection => _hubConnection;
    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;
    public IReadOnlyCollection<int> ActiveTaskGroups
    {
        get
        {
            lock (_taskGroupsLock)
            {
                return _activeTaskGroups.ToList();
            }
        }
    }

    public event Action<int, string, bool>? OnTokenDeltaReceived;
    public event Action<int, string, string, string, int?>? OnToolExecutionReceived;
    public event Action<int, string, string>? OnGitProgressReceived;
    public event Action<string, int?, string?, string>? OnWorkerStatusReceived;
    public event Action<int, string, string, string>? OnStageTransitionReceived;
    public event Action<int, string, bool, string>? OnDeterministicGateResultReceived;
    public event Action<int, string>? OnHostDirtyAlertReceived;
    public event Action<int, string, string, string, DateTime>? OnExecutionLogReceived;
    public event Action<int, string, string, string, int, DateTime>? OnOptimizationActionReceived;
    public event Action<NightCodeForge.Core.Workflow.LlmCallRecord>? OnLlmCallStarted;
    public event Action<string, int, double, double, int>? OnLlmCallProgress;
    public event Action<NightCodeForge.Core.Workflow.LlmCallRecord>? OnLlmCallCompleted;
    public event Action<NightCodeForge.Core.Workflow.ShellCommandRecord>? OnShellCommandStarted;
    public event Action<string, string, bool>? OnShellCommandOutput;
    public event Action<NightCodeForge.Core.Workflow.ShellCommandRecord>? OnShellCommandCompleted;
    public event Action<bool, string?, string?>? OnOperationsStatusChanged;
    public event Action<NightCodeForge.Core.Workflow.ActiveTaskSummary?>? OnActiveTaskReceived;
    public event Action<HubConnectionState>? OnConnectionStateChanged;

    public OrchestratorHubClientConnection(
        NavigationManager navigationManager,
        Microsoft.Extensions.Logging.ILogger<OrchestratorHubClientConnection> logger,
        WorkerStateService workerStateService)
    {
        _navigationManager = navigationManager ?? throw new ArgumentNullException(nameof(navigationManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerStateService = workerStateService ?? throw new ArgumentNullException(nameof(workerStateService));
    }

    public virtual async Task StartAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_hubConnection != null)
            {
                if (_hubConnection.State == HubConnectionState.Connected)
                {
                    await RejoinActiveTaskGroupsAsync();
                    return;
                }

                if (_hubConnection.State == HubConnectionState.Connecting || _hubConnection.State == HubConnectionState.Reconnecting)
                {
                    var waited = 0;
                    while ((_hubConnection.State == HubConnectionState.Connecting || _hubConnection.State == HubConnectionState.Reconnecting) && waited < 3000)
                    {
                        await Task.Delay(100);
                        waited += 100;
                    }

                    if (_hubConnection.State == HubConnectionState.Connected)
                    {
                        await RejoinActiveTaskGroupsAsync();
                        return;
                    }

                    if (_hubConnection.State == HubConnectionState.Connecting || _hubConnection.State == HubConnectionState.Reconnecting)
                    {
                        return;
                    }
                }
            }

            if (_hubConnection == null)
            {
                var hubUri = _navigationManager.ToAbsoluteUri("/hubs/orchestrator");

                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(hubUri)
                    .WithAutomaticReconnect(new[]
                    {
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(10)
                    })
                    .Build();

                RegisterHandlers(_hubConnection);

                _hubConnection.Reconnecting += _ =>
                {
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Reconnecting);
                    return Task.CompletedTask;
                };

                _hubConnection.Reconnected += async _ =>
                {
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
                    await RejoinActiveTaskGroupsAsync();
                };

                _hubConnection.Closed += _ =>
                {
                    OnConnectionStateChanged?.Invoke(HubConnectionState.Disconnected);
                    return Task.CompletedTask;
                };
            }

            await _hubConnection.StartAsync();
            OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
            await RejoinActiveTaskGroupsAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task JoinTaskGroupAsync(int taskId)
    {
        if (taskId <= 0) return;

        lock (_taskGroupsLock)
        {
            _activeTaskGroups.Add(taskId);
        }

        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            try
            {
                await StartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to start hub connection when joining task group {TaskId}.", taskId);
            }
        }

        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("JoinTaskGroup", taskId);
            }
            catch (Exception ex)
            {
                // Silently avoid breaking caller if hub connection is in flight; reconnect handler will catch it
                _logger.LogDebug(ex, "Failed to join task group {TaskId}; will retry on reconnect.", taskId);
            }
        }
    }

    public async Task LeaveTaskGroupAsync(int taskId)
    {
        if (taskId <= 0) return;

        lock (_taskGroupsLock)
        {
            _activeTaskGroups.Remove(taskId);
        }

        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("LeaveTaskGroup", taskId);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup
                _logger.LogDebug(ex, "Failed to leave task group {TaskId}", taskId);
            }
        }
    }

    private async Task RejoinActiveTaskGroupsAsync()
    {
        if (_hubConnection?.State != HubConnectionState.Connected) return;

        int[] groups;
        lock (_taskGroupsLock)
        {
            groups = _activeTaskGroups.ToArray();
        }

        foreach (var taskId in groups)
        {
            try
            {
                await _hubConnection.InvokeAsync("JoinTaskGroup", taskId);
            }
            catch (Exception ex)
            {
                // Non-fatal if one group join fails during batch reconnection
                _logger.LogDebug(ex, "Failed to rejoin task group {TaskId} on reconnect.", taskId);
            }
        }
    }

    public void RegisterHandlers(HubConnection hub)
    {
        hub.On<int, string, bool>("ReceiveTokenDelta", TriggerTokenDelta);
        hub.On<int, string, string, string, int?>("ReceiveToolExecution", TriggerToolExecution);
        hub.On<int, string, string>("ReceiveGitProgress", TriggerGitProgress);
        hub.On<string, int?, string?, string>("ReceiveWorkerStatus", TriggerWorkerStatus);
        hub.On<int, string, string, string>("ReceiveStageTransition", TriggerStageTransition);
        hub.On<int, string, bool, string>("ReceiveDeterministicGateResult", TriggerDeterministicGateResult);
        hub.On<int, string>("ReceiveHostDirtyAlert", TriggerHostDirtyAlert);
        hub.On<int, string, string, string, DateTime>("ReceiveExecutionLog", TriggerExecutionLog);
        hub.On<int, string, string, string, int, DateTime>("ReceiveOptimizationAction", TriggerOptimizationAction);
        hub.On<NightCodeForge.Core.Workflow.LlmCallRecord>("ReceiveLlmCallStarted", TriggerLlmCallStarted);
        hub.On<string, int, double, double, int>("ReceiveLlmCallProgress", TriggerLlmCallProgress);
        hub.On<NightCodeForge.Core.Workflow.LlmCallRecord>("ReceiveLlmCallCompleted", TriggerLlmCallCompleted);
        hub.On<NightCodeForge.Core.Workflow.ShellCommandRecord>("ReceiveShellCommandStarted", TriggerShellCommandStarted);
        hub.On<string, string, bool>("ReceiveShellCommandOutput", TriggerShellCommandOutput);
        hub.On<NightCodeForge.Core.Workflow.ShellCommandRecord>("ReceiveShellCommandCompleted", TriggerShellCommandCompleted);
        hub.On<bool, string?, string?>("ReceiveOperationsStatusChanged", TriggerOperationsStatusChanged);
        hub.On<NightCodeForge.Core.Workflow.ActiveTaskSummary?>("ReceiveActiveTask", TriggerActiveTask);
    }

    public void TriggerActiveTask(NightCodeForge.Core.Workflow.ActiveTaskSummary? activeTask)
    {
        if (activeTask != null)
        {
            _workerStateService.HandleWorkerStatus(WorkerStatusConstants.Running, activeTask.TaskId, activeTask.CurrentStageId, activeTask.HardwareTarget, activeTask.TaskTitle);
        }
        else
        {
            if (_workerStateService.Current.Status == WorkerExecutionStatus.Running)
            {
                _workerStateService.HandleWorkerStatus(WorkerStatusConstants.Idle, null, null, "Local GPU");
            }
        }
        OnActiveTaskReceived?.Invoke(activeTask);
    }

    public void TriggerTokenDelta(int taskId, string token, bool isThinking) =>
        OnTokenDeltaReceived?.Invoke(taskId, token, isThinking);

    public void TriggerToolExecution(int taskId, string toolName, string arguments, string status, int? durationMs) =>
        OnToolExecutionReceived?.Invoke(taskId, toolName, arguments, status, durationMs);

    public void TriggerGitProgress(int taskId, string operation, string message) =>
        OnGitProgressReceived?.Invoke(taskId, operation, message);

    public void TriggerWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget)
    {
        _workerStateService.HandleWorkerStatus(status, activeTaskId, stageId, hardwareTarget);
        OnWorkerStatusReceived?.Invoke(status, activeTaskId, stageId, hardwareTarget);
    }

    public void TriggerStageTransition(int taskId, string stageId, string stageKind, string status) =>
        OnStageTransitionReceived?.Invoke(taskId, stageId, stageKind, status);

    public void TriggerDeterministicGateResult(int taskId, string gateName, bool passed, string metricsSummary) =>
        OnDeterministicGateResultReceived?.Invoke(taskId, gateName, passed, metricsSummary);

    public void TriggerHostDirtyAlert(int taskId, string branchName) =>
        OnHostDirtyAlertReceived?.Invoke(taskId, branchName);

    public void TriggerExecutionLog(int taskId, string stageId, string level, string message, DateTime timestamp) =>
        OnExecutionLogReceived?.Invoke(taskId, stageId, level, message, timestamp);

    public void TriggerOptimizationAction(int taskId, string stageId, string actionType, string description, int tokensOrBytesSaved, DateTime timestamp) =>
        OnOptimizationActionReceived?.Invoke(taskId, stageId, actionType, description, tokensOrBytesSaved, timestamp);

    public void TriggerLlmCallStarted(NightCodeForge.Core.Workflow.LlmCallRecord call) =>
        OnLlmCallStarted?.Invoke(call);

    public void TriggerLlmCallProgress(string callId, int tokens, double durationMs, double tps, int reasoningTokens = 0) =>
        OnLlmCallProgress?.Invoke(callId, tokens, durationMs, tps, reasoningTokens);

    public void TriggerLlmCallCompleted(NightCodeForge.Core.Workflow.LlmCallRecord call) =>
        OnLlmCallCompleted?.Invoke(call);

    public void TriggerShellCommandStarted(NightCodeForge.Core.Workflow.ShellCommandRecord command) =>
        OnShellCommandStarted?.Invoke(command);

    public void TriggerShellCommandOutput(string commandId, string chunk, bool isError) =>
        OnShellCommandOutput?.Invoke(commandId, chunk, isError);

    public void TriggerShellCommandCompleted(NightCodeForge.Core.Workflow.ShellCommandRecord command) =>
        OnShellCommandCompleted?.Invoke(command);

    public void TriggerConnectionStateChanged(HubConnectionState state) =>
        OnConnectionStateChanged?.Invoke(state);

    public void TriggerOperationsStatusChanged(bool isPaused, string? reclaimStatus, string? lastReclaimError) =>
        OnOperationsStatusChanged?.Invoke(isPaused, reclaimStatus, lastReclaimError);

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
        _connectionGate.Dispose();
    }
}
