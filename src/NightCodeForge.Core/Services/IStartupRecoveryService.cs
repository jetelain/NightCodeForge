namespace NightCodeForge.Core.Services;

/// <summary>
/// Service responsible for crash recovery and repository state reconciliation upon host startup before background workers launch.
/// </summary>
public interface IStartupRecoveryService
{
    /// <summary>
    /// Scans workspaces for stale Git lockfiles, rescues uncommitted changes with WIP commits,
    /// reconciles orphaned 'Running' issues, and enqueues them to llm-urgent.
    /// </summary>
    Task ReconcileWorkspacesAsync(CancellationToken cancellationToken = default);
}
