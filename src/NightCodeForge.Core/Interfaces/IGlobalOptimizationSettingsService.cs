using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Service managing global local LLM optimization flags across all agent runs,
/// ideation chats, and pipeline executions.
/// </summary>
public interface IGlobalOptimizationSettingsService
{
    /// <summary>
    /// Gets the current in-memory cached global optimization flags synchronously.
    /// Safe for high-frequency access in agent loops.
    /// </summary>
    LocalOptimizationFlags GetFlags();

    /// <summary>
    /// Loads the global flags from the database (or initializes defaults if none saved).
    /// </summary>
    Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates and persists the global optimization flags to the database and refreshes the cache.
    /// </summary>
    Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event triggered whenever global optimization flags are updated.
    /// </summary>
    event Action<LocalOptimizationFlags>? OnFlagsChanged;
}
