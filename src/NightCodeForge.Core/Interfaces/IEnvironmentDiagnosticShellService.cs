using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Execution pipeline used by <see cref="IEnvironmentDiagnosticShellService"/>.
/// </summary>
public enum DiagnosticExecutionPipeline
{
    AgentBashTool = 0,
    RawExecutionEnvironment = 1
}

/// <summary>
/// Identifies the target execution environment, security tier, pipeline, and optional overrides for a diagnostic operation.
/// </summary>
public record EnvironmentDiagnosticTarget(
    int? WorkspaceId,
    int? TaskId = null,
    SandboxSecurityTier SecurityTier = SandboxSecurityTier.Standard,
    DiagnosticExecutionPipeline Pipeline = DiagnosticExecutionPipeline.AgentBashTool,
    string? WorkingDirectoryOverride = null,
    int? TimeoutSecondsOverride = null,
    SandboxScopeKind? ScopeKindOverride = null);

/// <summary>
/// Describes a configured volume bind mount inside an execution environment.
/// </summary>
public record EnvironmentMountInfo(
    string HostPath,
    string ContainerPath,
    bool IsReadOnly);

/// <summary>
/// Snapshot of resolved execution environment metadata, mounts, drift, and liveness state.
/// </summary>
public record EnvironmentInspectionSnapshot(
    SandboxScopeKind ScopeKind,
    int? WorkspaceId,
    string WorkspaceName,
    ExecutionMode ConfiguredMode,
    ExecutionTier ResolvedTier,
    int? TaskId,
    SandboxSecurityTier SecurityTier,
    string DefaultWorkingDirectory,
    string? ContainerName,
    string? ContainerImage,
    bool? IsContainerRunning,
    string? DriftDescription,
    string? UserUidGid,
    bool UseWsl,
    string? WslDistribution,
    string SynthesizedPath,
    IReadOnlyList<EnvironmentMountInfo> ConfiguredMounts);

/// <summary>
/// Result of a diagnostic shell command execution, including cancelled partial output recovery.
/// </summary>
public record DiagnosticCommandResult(
    string CommandLine,
    string EffectiveWorkingDirectory,
    int ExitCode,
    string Output,
    string Error,
    TimeSpan Duration,
    bool IsTruncated,
    DateTimeOffset ExecutedAt);

/// <summary>
/// Status of an individual Environment Doctor probe check.
/// </summary>
public enum DoctorCheckStatus
{
    Passed,
    Warning,
    Failed
}

/// <summary>
/// Single check item produced by <see cref="IEnvironmentDiagnosticShellService.RunEnvironmentDoctorAsync"/>.
/// </summary>
public record EnvironmentDoctorCheckItem(
    string Category,
    string Name,
    DoctorCheckStatus Status,
    string Summary,
    string? DiagnosticOutput);

/// <summary>
/// Diagnostic service providing container inspection, dual-pipeline shell execution, Environment Doctor checks, and container recreation.
/// </summary>
public interface IEnvironmentDiagnosticShellService
{
    Task<EnvironmentInspectionSnapshot> InspectEnvironmentAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default);

    Task<DiagnosticCommandResult> ExecuteCommandAsync(
        EnvironmentDiagnosticTarget target,
        string command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnvironmentDoctorCheckItem>> RunEnvironmentDoctorAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default);

    Task RecreateEnvironmentContainerAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default);
}
