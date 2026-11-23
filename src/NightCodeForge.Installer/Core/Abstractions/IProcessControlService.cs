namespace NightCodeForge.Installer.Core.Abstractions;

/// <summary>
/// Abstraction for querying and stopping active host processes safely while preserving the current installer PID.
/// </summary>
public interface IProcessControlService
{
    int CurrentProcessId { get; }
    string? CurrentProcessPath { get; }
    bool IsAnyProcessRunning(IEnumerable<string> processNames);
    Task<bool> StopProcessesAsync(
        IEnumerable<string> processNames,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken = default);
}
