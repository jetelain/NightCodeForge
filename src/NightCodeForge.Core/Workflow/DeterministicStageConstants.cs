namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Constants for deterministic workflow execution, sub-stages, and activity streaming.
/// </summary>
public static class DeterministicStageConstants
{
    public const string CpuModelName = "deterministic-cpu";
    public const string CpuDeterministicQueue = "cpu-deterministic";
    public const string LlmInferenceQueue = "llm-inference";

    public const string GitSyncToolSuffix = "-git-sync";
    public const int DefaultPersistTimeoutSeconds = 5;

    public const string FormatSubStage = "format";
    public const string BuildSubStage = "build";
    public const string TestSubStage = "test";
    public const string TestWithCoverageSubStage = "test-with-coverage";
    public const string SastSubStage = "sast";
    public const string RatchetSubStage = "ratchet";

    public const string DefaultBuildCommand = "dotnet build";
    public const string DefaultFormatCommand = "dotnet format --verify-no-changes";
    public const string DefaultTestCommand = "dotnet test --no-build";
    public const string DefaultCoverageCommand = "dotnet test --collect:\"XPlat Code Coverage\"";
    public const string DefaultSastCommand = "devskim analyze --source .";
}
