using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class MergeModeHelperTests
{
    [Fact]
    public void MergeModeHelper_ReturnsCorrectLabelsAndDescriptions()
    {
        Assert.Equal("Automatic Merge", MergeModeHelper.GetLabel(MergeMode.Automatic));
        Assert.Equal("Manual Review Required", MergeModeHelper.GetLabel(MergeMode.ManualReviewRequired));

        Assert.Equal("Auto", MergeModeHelper.GetShortLabel(MergeMode.Automatic));
        Assert.Equal("Manual", MergeModeHelper.GetShortLabel(MergeMode.ManualReviewRequired));

        Assert.Contains("Auto-squashes and merges", MergeModeHelper.GetDescription(MergeMode.Automatic));
        Assert.Contains("Pauses at Ready for human maintainer review", MergeModeHelper.GetDescription(MergeMode.ManualReviewRequired));

        Assert.Equal("badge-teal", MergeModeHelper.GetBadgeClass(MergeMode.Automatic));
        Assert.Equal("badge-indigo", MergeModeHelper.GetBadgeClass(MergeMode.ManualReviewRequired));

        Assert.Equal("#14b8a6", MergeModeHelper.GetBadgeColor(MergeMode.Automatic));
        Assert.Equal("#6366f1", MergeModeHelper.GetBadgeColor(MergeMode.ManualReviewRequired));

        Assert.Equal("⚡", MergeModeHelper.GetIcon(MergeMode.Automatic));
        Assert.Equal("🔍", MergeModeHelper.GetIcon(MergeMode.ManualReviewRequired));

        Assert.Equal(2, MergeModeHelper.All.Length);
    }

    [Fact]
    public void TaskPriority_AllAndDescriptions_AreCorrect()
    {
        Assert.Equal(5, TaskPriority.All.Length);
        Assert.Equal(TaskPriority.Critical, TaskPriority.All[0]);
        Assert.Equal(TaskPriority.Trivial, TaskPriority.All[4]);

        Assert.Contains("llm-urgent", TaskPriority.GetDescription(TaskPriority.Critical));
        Assert.Contains("Core architectural prerequisite", TaskPriority.GetDescription(TaskPriority.High));
        Assert.Contains("Default for newly authored tasks", TaskPriority.GetDescription(TaskPriority.Normal));
        Assert.Contains("Minor refactoring", TaskPriority.GetDescription(TaskPriority.Low));
        Assert.Contains("Non-essential cleanup", TaskPriority.GetDescription(TaskPriority.Trivial));

        Assert.Equal("#ef4444", TaskPriority.GetBadgeColor(TaskPriority.Critical));
        Assert.Equal("P1", TaskPriority.GetShortLabel(TaskPriority.Critical));
        Assert.Equal("Critical / Urgent", TaskPriority.GetLabel(TaskPriority.Critical));
    }
}
