namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Persistence contract for global operations execution and pause state.
/// </summary>
public interface IOperationsStateStore
{
    Task<bool> GetIsPausedAsync(CancellationToken cancellationToken = default);
    Task SetIsPausedAsync(bool isPaused, CancellationToken cancellationToken = default);
}
