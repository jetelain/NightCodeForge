using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskCreationUiServiceTests
{
    [Fact]
    public async Task CreateTaskAsync_SyncsDependenciesToDisk_AndDefersDispatchWhenPrerequisitesNotClosed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ncf-create-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var taskRepo = new InMemoryTaskRepository();
            var workspaceRepo = new InMemoryWorkspaceRepository();
            var workflowRepo = new InMemoryWorkflowRepository([StandardWorkflowDefinitions.CreateStandard3AgentWorkflow()]);
            var markdownSyncMock = new Mock<ITaskMarkdownSyncService>();
            var mirrorMock = new Mock<IGitHubTaskMirrorService>();
            var prDeliveryMock = new Mock<IGitHubPullRequestDeliveryService>();
            var jobQueueMock = new Mock<ITaskJobQueue>();

            var ws = await workspaceRepo.CreateAsync(new Workspace
            {
                Id = 1,
                Name = "Create WS",
                RepositoryPath = tempDir
            });

            await taskRepo.CreateAsync(new ForgeTask
            {
                Id = 1,
                WorkspaceId = 1,
                Title = "Open Prerequisite",
                Status = TaskOverallStatus.Open
            });

            var service = new TaskCreationUiService(
                taskRepo,
                workspaceRepo,
                workflowRepo,
                markdownSyncMock.Object,
                mirrorMock.Object,
                prDeliveryMock.Object,
                jobQueueMock.Object,
                Options.Create(new SecurityOptions()),
                NullLogger<TaskCreationUiService>.Instance);

            var request = new TaskCreationRequest(
                WorkspaceId: ws.Id,
                Workspace: ws,
                RepositoryPath: tempDir,
                Title: "Dependent Task",
                Description: "Depends on #1",
                WorkflowId: null,
                Priority: TaskPriority.Normal,
                MergeMode: MergeMode.ManualReviewRequired,
                BaseBranch: "main",
                FeatureBranch: null,
                InitialStatus: TaskOverallStatus.Open,
                PrerequisiteIds: new[] { 1 },
                LaunchImmediately: true,
                AuditReason: "Created with open prerequisite");

            var result = await service.CreateTaskAsync(request);

            Assert.False(result.WasDispatched);
            Assert.Null(result.Task.LastHangfireJobId);
            jobQueueMock.Verify(
                q => q.EnqueueStage(It.IsAny<int>(), It.IsAny<StageQueueTier>(), It.IsAny<int>()),
                Times.Never);

            markdownSyncMock.Verify(
                m => m.SyncTaskToDiskAsync(
                    It.Is<ForgeTask>(t => t.Id == result.Task.Id),
                    tempDir,
                    It.Is<IReadOnlyList<int>>(d => d != null && d.SequenceEqual(new[] { 1 })),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreateTaskAsync_WhenAllPrerequisitesClosed_DispatchesImmediately()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ncf-create-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var taskRepo = new InMemoryTaskRepository();
            var workspaceRepo = new InMemoryWorkspaceRepository();
            var workflowRepo = new InMemoryWorkflowRepository([StandardWorkflowDefinitions.CreateStandard3AgentWorkflow()]);
            var markdownSyncMock = new Mock<ITaskMarkdownSyncService>();
            var mirrorMock = new Mock<IGitHubTaskMirrorService>();
            var prDeliveryMock = new Mock<IGitHubPullRequestDeliveryService>();
            var jobQueueMock = new Mock<ITaskJobQueue>();
            jobQueueMock.Setup(q => q.EnqueueStage(It.IsAny<int>(), StageQueueTier.TaskReview, It.IsAny<int>()))
                .Returns("job-123");

            var ws = await workspaceRepo.CreateAsync(new Workspace
            {
                Id = 1,
                Name = "Dispatch WS",
                RepositoryPath = tempDir
            });

            await taskRepo.CreateAsync(new ForgeTask
            {
                Id = 2,
                WorkspaceId = 1,
                Title = "Closed Prerequisite",
                Status = TaskOverallStatus.Closed
            });

            var service = new TaskCreationUiService(
                taskRepo,
                workspaceRepo,
                workflowRepo,
                markdownSyncMock.Object,
                mirrorMock.Object,
                prDeliveryMock.Object,
                jobQueueMock.Object,
                Options.Create(new SecurityOptions()),
                NullLogger<TaskCreationUiService>.Instance);

            var request = new TaskCreationRequest(
                WorkspaceId: ws.Id,
                Workspace: ws,
                RepositoryPath: tempDir,
                Title: "Ready To Dispatch Task",
                Description: "Depends on closed #2",
                WorkflowId: null,
                Priority: TaskPriority.High,
                MergeMode: MergeMode.ManualReviewRequired,
                BaseBranch: "main",
                FeatureBranch: null,
                InitialStatus: TaskOverallStatus.Open,
                PrerequisiteIds: new[] { 2 },
                LaunchImmediately: true,
                AuditReason: "Created with closed prerequisite");

            var result = await service.CreateTaskAsync(request);

            Assert.True(result.WasDispatched);
            Assert.Equal("job-123", result.Task.LastHangfireJobId);
            jobQueueMock.Verify(
                q => q.EnqueueStage(result.Task.Id, StageQueueTier.TaskReview, TaskPriority.High),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
