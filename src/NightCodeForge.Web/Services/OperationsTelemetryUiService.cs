using System.Globalization;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation service managing state, real-time SignalR event synchronization,
/// and presentation formatting for the Live Operations & Hardware Telemetry Console.
/// Keeps Razor components purely focused on layout and user interaction.
/// </summary>
public class OperationsTelemetryUiService : IDisposable
{
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly object _lock = new();
    private readonly List<LlmCallRecord> _activeLlmCalls = new();
    private readonly List<ShellCommandRecord> _activeShellCommands = new();
    private readonly List<ActiveTaskSummary> _activeTasks = new();
    private readonly List<LlmCallRecord> _recentLlmCalls = new();
    private readonly List<ShellCommandRecord> _recentShellCommands = new();

    public LlmCallRecord? ActiveLlmCall { get; private set; }
    public ShellCommandRecord? ActiveShellCommand { get; private set; }
    public ActiveTaskSummary? ActiveTask { get; private set; }
    public ModelPreloadTelemetryRecord? LatestModelPreload { get; private set; }

    public IReadOnlyList<LlmCallRecord> ActiveLlmCalls { get; private set; } = Array.Empty<LlmCallRecord>();
    public IReadOnlyList<ShellCommandRecord> ActiveShellCommands { get; private set; } = Array.Empty<ShellCommandRecord>();
    public IReadOnlyList<ActiveTaskSummary> ActiveTasks { get; private set; } = Array.Empty<ActiveTaskSummary>();

    public IReadOnlyList<LlmCallRecord> RecentLlmCalls { get; private set; } = Array.Empty<LlmCallRecord>();
    public IReadOnlyList<ShellCommandRecord> RecentShellCommands { get; private set; } = Array.Empty<ShellCommandRecord>();

    public LlmCallRecord? SelectedLlmCall { get; set; }
    public ShellCommandRecord? SelectedShellCommand { get; set; }

    public event Action? OnChange;

