using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests.Workflow;

public class QualityGateMetricsExtractorTests
{
    [Fact]
    public void Extract_UsesFallbackPipelineDataBagKeys_WhenCurrentMetricKeysAreAbsent()
    {
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.CoverageCurrent, 82.5);
        bag.Set(PipelineDataBagKeys.CoverageBaseline, 80.0);
        bag.Set(PipelineDataBagKeys.TestsTotal, 40);
        bag.Set(PipelineDataBagKeys.TestsFailed, 2);
        bag.Set(PipelineDataBagKeys.TestsPassed, 38);
        bag.Set(PipelineDataBagKeys.TestsDurationMs, 1250);
        bag.Set(PipelineDataBagKeys.WarningsCurrentCount, 3);
        bag.Set(PipelineDataBagKeys.WarningsBaselineCount, 5);

        var metrics = QualityGateMetricsExtractor.Extract(new ForgeTask
        {
            Id = 7,
            WorkspaceId = 1,
            PipelineDataJson = bag.ToJson()
        });

        Assert.Equal(82.5, metrics.CurrentCoverage);
        Assert.Equal(80.0, metrics.BaselineCoverage);
        Assert.Equal(2.5, metrics.CoverageDelta);
        Assert.Equal(40, metrics.TotalTests);
        Assert.Equal(38, metrics.PassedTests);
        Assert.Equal(2, metrics.FailedTests);
        Assert.Equal(1250, metrics.DurationMs);
        Assert.Equal(3, metrics.CurrentWarnings);
        Assert.Equal(5, metrics.BaselineWarnings);
        Assert.Equal(-2, metrics.WarningDelta);
    }

    [Fact]
    public void Extract_PrefersCurrentMetricKeysAndScalesFractionalLineRates()
    {
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.9125);
        bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.85);
        bag.Set(PipelineDataBagKeys.CoverageCurrent, 10.0);
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 100);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 1);
        bag.Set(PipelineDataBagKeys.TestsTotal, 5);

        var metrics = QualityGateMetricsExtractor.Extract(new ForgeTask
        {
            Id = 8,
            WorkspaceId = 1,
            PipelineDataJson = bag.ToJson()
        });

        Assert.Equal(91.25, metrics.CurrentCoverage);
        Assert.Equal(85.0, metrics.BaselineCoverage);
        Assert.Equal(6.25, metrics.CoverageDelta);
        Assert.Equal(100, metrics.TotalTests);
        Assert.Equal(99, metrics.PassedTests);
        Assert.Equal(1, metrics.FailedTests);
    }
}
