namespace NightCodeForge.Core.Execution;

using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

/// <summary>
/// Execution environment provider that consistently returns a single pre-configured execution environment.
/// Useful for testing, single-environment deployments, and deterministic quality gates.
/// </summary>
public class SingleExecutionEnvironmentProvider : IExecutionEnvironmentProvider
{
    private readonly IExecutionEnvironment _environment;

    public SingleExecutionEnvironmentProvider(IExecutionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    public IExecutionEnvironment GetEnvironment(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? taskId = null) => _environment;
}