    public OperationsTelemetryUiService(
        OrchestratorHubClientConnection hubConnection,
        IOperationsTelemetryService telemetryService)
    {
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));

        // Hydrate initial snapshots
        HydrateFromService();

        // Subscribe to live SignalR and telemetry events
        _hubConnection.OnLlmCallStarted += HandleLlmCallStarted;
        _hubConnection.OnLlmCallProgress += HandleLlmCallProgress;
        _hubConnection.OnLlmCallCompleted += HandleLlmCallCompleted;

        _hubConnection.OnShellCommandStarted += HandleShellCommandStarted;
        _hubConnection.OnShellCommandOutput += HandleShellCommandOutput;
        _hubConnection.OnShellCommandCompleted += HandleShellCommandCompleted;

        _hubConnection.OnActiveTaskReceived += HandleActiveTaskChanged;
        _hubConnection.OnWorkerStatusReceived += HandleWorkerStatusReceived;
        _telemetryService.OnLlmCallChanged += HandleLlmCallChanged;
        _telemetryService.OnActiveTaskChanged += HandleActiveTaskChanged;
        _telemetryService.OnActiveTasksChanged += HandleTelemetryActiveTasksChanged;
        _telemetryService.OnModelPreloadChanged += HandleModelPreloadChanged;
    }

    public void HydrateFromService()
    {
        lock (_lock)
        {
            LatestModelPreload = _telemetryService.GetLatestModelPreload();
            _recentLlmCalls.Clear();
            _recentLlmCalls.AddRange(_telemetryService.GetRecentLlmCalls(50) ?? Array.Empty<LlmCallRecord>());
            RecentLlmCalls = _recentLlmCalls.ToArray();

            _activeLlmCalls.Clear();
            _activeLlmCalls.AddRange(_telemetryService.GetActiveLlmCalls() ?? Array.Empty<LlmCallRecord>());
            ActiveLlmCalls = _activeLlmCalls.OrderBy(c => c.StartTime).ToArray();
            ActiveLlmCall = _telemetryService.GetActiveLlmCall() ?? ActiveLlmCalls.LastOrDefault() ?? RecentLlmCalls.FirstOrDefault(c => c.Status == LlmCallStatus.Running);
            if (ActiveLlmCall != null && !_activeLlmCalls.Any(c => c.CallId == ActiveLlmCall.CallId))
            {
                _activeLlmCalls.Add(ActiveLlmCall);
                ActiveLlmCalls = _activeLlmCalls.OrderBy(c => c.StartTime).ToArray();
            }

            _recentShellCommands.Clear();
            _recentShellCommands.AddRange(_telemetryService.GetRecentShellCommands(50) ?? Array.Empty<ShellCommandRecord>());
            RecentShellCommands = _recentShellCommands.ToArray();

            _activeShellCommands.Clear();
            _activeShellCommands.AddRange(_telemetryService.GetActiveShellCommands() ?? Array.Empty<ShellCommandRecord>());
            ActiveShellCommands = _activeShellCommands.OrderBy(c => c.StartTime).ToArray();
            ActiveShellCommand = _telemetryService.GetActiveShellCommand() ?? ActiveShellCommands.LastOrDefault() ?? RecentShellCommands.FirstOrDefault(c => c.Status == ShellCommandStatus.Running);
            if (ActiveShellCommand != null && !_activeShellCommands.Any(c => c.CommandId == ActiveShellCommand.CommandId))
            {
                _activeShellCommands.Add(ActiveShellCommand);
                ActiveShellCommands = _activeShellCommands.OrderBy(c => c.StartTime).ToArray();
            }

            _activeTasks.Clear();
            _activeTasks.AddRange((_telemetryService.GetActiveTasks() ?? Array.Empty<ActiveTaskSummary>()).Where(AutonomousTaskClassifier.IsAutonomousTask));
            ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
            var active = _telemetryService.GetActiveTask();
            ActiveTask = AutonomousTaskClassifier.IsAutonomousTask(active) ? active : ActiveTasks.LastOrDefault();
            if (ActiveTask != null && !_activeTasks.Any(t => t.TaskId == ActiveTask.TaskId))
            {
                _activeTasks.Add(ActiveTask);
                ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
            }
        }
        NotifyStateChanged();
    }

    public void SetActiveTask(ActiveTaskSummary? task)
    {
        lock (_lock)
        {
            if (task != null && !AutonomousTaskClassifier.IsAutonomousTask(task))
            {
                return;
            }

            if (task != null)
            {
                _activeTasks.RemoveAll(t => t.TaskId == task.TaskId);
                _activeTasks.Add(task);
                ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
                ActiveTask = task;
            }
            else
            {
                _activeTasks.Clear();
                ActiveTasks = Array.Empty<ActiveTaskSummary>();
                ActiveTask = null;
            }
        }
        NotifyStateChanged();
    }

    private void HandleLlmCallChanged(LlmCallRecord call)
    {
        if (call.Status == LlmCallStatus.Running)
        {
            HandleLlmCallStarted(call);
        }
        else
        {
            HandleLlmCallCompleted(call);
        }
    }

    private void HandleLlmCallStarted(LlmCallRecord call)
    {
        lock (_lock)
        {
            var activeIdx = _activeLlmCalls.FindIndex(c => c.CallId == call.CallId);
            if (activeIdx >= 0)
            {
                _activeLlmCalls[activeIdx] = call;
            }
            else
            {
                _activeLlmCalls.Add(call);
            }
            ActiveLlmCalls = _activeLlmCalls.OrderBy(c => c.StartTime).ToArray();
            ActiveLlmCall = call;

            if (SelectedLlmCall?.CallId == call.CallId)
            {
                SelectedLlmCall = call;
            }
            var idx = _recentLlmCalls.FindIndex(c => c.CallId == call.CallId);
            if (idx >= 0)
            {
                _recentLlmCalls[idx] = call;
            }
            else
            {
                _recentLlmCalls.Insert(0, call);
                if (_recentLlmCalls.Count > 50) _recentLlmCalls.RemoveAt(_recentLlmCalls.Count - 1);
            }
            RecentLlmCalls = _recentLlmCalls.ToArray();
        }
        NotifyStateChanged();
    }

    private void HandleLlmCallProgress(string callId, int tokens, double durationMs, double tps, int reasoningTokens)
    {
        lock (_lock)
        {
            var activeIdx = _activeLlmCalls.FindIndex(c => c.CallId == callId);
            if (activeIdx >= 0)
            {
                _activeLlmCalls[activeIdx] = _activeLlmCalls[activeIdx] with
                {
                    CompletionTokens = tokens,
                    DurationMs = durationMs,
                    TokensPerSecond = tps,
                    ReasoningTokens = reasoningTokens
                };
                ActiveLlmCalls = _activeLlmCalls.OrderBy(c => c.StartTime).ToArray();
            }

            if (ActiveLlmCall?.CallId == callId)
            {
                ActiveLlmCall = ActiveLlmCall with
                {
                    CompletionTokens = tokens,
                    DurationMs = durationMs,
                    TokensPerSecond = tps,
                    ReasoningTokens = reasoningTokens
                };
            }

            if (SelectedLlmCall?.CallId == callId)
            {
                SelectedLlmCall = SelectedLlmCall with
                {
                    CompletionTokens = tokens,
                    DurationMs = durationMs,
                    TokensPerSecond = tps,
                    ReasoningTokens = reasoningTokens
                };
            }

            var idx = _recentLlmCalls.FindIndex(c => c.CallId == callId);
            if (idx >= 0)
            {
                var existing = _recentLlmCalls[idx];
                _recentLlmCalls[idx] = existing with
                {
                    CompletionTokens = tokens,
                    DurationMs = durationMs,
                    TokensPerSecond = tps,
                    ReasoningTokens = reasoningTokens
                };
                RecentLlmCalls = _recentLlmCalls.ToArray();
            }
        }
        NotifyStateChanged();
    }

    private void HandleLlmCallCompleted(LlmCallRecord call)
    {
        lock (_lock)
        {
            _activeLlmCalls.RemoveAll(c => c.CallId == call.CallId);
            ActiveLlmCalls = _activeLlmCalls.OrderBy(c => c.StartTime).ToArray();

            var idx = _recentLlmCalls.FindIndex(c => c.CallId == call.CallId);
            if (idx >= 0)
            {
                _recentLlmCalls[idx] = call;
            }
            else
            {
                _recentLlmCalls.Insert(0, call);
                if (_recentLlmCalls.Count > 50) _recentLlmCalls.RemoveAt(_recentLlmCalls.Count - 1);
            }
            RecentLlmCalls = _recentLlmCalls.ToArray();

            if (ActiveLlmCall?.CallId == call.CallId)
            {
                ActiveLlmCall = _activeLlmCalls.OrderByDescending(c => c.StartTime).FirstOrDefault()
                    ?? _recentLlmCalls.FirstOrDefault(c => c.CallId != call.CallId && c.Status == LlmCallStatus.Running);
            }

            if (SelectedLlmCall?.CallId == call.CallId)
            {
                SelectedLlmCall = call;
            }
        }
        NotifyStateChanged();
    }

    private void HandleShellCommandStarted(ShellCommandRecord command)
    {
        lock (_lock)
        {
            var activeIdx = _activeShellCommands.FindIndex(c => c.CommandId == command.CommandId);
            if (activeIdx >= 0)
            {
                _activeShellCommands[activeIdx] = command;
            }
            else
            {
                _activeShellCommands.Add(command);
            }
            ActiveShellCommands = _activeShellCommands.OrderBy(c => c.StartTime).ToArray();
            ActiveShellCommand = command;

            var idx = _recentShellCommands.FindIndex(c => c.CommandId == command.CommandId);
            if (idx >= 0)
            {
                _recentShellCommands[idx] = command;
            }
            else
            {
                _recentShellCommands.Insert(0, command);
                if (_recentShellCommands.Count > 50) _recentShellCommands.RemoveAt(_recentShellCommands.Count - 1);
            }
            RecentShellCommands = _recentShellCommands.ToArray();
        }
        NotifyStateChanged();
    }

    private void HandleShellCommandOutput(string commandId, string chunk, bool isError)
    {
        lock (_lock)
        {
            var activeIdx = _activeShellCommands.FindIndex(c => c.CommandId == commandId);
            if (activeIdx >= 0)
            {
                var existing = _activeShellCommands[activeIdx];
                if (isError)
                {
                    var updated = (existing.ErrorPreview ?? "") + chunk;
                    _activeShellCommands[activeIdx] = existing with { ErrorPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
                else
                {
                    var updated = (existing.OutputPreview ?? "") + chunk;
                    _activeShellCommands[activeIdx] = existing with { OutputPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
                ActiveShellCommands = _activeShellCommands.OrderBy(c => c.StartTime).ToArray();
            }

            if (ActiveShellCommand?.CommandId == commandId)
            {
                if (isError)
                {
                    var updated = (ActiveShellCommand.ErrorPreview ?? "") + chunk;
                    ActiveShellCommand = ActiveShellCommand with { ErrorPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
                else
                {
                    var updated = (ActiveShellCommand.OutputPreview ?? "") + chunk;
                    ActiveShellCommand = ActiveShellCommand with { OutputPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
            }

            if (SelectedShellCommand?.CommandId == commandId)
            {
                var existing = SelectedShellCommand;
                if (isError)
                {
                    var updated = (existing.ErrorPreview ?? "") + chunk;
                    SelectedShellCommand = existing with { ErrorPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
                else
                {
                    var updated = (existing.OutputPreview ?? "") + chunk;
                    SelectedShellCommand = existing with { OutputPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
            }

            var idx = _recentShellCommands.FindIndex(c => c.CommandId == commandId);
            if (idx >= 0)
            {
                var existing = _recentShellCommands[idx];
                if (isError)
                {
                    var updated = (existing.ErrorPreview ?? "") + chunk;
                    _recentShellCommands[idx] = existing with { ErrorPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
                else
                {
                    var updated = (existing.OutputPreview ?? "") + chunk;
                    _recentShellCommands[idx] = existing with { OutputPreview = updated.Length > 10_000 ? updated[^10_000..] : updated };
                }
                RecentShellCommands = _recentShellCommands.ToArray();
            }
        }
        NotifyStateChanged();
    }

    private void HandleShellCommandCompleted(ShellCommandRecord command)
    {
        lock (_lock)
        {
            _activeShellCommands.RemoveAll(c => c.CommandId == command.CommandId);
            ActiveShellCommands = _activeShellCommands.OrderBy(c => c.StartTime).ToArray();

            var idx = _recentShellCommands.FindIndex(c => c.CommandId == command.CommandId);
            if (idx >= 0)
            {
                _recentShellCommands[idx] = command;
            }
            else
            {
                _recentShellCommands.Insert(0, command);
                if (_recentShellCommands.Count > 50) _recentShellCommands.RemoveAt(_recentShellCommands.Count - 1);
            }
            RecentShellCommands = _recentShellCommands.ToArray();

            if (ActiveShellCommand?.CommandId == command.CommandId)
            {
                ActiveShellCommand = _activeShellCommands.OrderByDescending(c => c.StartTime).FirstOrDefault()
                    ?? _recentShellCommands.FirstOrDefault(c => c.CommandId != command.CommandId && c.Status == ShellCommandStatus.Running);
            }

            if (SelectedShellCommand?.CommandId == command.CommandId)
            {
                SelectedShellCommand = command;
            }
        }
        NotifyStateChanged();
    }

    public static bool HasRetryAttempt(LlmCallRecord? call) =>
        call != null && call.RetryAttempt > 0;

    public static string FormatRetryBadgeLabel(LlmCallRecord? call)
    {
        if (call == null || call.RetryAttempt <= 0) return string.Empty;
        return call.MaxRetries > 0
            ? string.Create(CultureInfo.InvariantCulture, $"Retry {call.RetryAttempt}/{call.MaxRetries}")
            : string.Create(CultureInfo.InvariantCulture, $"Retry {call.RetryAttempt}");
    }

    public static string FormatRetryNotice(LlmCallRecord? call)
    {
        if (call == null || call.RetryAttempt <= 0) return string.Empty;
        var badge = FormatRetryBadgeLabel(call);
        return !string.IsNullOrWhiteSpace(call.LastRetryReason)
            ? $"{badge} — {call.LastRetryReason}"
            : badge;
    }

    public static string FormatDuration(double durationMs)
    {
        if (durationMs <= 0) return "0ms";
        if (durationMs < 1000) return string.Create(CultureInfo.InvariantCulture, $"{durationMs:F0}ms");
        return string.Create(CultureInfo.InvariantCulture, $"{durationMs / 1000.0:F2}s");
    }

    public static string FormatLiveLlmDuration(LlmCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Status == LlmCallStatus.Running)
        {
            var elapsed = (DateTime.UtcNow - call.StartTime).TotalMilliseconds;
            return FormatDuration(Math.Max(elapsed, call.DurationMs));
        }
        return FormatDuration(call.DurationMs);
    }

    public static string FormatLiveShellDuration(ShellCommandRecord cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (cmd.Status == ShellCommandStatus.Running)
        {
            var elapsed = (DateTime.UtcNow - cmd.StartTime).TotalMilliseconds;
            return FormatDuration(Math.Max(elapsed, cmd.DurationMs));
        }
        return FormatDuration(cmd.DurationMs);
    }

    public static string FormatSpeed(double tps)
    {
        if (tps <= 0) return "—";
        return string.Create(CultureInfo.InvariantCulture, $"{tps:F1} t/s");
    }

    public static string GetRouteBadgeClass(string? route)
    {
        return (route?.Trim().ToLowerInvariant()) switch
        {
            "local" => "badge-teal",
            "remote" => "badge-indigo",
            "cloud" => "badge-amber",
            "openai compatible" or "openaicompatible" or "openai" => "badge-teal",
            _ => "badge-gray"
        };
    }

    public static string GetTargetBackendBadgeClass(TargetBackendState state)
    {
        return state switch
        {
            TargetBackendState.Active => "badge-teal",
            TargetBackendState.Available => "badge-indigo",
            TargetBackendState.Busy => "badge-amber",
            TargetBackendState.Disabled => "badge-gray",
            TargetBackendState.Offline => "badge-crimson",
            _ => "badge-gray"
        };
    }

    public static string GetLlmStatusBadgeClass(LlmCallStatus status)
    {
        return status switch
        {
            LlmCallStatus.Running => "badge-amber",
            LlmCallStatus.Completed => "badge-teal",
            LlmCallStatus.Failed => "badge-crimson",
            LlmCallStatus.Cancelled => "badge-gray",
            _ => "badge-gray"
        };
    }

    public static string GetShellStatusBadgeClass(ShellCommandStatus status)
    {
        return status switch
        {
            ShellCommandStatus.Running => "badge-amber",
            ShellCommandStatus.Completed => "badge-teal",
            ShellCommandStatus.Failed => "badge-crimson",
            _ => "badge-gray"
        };
    }

    public static string GetExitCodeBadgeClass(int? exitCode)
    {
        if (!exitCode.HasValue) return "badge-gray";
        return exitCode.Value == 0 ? "badge-teal" : "badge-crimson";
    }

    public static bool IsPreloadActive(ModelPreloadTelemetryRecord? record) =>
        record?.IsActive == true;

    public static string GetPreloadBadgeClass(ModelPreloadTelemetryRecord? record)
    {
        if (record == null) return "badge-gray";
        if (record.IsActive) return "badge-amber";
        if (record.Success) return "badge-emerald";
        return "badge-crimson";
    }

    public static string GetPreloadStatusText(ModelPreloadTelemetryRecord? record)
    {
        if (record == null) return "Idle";
        if (record.IsActive) return "⚡ JIT Warmup";
        if (record.Success) return "● Resident";
        return "⚠️ Warmup Issue";
    }

    public static string FormatPreloadBadgeLabel(ModelPreloadTelemetryRecord? record)
    {
        if (record == null) return string.Empty;
        var status = GetPreloadStatusText(record);
        var target = !string.IsNullOrWhiteSpace(record.SelectedTarget) ? $" ({record.SelectedTarget})" : string.Empty;
        return $"{status}: {record.Model}{target}";
    }

    public static string FormatPreloadSummary(ModelPreloadTelemetryRecord? record)
    {
        if (record == null) return "No active preload";
        if (record.IsActive)
        {
            var target = !string.IsNullOrWhiteSpace(record.SelectedTarget) ? $" → {record.SelectedTarget}" : string.Empty;
            return $"Warming up '{record.Model}'{target}...";
        }

        if (record.Success)
        {
            var target = !string.IsNullOrWhiteSpace(record.SelectedTarget) ? record.SelectedTarget : "Fleet Target";
            var tokens = record.NumCtx.HasValue ? $" ({record.NumCtx.Value.ToString("N0", CultureInfo.InvariantCulture)} ctx)" : string.Empty;
            var duration = record.DurationMs.HasValue ? $" in {record.DurationMs.Value.ToString("N0", CultureInfo.InvariantCulture)}ms" : string.Empty;
            return $"'{record.Model}' on {target}{tokens}{duration}";
        }

        return !string.IsNullOrWhiteSpace(record.Reason) ? record.Reason : $"Warmup failed for '{record.Model}'";
    }

    public static string FormatActiveLlmBadge(int count) =>
        count > 1 ? $"{count} Inferring" : "Inferring";

    public static string FormatActiveShellBadge(int count) =>
        count > 1 ? $"{count} Executing" : "Executing";

    public static string FormatActiveTaskBadge(int count) =>
        count > 1 ? $"Running ({count})" : "Running";

    private void HandleModelPreloadChanged(ModelPreloadTelemetryRecord? record)
    {
        lock (_lock)
        {
            LatestModelPreload = record;
        }
        NotifyStateChanged();
    }

    private void HandleTelemetryActiveTasksChanged(IReadOnlyList<ActiveTaskSummary> tasks)
    {
        lock (_lock)
        {
            _activeTasks.Clear();
            _activeTasks.AddRange(tasks.Where(AutonomousTaskClassifier.IsAutonomousTask));
            ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
            ActiveTask = _activeTasks.OrderByDescending(t => t.StartedAt).FirstOrDefault();
        }
        NotifyStateChanged();
    }

    private void HandleActiveTaskChanged(ActiveTaskSummary? task)
    {
        lock (_lock)
        {
            if (task != null && !AutonomousTaskClassifier.IsAutonomousTask(task))
            {
                return;
            }

            var teleTasks = _telemetryService.GetActiveTasks();
            var validTeleTasks = teleTasks?.Where(AutonomousTaskClassifier.IsAutonomousTask).ToList();

            if (task == null)
            {
                _activeTasks.Clear();
                if (validTeleTasks is { Count: > 0 })
                {
                    _activeTasks.AddRange(validTeleTasks);
                }
            }
            else if (validTeleTasks is { Count: > 0 })
            {
                _activeTasks.Clear();
                _activeTasks.AddRange(validTeleTasks.Where(t => t.TaskId != task.TaskId));
                _activeTasks.Add(task);
            }
            else
            {
                _activeTasks.RemoveAll(t => t.TaskId == task.TaskId);
                _activeTasks.Add(task);
            }

            ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
            ActiveTask = _activeTasks.OrderByDescending(t => t.StartedAt).FirstOrDefault();
        }
        NotifyStateChanged();
    }

    private void HandleWorkerStatusReceived(string status, int? activeTaskId, string? stageId, string hardwareTarget)
    {
        lock (_lock)
        {
            if (activeTaskId.HasValue && !AutonomousTaskClassifier.IsAutonomousTask(activeTaskId, stageId))
            {
                return;
            }

            if (WorkerStatusConstants.IsIdle(status) ||
                WorkerStatusConstants.IsWaitingForHuman(status) ||
                WorkerStatusConstants.IsBlocked(status))
            {
                if (!activeTaskId.HasValue)
                {
                    _activeTasks.Clear();
                    ActiveTasks = Array.Empty<ActiveTaskSummary>();
                    ActiveTask = null;
                }
                else
                {
                    _activeTasks.RemoveAll(t => t.TaskId == activeTaskId.Value);

                    var teleActive = _telemetryService.GetActiveTask();
                    if (teleActive != null && AutonomousTaskClassifier.IsAutonomousTask(teleActive) && teleActive.TaskId != activeTaskId.Value)
                    {
                        if (!_activeTasks.Any(t => t.TaskId == teleActive.TaskId))
                        {
                            _activeTasks.Add(teleActive);
                        }
                    }

                    var teleTasks = _telemetryService.GetActiveTasks();
                    if (teleTasks != null && teleTasks.Count > 0)
                    {
                        _activeTasks.Clear();
                        foreach (var t in teleTasks.Where(AutonomousTaskClassifier.IsAutonomousTask))
                        {
                            if (t.TaskId != activeTaskId.Value)
                            {
                                _activeTasks.Add(t);
                            }
                        }
                    }

                    ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
                    ActiveTask = _activeTasks.OrderByDescending(t => t.StartedAt).FirstOrDefault();
                }
            }
            else if (WorkerStatusConstants.IsRunning(status) && activeTaskId.HasValue)
            {
                var teleTasks = _telemetryService.GetActiveTasks();
                var validTeleTasks = teleTasks?.Where(AutonomousTaskClassifier.IsAutonomousTask).ToList();
                var teleMatch = validTeleTasks?.FirstOrDefault(t => t.TaskId == activeTaskId.Value)
                    ?? _telemetryService.GetActiveTask(activeTaskId.Value);

                var teleActive = _telemetryService.GetActiveTask();
                if (teleMatch == null && teleActive != null && teleActive.TaskId == activeTaskId.Value)
                {
                    teleMatch = teleActive;
                }

                ActiveTaskSummary taskSummary;
                if (teleMatch != null && AutonomousTaskClassifier.IsAutonomousTask(teleMatch))
                {
                    taskSummary = teleMatch with
                    {
                        CurrentStageId = stageId ?? teleMatch.CurrentStageId,
                        HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? teleMatch.HardwareTarget : hardwareTarget
                    };
                }
                else
                {
                    var existing = _activeTasks.FirstOrDefault(t => t.TaskId == activeTaskId.Value);
                    taskSummary = existing != null
                        ? existing with
                        {
                            CurrentStageId = stageId ?? existing.CurrentStageId,
                            HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? existing.HardwareTarget : hardwareTarget
                        }
                        : new ActiveTaskSummary(
                            activeTaskId.Value,
                            $"Task #{activeTaskId.Value}",
                            stageId ?? "Executing",
                            1,
                            "default",
                            DateTime.UtcNow,
                            hardwareTarget);
                }

                if (validTeleTasks is { Count: > 0 })
                {
                    _activeTasks.Clear();
                    _activeTasks.AddRange(validTeleTasks.Where(t => t.TaskId != activeTaskId.Value));
                    _activeTasks.Add(taskSummary);
                }
                else
                {
                    _activeTasks.RemoveAll(t => t.TaskId == activeTaskId.Value);
                    _activeTasks.Add(taskSummary);
                }

                ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
                ActiveTask = _activeTasks.OrderByDescending(t => t.StartedAt).FirstOrDefault();
            }
            else if (ActiveTask != null && stageId != null)
            {
                for (int i = 0; i < _activeTasks.Count; i++)
                {
                    if (_activeTasks[i].TaskId == ActiveTask.TaskId)
                    {
                        _activeTasks[i] = _activeTasks[i] with
                        {
                            CurrentStageId = stageId,
                            HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? _activeTasks[i].HardwareTarget : hardwareTarget
                        };
                    }
                }
                ActiveTasks = _activeTasks.OrderBy(t => t.StartedAt).ToArray();
                ActiveTask = ActiveTask with
                {
                    CurrentStageId = stageId,
                    HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? ActiveTask.HardwareTarget : hardwareTarget
                };
            }
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        _hubConnection.OnLlmCallStarted -= HandleLlmCallStarted;
        _hubConnection.OnLlmCallProgress -= HandleLlmCallProgress;
        _hubConnection.OnLlmCallCompleted -= HandleLlmCallCompleted;

        _hubConnection.OnShellCommandStarted -= HandleShellCommandStarted;
        _hubConnection.OnShellCommandOutput -= HandleShellCommandOutput;
        _hubConnection.OnShellCommandCompleted -= HandleShellCommandCompleted;

        _hubConnection.OnActiveTaskReceived -= HandleActiveTaskChanged;
        _hubConnection.OnWorkerStatusReceived -= HandleWorkerStatusReceived;
        _telemetryService.OnLlmCallChanged -= HandleLlmCallChanged;
        _telemetryService.OnActiveTaskChanged -= HandleActiveTaskChanged;
        _telemetryService.OnActiveTasksChanged -= HandleTelemetryActiveTasksChanged;
        _telemetryService.OnModelPreloadChanged -= HandleModelPreloadChanged;
    }
}
