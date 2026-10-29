using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDiffServiceTests
{
    [Fact]
    public async Task GetTaskDiffAsync_DelegatesToTaskReviewService_AndReturnsFiles()
    {
        var mockReviewService = new Mock<ITaskReviewService>();
        var sampleDiffs = new List<FileDiffSummary>
        {
            new("src/ModuleA.cs", FileChangeType.Modified, 5, 2, "old", "new")
        };
        mockReviewService
            .Setup(s => s.GetTaskDiffAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskDiffResult(sampleDiffs, DiffProvenance.LiveWorktree, "main"));

        var diffService = new TaskDiffService(mockReviewService.Object);
        var result = await diffService.GetTaskDiffAsync(123);

        Assert.Single(result.Files);
        Assert.Equal("src/ModuleA.cs", result.Files[0].Path);
        mockReviewService.Verify(s => s.GetTaskDiffAsync(123, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetTaskDiffAsync_DelegatesToTaskReviewService()
    {
        var mockReviewService = new Mock<ITaskReviewService>();
        var sampleDiffs = new List<FileDiffSummary>
        {
            new("src/ModuleA.cs", FileChangeType.Modified, 5, 2, "old", "new")
        };
        var sampleResult = new TaskDiffResult(sampleDiffs, DiffProvenance.LiveWorktree, "main");
        mockReviewService
            .Setup(s => s.GetTaskDiffAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sampleResult);

        var diffService = new TaskDiffService(mockReviewService.Object);
        var result = await diffService.GetTaskDiffAsync(123);

        Assert.Equal(DiffProvenance.LiveWorktree, result.Provenance);
        Assert.Single(result.Files);
        Assert.Equal(5, result.TotalAdditions);
        Assert.Equal(2, result.TotalDeletions);
        mockReviewService.Verify(s => s.GetTaskDiffAsync(123, It.IsAny<CancellationToken>()), Times.Once);
    }
}
