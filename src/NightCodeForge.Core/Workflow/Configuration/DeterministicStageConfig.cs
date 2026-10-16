namespace NightCodeForge.Core.Workflow.Configuration;

/// <summary>
/// Configuration payload for a Deterministic (CPU-native) stage.
/// </summary>
public class DeterministicStageConfig
{
    /// <summary>
    /// Action category: "Build", "Test", "Coverage", "Sast", "WarningRatchet", "Script".
    /// </summary>
    public string Action { get; set; } = "Build";

    /// <summary>
    /// Optional command override (e.g. "dotnet test --no-build", "npm run lint").
    /// If null, resolved automatically from CodeStack presets.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Target stage to loop back to on failure (e.g. "implementation").
    /// </summary>
    public string LoopBackStageId { get; set; } = "implementation";

    /// <summary>
    /// Whether a non-zero exit code or gate failure blocks pipeline progression.
    /// </summary>
    public bool FailOnError { get; set; } = true;

    /// <summary>
    /// Execution timeout in seconds (default 300s).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Extensible action-specific key-value parameters.
    /// </summary>
    public Dictionary<string, string> ExtraParameters { get; set; } = new();
}
