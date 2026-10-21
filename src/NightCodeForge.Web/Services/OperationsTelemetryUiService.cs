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
    private readonly List<LlmCallRecord> _recentLlmCalls = new();
    private readonly List<ShellCommandRecord> _recentShellCommands = new();

    public LlmCallRecord? ActiveLlmCall { get; private set; }
    public ShellCommandRecord? ActiveShellCommand { get; private set; }

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

        // Subscribe to live SignalR events
        _hubConnection.OnLlmCallStarted += HandleLlmCallStarted;
        _hubConnection.OnLlmCallProgress += HandleLlmCallProgress;
        _hubConnection.OnLlmCallCompleted += HandleLlmCallCompleted;

        _hubConnection.OnShellCommandStarted += HandleShellCommandStarted;
        _hubConnection.OnShellCommandOutput += HandleShellCommandOutput;
        _hubConnection.OnShellCommandCompleted += HandleShellCommandCompleted;
    }

    public void HydrateFromService()
    {
        lock (_lock)
        {
            _recentLlmCalls.Clear();
            _recentLlmCalls.AddRange(_telemetryService.GetRecentLlmCalls(50));
            RecentLlmCalls = _recentLlmCalls.ToArray();
            ActiveLlmCall = _telemetryService.GetActiveLlmCall() ?? RecentLlmCalls.FirstOrDefault(c => c.Status == LlmCallStatus.Running);

            _recentShellCommands.Clear();
            _recentShellCommands.AddRange(_telemetryService.GetRecentShellCommands(50));
            RecentShellCommands = _recentShellCommands.ToArray();
            ActiveShellCommand = _telemetryService.GetActiveShellCommand() ?? RecentShellCommands.FirstOrDefault(c => c.Status == ShellCommandStatus.Running);
        }
        NotifyStateChanged();
    }

    private void HandleLlmCallStarted(LlmCallRecord call)
    {
        lock (_lock)
        {
            ActiveLlmCall = call;
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
                ActiveLlmCall = _recentLlmCalls.FirstOrDefault(c => c.CallId != call.CallId && c.Status == LlmCallStatus.Running);
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
                ActiveShellCommand = _recentShellCommands.FirstOrDefault(c => c.CommandId != command.CommandId && c.Status == ShellCommandStatus.Running);
            }

            if (SelectedShellCommand?.CommandId == command.CommandId)
            {
                SelectedShellCommand = command;
            }
        }
        NotifyStateChanged();
    }

    public static string FormatDuration(double durationMs)
    {
        if (durationMs <= 0) return "0ms";
        if (durationMs < 1000) return $"{durationMs:F0}ms";
        return $"{durationMs / 1000.0:F2}s";
    }

    public static string FormatSpeed(double tps)
    {
        if (tps <= 0) return "—";
        return $"{tps:F1} t/s";
    }

    public static string GetRouteBadgeClass(string? route)
    {
        return (route?.ToLowerInvariant()) switch
        {
            "local" => "badge-teal",
            "remote" => "badge-indigo",
            "cloud" => "badge-amber",
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
    }
}
