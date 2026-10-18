using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Deterministic quality gate verifying that code coverage does not degrade and test count does not decrease.
/// Automatically executes the test suite if test results have not yet been populated in the pipeline.
/// </summary>
public class DeterministicTestWithCoverageGate : IWorkflowStage
{
    public const string DefaultStageId = StageImplementationTypes.DeterministicTestWithCoverage;
    public const string FallbackLoopBackStageId = StandardStageIds.Implementation;

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly ILogger<DeterministicTestWithCoverageGate> _logger;
    private readonly string? _testCommand;

    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
    public string LoopBackStageId { get; set; } = FallbackLoopBackStageId;

    /// <summary>
    /// When true, only evaluates the anti-cheating and coverage ratchet gates against existing DataBag metrics without executing test runner.
    /// </summary>
    public bool EvaluateOnly { get; init; }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DeterministicTestWithCoverageGate(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicTestWithCoverageGate> logger)
        : this(envProvider, logger, DefaultStageId, "Deterministic Test & Coverage Gate", null)
    {
    }

    public DeterministicTestWithCoverageGate(IExecutionEnvironmentProvider envProvider)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicTestWithCoverageGate>.Instance, DefaultStageId, "Deterministic Test & Coverage Gate", null)
    {
    }

    public DeterministicTestWithCoverageGate(
        IExecutionEnvironmentProvider envProvider,
        string stageId,
        string displayName,
        string? testCommand)
        : this(envProvider, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicTestWithCoverageGate>.Instance, stageId, displayName, testCommand)
    {
    }

    public DeterministicTestWithCoverageGate(
        IExecutionEnvironmentProvider envProvider,
        ILogger<DeterministicTestWithCoverageGate> logger,
        string stageId,
        string displayName,
        string? testCommand)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        StageId = !string.IsNullOrWhiteSpace(stageId) ? stageId : DefaultStageId;
        DisplayName = !string.IsNullOrWhiteSpace(displayName) ? displayName : "Deterministic Test & Coverage Gate";
        _testCommand = testCommand;
    }

    private Task<StageExecutionResult> RunCoverageTestStageAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var testCommand = !string.IsNullOrWhiteSpace(_testCommand)
            ? _testCommand
            : (context.DataBag.Get<string>(PipelineDataBagKeys.CoverageCommand)
               ?? WorkflowDefaults.DefaultCoverageTestCommand);

        var testStage = new DeterministicTestStage(
            _envProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicTestStage>.Instance,
            testCommand,
            stageId: StageId,
            displayName: DisplayName)
        {
            CollectCoverage = true
        };

        return testStage.ExecuteAsync(context, cancellationToken);
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bag = context.DataBag;
        var targetStage = bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId;

        // Reset previous failure reasons for coverage gate
        bag.Remove(PipelineDataBagKeys.CoverageFailureReason);
        bag.Remove(PipelineDataBagKeys.CoverageDelta);

        // Execute tests with coverage collection unless explicitly configured as evaluate-only
        if (!EvaluateOnly)
        {
            var testResult = await RunCoverageTestStageAsync(context, cancellationToken);
            if (!testResult.IsSuccess)
            {
                return testResult;
            }
        }

        // 1. Anti-cheating check: Test count preservation
        if (bag.TryGet<int>(PipelineDataBagKeys.TestsBaselineTotal, out var baselineTests) &&
            bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentTotal, out var currentTests))
        {
            if (currentTests < baselineTests)
            {
                var error = $"Anti-cheating violation: total test count decreased from {baselineTests} to {currentTests}. Tests must not be deleted or disabled.";
                bag.Set(PipelineDataBagKeys.CoverageFailureReason, error);
                return StageExecutionResult.Failure(error, targetStage);
            }
        }

        bool hasBaseline = bag.TryGet<double>(PipelineDataBagKeys.CoverageBaselineLineRate, out var baselineLineRate);
        bool hasCurrent = bag.TryGet<double>(PipelineDataBagKeys.CoverageCurrentLineRate, out var currentLineRate);

        // 2. Missing coverage check: fail if baseline exists, otherwise warn gracefully if unconfigured
        if (!hasCurrent)
        {
            if (hasBaseline)
            {
                var error = $"Coverage gate failure: baseline coverage was established at {baselineLineRate:P2}, but current code coverage could not be extracted from test runner output or coverage reports. Ensure coverage collection remains enabled.";
                bag.Set(PipelineDataBagKeys.CoverageFailureReason, error);
                _logger?.LogError("{Error}", error);
                return StageExecutionResult.Failure(error, targetStage);
            }

            var warning = "Code coverage metric could not be extracted from test runner output or coverage reports. Ensure tests execute with coverage collection enabled (e.g. --collect:\"XPlat Code Coverage\"). Coverage check skipped as a warning.";
            bag.Set(PipelineDataBagKeys.CoverageFailureReason, warning);
            _logger?.LogWarning("{Warning}", warning);

            if (!bag.TryGet<string>(PipelineDataBagKeys.StageCommand(StageId), out _))
            {
                bag.Set(PipelineDataBagKeys.StageCommand(StageId), "coverage evaluation");
                bag.Set(PipelineDataBagKeys.StageOutput(StageId), warning);
            }

            return StageExecutionResult.Success();
        }

        // 3. Code coverage regression check
        if (hasBaseline)
        {
            if (currentLineRate < baselineLineRate)
            {
                var delta = currentLineRate - baselineLineRate;
                bag.Set(PipelineDataBagKeys.CoverageDelta, delta);
                var error = $"Coverage regression detected: line coverage decreased from {baselineLineRate:P2} to {currentLineRate:P2} (Delta: {delta:P2}). Quality gate requires Delta >= 0.0%.";
                bag.Set(PipelineDataBagKeys.CoverageFailureReason, error);
                return StageExecutionResult.Failure(error, targetStage);
            }

            bag.Set(PipelineDataBagKeys.CoverageDelta, currentLineRate - baselineLineRate);
        }
        else
        {
            // Establish baseline coverage if not set yet (e.g. during pre-impl baseline)
            bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, currentLineRate);
        }

        if (!bag.TryGet<string>(PipelineDataBagKeys.StageCommand(StageId), out _))
        {
            bag.Set(PipelineDataBagKeys.StageCommand(StageId), "coverage evaluation");
            var summary = $"Code coverage verified: {currentLineRate:P2} line coverage." +
                (bag.TryGet<double>(PipelineDataBagKeys.CoverageBaselineLineRate, out var b) ? $" (Baseline: {b:P2})" : "");
            bag.Set(PipelineDataBagKeys.StageOutput(StageId), summary);
        }

        return StageExecutionResult.Success();
    }
}
