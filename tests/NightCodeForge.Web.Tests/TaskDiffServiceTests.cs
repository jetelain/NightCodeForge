using Moq;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDiffServiceTests
{
    [Fact]
    public async Task GetTaskDiffFilesAsync_DelegatesToTaskReviewService()
    {
        var mockReviewService = new Mock<ITaskReviewService>();
        var sampleDiffs = new List<FileDiffSummary>
        {
            new("src/ModuleA.cs", FileChangeType.Modified, 5, 2, "old", "new")
        };
        mockReviewService
            .Setup(s => s.GetTaskDiffFilesAsync(123, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sampleDiffs);

        var diffService = new TaskDiffService(mockReviewService.Object);
        var result = await diffService.GetTaskDiffFilesAsync(123);

        Assert.Single(result);
        Assert.Equal("src/ModuleA.cs", result[0].Path);
        mockReviewService.Verify(s => s.GetTaskDiffFilesAsync(123, It.IsAny<CancellationToken>()), Times.Once);
    }
}
