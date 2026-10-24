using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Telemetry;

/// <summary>
/// Infrastructure implementation of IWorkerStateService querying real-time active task telemetry.
/// </summary>
public class TelemetryWorkerStateService : IWorkerStateService
{
    private readonly IOperationsTelemetryService _telemetryService;

    public TelemetryWorkerStateService(IOperationsTelemetryService telemetryService)
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
    }

    public int? ActiveTaskId => _telemetryService.GetActiveTask()?.TaskId;

    public bool IsRunning => _telemetryService.GetActiveTask() != null;
}
