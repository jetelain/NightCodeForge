using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Tests;

public class TaskRepositoryExecutionScopingTests
{
    [Fact]
    public async Task InMemoryTaskRepository_GetExecutedTasksByWorkspaceIdAsync_ReturnsOnlyExecutedTasks()
    {
        // Arrange
        var repo = new InMemoryTaskRepository();
        int workspaceId = 42;

        // 1. Fresh unstarted Open task (HasExecutionRun = false)
        await repo.CreateAsync(new ForgeTask
        {
            Id = 1,
            WorkspaceId = workspaceId,
            Title = "Unstarted Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false
        });

        // 2. Unstarted Suggested task (HasExecutionRun = false)
        await repo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = workspaceId,
            Title = "Suggested Backlog Task",
            Status = TaskOverallStatus.Suggested,
            HasExecutionRun = false
        });

        // 3. Rolled back Open task (HasExecutionRun = true, MaxExecutedStageOrder = 2)
        await repo.CreateAsync(new ForgeTask
        {
            Id = 3,
            WorkspaceId = workspaceId,
            Title = "Rolled Back Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 2
        });

        // 4. Executed Implemented task (HasExecutionRun = true)
        await repo.CreateAsync(new ForgeTask
        {
            Id = 4,
            WorkspaceId = workspaceId,
            Title = "Implemented Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 4
        });

        // 5. Executed Closed task (HasExecutionRun = true)
        await repo.CreateAsync(new ForgeTask
        {
            Id = 5,
            WorkspaceId = workspaceId,
            Title = "Closed Task",
            Status = TaskOverallStatus.Closed,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 7
        });

        // 6. Executed task in a different workspace (WorkspaceId = 999, HasExecutionRun = true)
        await repo.CreateAsync(new ForgeTask
        {
            Id = 6,
            WorkspaceId = 999,
            Title = "Other Workspace Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        });

        // Act
        var executedTasks = await repo.GetExecutedTasksByWorkspaceIdAsync(workspaceId);

        // Assert
        Assert.Equal(3, executedTasks.Count);
        Assert.Contains(executedTasks, t => t.Id == 3 && t.Status == TaskOverallStatus.Open && t.HasExecutionRun);
        Assert.Contains(executedTasks, t => t.Id == 4 && t.Status == TaskOverallStatus.Implemented && t.HasExecutionRun);
        Assert.Contains(executedTasks, t => t.Id == 5 && t.Status == TaskOverallStatus.Closed && t.HasExecutionRun);
        Assert.DoesNotContain(executedTasks, t => t.Id == 1);
        Assert.DoesNotContain(executedTasks, t => t.Id == 2);
        Assert.DoesNotContain(executedTasks, t => t.Id == 6);
    }
}
