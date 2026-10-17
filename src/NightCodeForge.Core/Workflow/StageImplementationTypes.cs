namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Canonical implementation type identifiers registered in <see cref="IWorkflowStageRegistry"/>.
/// </summary>
public static class StageImplementationTypes
{
    public const string Agentic = "agentic";
    public const string DeterministicBuild = "deterministic-build";
    public const string DeterministicTest = "deterministic-test";
    public const string DeterministicCoverage = "deterministic-coverage";
    public const string DeterministicSast = "deterministic-sast";
    public const string DeterministicRatchet = "deterministic-ratchet";
    public const string DeterministicComposite = "deterministic-composite";
    public const string DeterministicScript = "deterministic-script";
}
