using NightCodeForge.ScenarioRunner.Common;
using NightCodeForge.ScenarioRunner.Environment;

namespace NightCodeForge.ScenarioRunner.Scenarios;

/// <summary>
/// Execution result emitted by a test scenario.
/// </summary>
public record ScenarioResult(
    bool Success,
    TimeSpan Duration,
    string Summary,
    IReadOnlyList<string>? Details = null)
{
    public static ScenarioResult Pass(TimeSpan duration, string summary, params string[] details) =>
        new(true, duration, summary, details);

    public static ScenarioResult Fail(TimeSpan duration, string error, params string[] details) =>
        new(false, duration, error, details);
}

/// <summary>
/// Context passed into scenario execution containing environment, options, and cancellation token.
/// </summary>
public class ScenarioContext
{
    public SyntheticEnvironment Environment { get; }
    public RunnerOptions Options { get; }
    public CancellationToken CancellationToken { get; }

    public ScenarioContext(
        SyntheticEnvironment environment,
        RunnerOptions options,
        CancellationToken cancellationToken = default)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        CancellationToken = cancellationToken;
    }
}

/// <summary>
/// Common contract implemented by all NightCodeForge test scenarios.
/// </summary>
public interface IScenario
{
    string Id { get; }
    string Name { get; }
    string Description { get; }

    Task<ScenarioResult> RunAsync(ScenarioContext context);
}
