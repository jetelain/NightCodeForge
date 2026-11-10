using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskCostPresentationHelperTests
{
    [Fact]
    public void ComputeTaskCost_WhenTaskOrRecordsNullOrEmpty_ReturnsEmptyWithHasCostDataFalse()
    {
        // Null task
        var summaryNullTask = TaskCostPresentationHelper.ComputeTaskCost(null);
        Assert.False(summaryNullTask.HasCostData);
        Assert.Equal(0, summaryNullTask.TotalCost);

        // Empty execution records
        var taskEmpty = new ForgeTask { Id = 1, Title = "No records" };
        var summaryEmpty = TaskCostPresentationHelper.ComputeTaskCost(taskEmpty);
        Assert.False(summaryEmpty.HasCostData);
        Assert.Equal(0, summaryEmpty.TotalCost);

        // Records without cost
        var taskUnpriced = new ForgeTask
        {
            Id = 2,
            Title = "Unpriced",
            ExecutionRecords = new List<StageExecutionRecord>
            {
                new() { StageId = "dev", Cost = null, PromptTokens = 500, CompletionTokens = 200 }
            }
        };
        var summaryUnpriced = TaskCostPresentationHelper.ComputeTaskCost(taskUnpriced);
        Assert.False(summaryUnpriced.HasCostData);
        Assert.Equal(0, summaryUnpriced.TotalCost);
    }

    [Fact]
    public void ComputeTaskCost_RollsUpMultipleRecords_IncludingRetriesAndSupersededRuns()
    {
        var task = new ForgeTask
        {
            Id = 10,
            Title = "Multi-attempt Task",
            ExecutionRecords = new List<StageExecutionRecord>
            {
                // Attempt 1 (failed)
                new()
                {
                    StageId = "impl",
                    Cost = 0.015,
                    ActualCloudCost = 0.010,
                    ElectricityCost = 0.005,
                    CloudSavings = 0.040,
                    Currency = "$",
                    Status = StageExecutionStatus.Failed
                },
                // Attempt 2 (retried, success)
                new()
                {
                    StageId = "impl",
                    Cost = 0.020,
                    ActualCloudCost = 0.012,
                    ElectricityCost = 0.008,
                    CloudSavings = 0.060,
                    Currency = "$",
                    Status = StageExecutionStatus.Completed
                },
                // Stage 2 (review)
                new()
                {
                    StageId = "review",
                    Cost = 0.007,
                    ActualCloudCost = 0.003,
                    ElectricityCost = 0.004,
                    CloudSavings = 0.020,
                    Currency = "$",
                    Status = StageExecutionStatus.Completed
                }
            }
        };

        var summary = TaskCostPresentationHelper.ComputeTaskCost(task);

        Assert.True(summary.HasCostData);
        Assert.Equal(0.042, summary.TotalCost);
        Assert.Equal(0.025, summary.ActualCloudCost);
        Assert.Equal(0.017, summary.ElectricityCost);
        Assert.Equal(0.120, summary.CloudSavings);
        Assert.Equal("$", summary.Currency);
    }

    [Fact]
    public void ComputeConversationCost_MapsFieldsCorrectly()
    {
        var conv = new Conversation
        {
            Id = 42,
            Title = "Ideation Discovery",
            Cost = 0.035,
            ActualCloudCost = 0.025,
            ElectricityCost = 0.010,
            CloudSavings = 0.090,
            Currency = "€"
        };

        var summary = TaskCostPresentationHelper.ComputeConversationCost(conv);

        Assert.True(summary.HasCostData);
        Assert.Equal(0.035, summary.TotalCost);
        Assert.Equal(0.025, summary.ActualCloudCost);
        Assert.Equal(0.010, summary.ElectricityCost);
        Assert.Equal(0.090, summary.CloudSavings);
        Assert.Equal("€", summary.Currency);

        // Null conversation
        var summaryNull = TaskCostPresentationHelper.ComputeConversationCost(null);
        Assert.False(summaryNull.HasCostData);

        // Unpriced conversation
        var unpricedConv = new Conversation { Id = 43, Cost = null };
        var summaryUnpriced = TaskCostPresentationHelper.ComputeConversationCost(unpricedConv);
        Assert.False(summaryUnpriced.HasCostData);
    }

    [Fact]
    public void ComputeUsageDeltaCost_MapsFieldsCorrectly()
    {
        var delta = new SessionUsageDelta(
            InputTokens: 1000,
            OutputTokens: 500,
            CachedInputTokens: 200,
            Cost: 0.028,
            ActualCloudCost: 0.020,
            ElectricityCost: 0.008,
            CloudSavings: 0.050,
            Currency: "$"
        );

        var summary = TaskCostPresentationHelper.ComputeUsageDeltaCost(delta);

        Assert.True(summary.HasCostData);
        Assert.Equal(0.028, summary.TotalCost);
        Assert.Equal(0.020, summary.ActualCloudCost);
        Assert.Equal(0.008, summary.ElectricityCost);
        Assert.Equal(0.050, summary.CloudSavings);
        Assert.Equal("$", summary.Currency);

        // Null delta
        var summaryNull = TaskCostPresentationHelper.ComputeUsageDeltaCost(null);
        Assert.False(summaryNull.HasCostData);

        // Unpriced delta
        var unpriced = new SessionUsageDelta(100, 50, 0, null, null, null, null, null);
        var summaryUnpriced = TaskCostPresentationHelper.ComputeUsageDeltaCost(unpriced);
        Assert.False(summaryUnpriced.HasCostData);
    }

    [Theory]
    [InlineData(0.042, "$", "$0.042")]
    [InlineData(0.042, "€", "€0.042")]
    [InlineData(0.0004, "$", "< $0.001")]
    [InlineData(0.0004, "€", "< €0.001")]
    [InlineData(0.00001, null, "< $0.001")]
    [InlineData(0.0, "$", "$0.000")]
    [InlineData(1.25, "$", "$1.250")]
    public void FormatCost_FormatsWithAppropriatePrecisionAndFloor(double? cost, string? currency, string expected)
    {
        var formatted = TaskCostPresentationHelper.FormatCost(cost, currency);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatCost_WhenNull_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TaskCostPresentationHelper.FormatCost(null));
    }

    [Fact]
    public void BuildCostBreakdownTooltip_ProducesFormattedTooltipString()
    {
        var summary = new TaskCostSummary(
            TotalCost: 0.042,
            ActualCloudCost: 0.035,
            ElectricityCost: 0.007,
            CloudSavings: 0.120,
            Currency: "$",
            HasCostData: true
        );

        var tooltip = TaskCostPresentationHelper.BuildCostBreakdownTooltip(summary);

        Assert.Equal("Estimated Spend: $0.042 (Cloud: $0.035 | Electricity: $0.007 | Cloud Savings: $0.120)", tooltip);

        var emptySummary = TaskCostSummary.Empty;
        Assert.Equal(string.Empty, TaskCostPresentationHelper.BuildCostBreakdownTooltip(emptySummary));
    }

    [Fact]
    public void FormatStageCost_AndBuildStageCostTooltip_WorkForStageRecord()
    {
        var rec = new StageExecutionRecord
        {
            StageId = "impl",
            Cost = 0.025,
            ActualCloudCost = 0.018,
            ElectricityCost = 0.007,
            CloudSavings = 0.080,
            Currency = "$"
        };

        var formatted = TaskCostPresentationHelper.FormatStageCost(rec);
        Assert.Equal("$0.025", formatted);

        var tooltip = TaskCostPresentationHelper.BuildStageCostTooltip(rec);
        Assert.Equal("Stage Spend: $0.025 (Cloud: $0.018 | Electricity: $0.007 | Cloud Savings: $0.080)", tooltip);

        var unpriced = new StageExecutionRecord { StageId = "test", Cost = null };
        Assert.Equal(string.Empty, TaskCostPresentationHelper.FormatStageCost(unpriced));
        Assert.Equal(string.Empty, TaskCostPresentationHelper.BuildStageCostTooltip(unpriced));
    }

    [Fact]
    public void ComputeTaskCost_WhenCostNull_CalculatesFromCloudAndElectricity()
    {
        var task = new ForgeTask
        {
            Id = 99,
            Title = "Task with Split Cost",
            ExecutionRecords = new List<StageExecutionRecord>
            {
                new()
                {
                    StageId = "impl",
                    Cost = null,
                    ActualCloudCost = 0.015,
                    ElectricityCost = 0.005,
                    CloudSavings = 0.040,
                    Currency = "€"
                }
            }
        };

        var summary = TaskCostPresentationHelper.ComputeTaskCost(task);

        Assert.True(summary.HasCostData);
        Assert.Equal(0.020, summary.TotalCost);
        Assert.Equal(0.015, summary.ActualCloudCost);
        Assert.Equal(0.005, summary.ElectricityCost);
        Assert.Equal("€", summary.Currency);
    }

    [Fact]
    public void AccumulateCost_CombinesExistingCostSummaryWithUsageDelta()
    {
        var initial = new TaskCostSummary(
            TotalCost: 0.015,
            ActualCloudCost: 0.010,
            ElectricityCost: 0.005,
            CloudSavings: 0.030,
            Currency: "$",
            HasCostData: true
        );

        var delta = new SessionUsageDelta(
            InputTokens: 500,
            OutputTokens: 200,
            CachedInputTokens: 100,
            Cost: 0.025,
            ActualCloudCost: 0.018,
            ElectricityCost: 0.007,
            CloudSavings: 0.050,
            Currency: "$"
        );

        var accumulated = TaskCostPresentationHelper.AccumulateCost(initial, delta);

        Assert.True(accumulated.HasCostData);
        Assert.Equal(0.040, accumulated.TotalCost);
        Assert.Equal(0.028, accumulated.ActualCloudCost);
        Assert.Equal(0.012, accumulated.ElectricityCost);
        Assert.Equal(0.080, accumulated.CloudSavings);
        Assert.Equal("$", accumulated.Currency);

        // Null delta preserves current
        var withNull = TaskCostPresentationHelper.AccumulateCost(initial, null);
        Assert.Same(initial, withNull);

        // Empty current initializes from delta
        var fromEmpty = TaskCostPresentationHelper.AccumulateCost(TaskCostSummary.Empty, delta);
        Assert.True(fromEmpty.HasCostData);
        Assert.Equal(0.025, fromEmpty.TotalCost);
    }

    [Fact]
    public void FormatCost_WhenNaNOrInfinity_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, TaskCostPresentationHelper.FormatCost(double.NaN));
        Assert.Equal(string.Empty, TaskCostPresentationHelper.FormatCost(double.PositiveInfinity));
        Assert.Equal(string.Empty, TaskCostPresentationHelper.FormatCost(double.NegativeInfinity));
    }
}

