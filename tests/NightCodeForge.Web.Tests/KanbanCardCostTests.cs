using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Backlog;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class KanbanCardCostTests : BunitContext
{
    [Fact]
    public void KanbanCard_WhenTaskHasRecordedSpend_RendersCompactCostBadgeWithTooltip()
    {
        var task = new ForgeTask
        {
            Id = 88,
            Title = "Task with Stage Spend",
            Status = TaskOverallStatus.Open,
            ExecutionRecords = new List<StageExecutionRecord>
            {
                new()
                {
                    TaskId = 88,
                    StageId = "impl",
                    Cost = 0.042,
                    ActualCloudCost = 0.035,
                    ElectricityCost = 0.007,
                    CloudSavings = 0.120,
                    Currency = "$"
                }
            }
        };

        var cut = Render<KanbanCard>(p => p.Add(c => c.Task, task));

        Assert.Contains("💰 $0.042", cut.Markup);
        Assert.Contains("Estimated Spend: $0.042 (Cloud: $0.035 | Electricity: $0.007 | Cloud Savings: $0.120)", cut.Markup);
    }

    [Fact]
    public void KanbanCard_WhenTaskHasNoCostData_OmitsCostBadge()
    {
        var task = new ForgeTask
        {
            Id = 89,
            Title = "Unpriced Task Card",
            Status = TaskOverallStatus.Open,
            ExecutionRecords = new List<StageExecutionRecord>()
        };

        var cut = Render<KanbanCard>(p => p.Add(c => c.Task, task));

        Assert.DoesNotContain("💰", cut.Markup);
    }
}

