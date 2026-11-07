namespace NightCodeForge.Core.Services;

/// <summary>
/// Configuration options for global operations control, execution pausing, and GPU VRAM lifecycle.
/// </summary>
public class OperationsControlOptions
{
    public const string SectionName = "OperationsControl";
    public const string ReclaimVramOnPauseEnvironmentVariable = "NCF_OPERATIONS_RECLAIM_VRAM_ON_PAUSE";

    private bool _reclaimVramOnPause;

    /// <summary>
    /// Controls whether GPU VRAM is reclaimed (resident models unloaded) when global operations are paused.
    /// Disabled by default (false) to prevent disturbing active host workloads or local Ollama instances.
    /// Can be overridden via configuration ("OperationsControl:ReclaimVramOnPause") or the
    /// NCF_OPERATIONS_RECLAIM_VRAM_ON_PAUSE environment variable.
    /// Default: false.
    /// </summary>
    public bool ReclaimVramOnPause
    {
        get => GetEnvironmentVariableOverride() ?? _reclaimVramOnPause;
        set => _reclaimVramOnPause = value;
    }

    /// <summary>
    /// Optional explicit alias to disable VRAM reclaim on pause when set.
    /// If set to true, forces VRAM reclaim off; if set to false, enables VRAM reclaim.
    /// </summary>
    public bool? DisableVramReclaimOnPause { get; set; }

    /// <summary>
    /// Determines whether VRAM should be reclaimed during operations pause.
    /// Evaluates the <see cref="ReclaimVramOnPauseEnvironmentVariable"/> environment variable first,
    /// then <see cref="DisableVramReclaimOnPause"/> if explicitly specified,
    /// and otherwise falls back to <see cref="ReclaimVramOnPause"/> (default: false).
    /// </summary>
    public bool ShouldReclaimVramOnPause =>
        GetEnvironmentVariableOverride()
        ?? (DisableVramReclaimOnPause.HasValue
            ? !DisableVramReclaimOnPause.Value
            : _reclaimVramOnPause);

    private static bool? GetEnvironmentVariableOverride()
    {
        var env = Environment.GetEnvironmentVariable(ReclaimVramOnPauseEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        var trimmed = env.Trim();

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }
}

