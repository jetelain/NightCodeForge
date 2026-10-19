using Bunit;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Triage;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TriageTaskProposalCardTests : BunitContext
{
    [Theory]
    [InlineData(TaskPriority.Critical, "badge-priority-1", "Critical / Urgent", "P1")]
    [InlineData(TaskPriority.High, "badge-priority-2", "High", "P2")]
    [InlineData(TaskPriority.Normal, "badge-priority-3", "Normal", "P3")]
    [InlineData(TaskPriority.Low, "badge-priority-4", "Low", "P4")]
    [InlineData(TaskPriority.Trivial, "badge-priority-5", "Lowest / Trivial", "P5")]
    public void PriorityBadge_RendersPriorityClass_AndStandardTitleLabel(
        int priority,
        string expectedClass,
        string expectedTitle,
        string expectedText)
    {
        var task = new ForgeTask
        {
            Id = 10,
            Title = "Task with priority",
            Description = "Task description",
            Priority = priority,
            Status = TaskOverallStatus.Suggested
        };

        var cut = Render<TriageTaskProposalCard>(p => p.Add(c => c.Task, task));

        var badge = cut.Find($".forge-badge.{expectedClass}");
        Assert.NotNull(badge);
        Assert.Contains(expectedText, badge.TextContent);
        Assert.Equal(expectedTitle, badge.GetAttribute("title"));
    }
}
