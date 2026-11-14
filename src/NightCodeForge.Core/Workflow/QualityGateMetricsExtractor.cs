using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Shared helper that extracts <see cref="QualityGateMetrics"/> from a <see cref="ForgeTask"/>'s pipeline data bag.
/// </summary>
public static class QualityGateMetricsExtractor
{
    public static QualityGateMetrics Extract(ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);

        double currentCov = 0.0;
        if (bag.TryGet<double>(PipelineDataBagKeys.CoverageCurrentLineRate, out var cRate))
        {
            currentCov = cRate <= 1.0 ? cRate * 100.0 : cRate;
        }
        else if (bag.TryGet<double>("coverage.current", out var cVal))
        {
            currentCov = cVal;
        }

        double baseCov = currentCov;
        if (bag.TryGet<double>(PipelineDataBagKeys.CoverageBaselineLineRate, out var bRate))
        {
            baseCov = bRate <= 1.0 ? bRate * 100.0 : bRate;
        }
        else if (bag.TryGet<double>("coverage.baseline", out var bVal))
        {
            baseCov = bVal;
        }

        double covDelta = currentCov - baseCov;
        if (bag.TryGet<double>(PipelineDataBagKeys.CoverageDelta, out var explicitCovDelta))
        {
            covDelta = explicitCovDelta <= 1.0 && explicitCovDelta >= -1.0 ? explicitCovDelta * 100.0 : explicitCovDelta;
        }

        int currentWarns = bag.TryGet<int>(PipelineDataBagKeys.WarningsCurrentTotal, out var cw) ? cw :
            (bag.TryGet<int>("warnings.current.count", out cw) ? cw : 0);
        int baseWarns = bag.TryGet<int>(PipelineDataBagKeys.WarningsBaselineTotal, out var bw) ? bw :
            (bag.TryGet<int>("warnings.baseline.count", out bw) ? bw : currentWarns);
        int warnDelta = bag.TryGet<int>(PipelineDataBagKeys.WarningsDelta, out var wd) ? wd : (currentWarns - baseWarns);

        int totalTests = bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentTotal, out var tt) ? tt :
            (bag.TryGet<int>("tests.total", out tt) ? tt : 0);
        int failedTests = bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentFailed, out var ft) ? ft :
            (bag.TryGet<int>("tests.failed", out ft) ? ft : 0);
        int passedTests = bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentPassed, out var pt) ? pt :
            (bag.TryGet<int>("tests.passed", out pt) ? pt : Math.Max(0, totalTests - failedTests));
        int durationMs = bag.TryGet<int>(PipelineDataBagKeys.TestsCurrentDurationMs, out var dm) ? dm :
            (bag.TryGet<int>("tests.duration_ms", out dm) ? dm : 0);

        return new QualityGateMetrics(
            BaselineCoverage: Math.Round(baseCov, 2),
            CurrentCoverage: Math.Round(currentCov, 2),
            CoverageDelta: Math.Round(covDelta, 2),
            BaselineWarnings: baseWarns,
            CurrentWarnings: currentWarns,
            WarningDelta: warnDelta,
            TotalTests: totalTests,
            PassedTests: passedTests,
            FailedTests: failedTests,
            DurationMs: durationMs);
    }
}

