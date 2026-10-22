using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskMutationServiceTests
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockTaskOrchestrator _taskOrchestrator;
    private readonly TaskMutationService _service;
    private readonly Workspace _workspace;

    public TaskMutationServiceTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _markdownSync = new MockTaskMarkdownSyncService();
        _taskOrchestrator = new MockTaskOrchestrator();

        _service = new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            _markdownSync,
            _taskOrchestrator,
            NullLogger<TaskMutationService>.Instance);

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = Path.GetTempPath(),
            DefaultBaseBranch = "main"
        };
        _workspaceRepository.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task UpdateTitleAsync_ThrowsArgumentException_WhenTitleIsEmpty()
    {
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Original" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateTitleAsync(task, "   "));
    }

    [Fact]
    public async Task UpdateTitleAsync_NoOps_WhenTitleIsUnchanged()
    {
        var task = new ForgeTask { Id = 2, WorkspaceId = 1, Title = "Unchanged Title" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateTitleAsync(task, "Unchanged Title");

        var history = await _taskRepository.GetHistoryAsync(2);
        Assert.Single(history);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public async Task UpdateTitleAsync_UpdatesTaskAndRecordsHistoryWithUserAttribution()
    {
        var task = new ForgeTask { Id = 3, WorkspaceId = 1, Title = "Old Title" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateTitleAsync(task, "New Title");

        Assert.Equal("New Title", task.Title);
        var history = await _taskRepository.GetHistoryAsync(3);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(2, v2.Version);
        Assert.Equal("New Title", v2.Title);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Renamed task from \"Old Title\" to \"New Title\"", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateDescriptionAsync_UpdatesTaskAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 4, WorkspaceId = 1, Description = "Line 1" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateDescriptionAsync(task, "Line 1\nLine 2 Added");

        Assert.Equal("Line 1\nLine 2 Added", task.Description);
        var history = await _taskRepository.GetHistoryAsync(4);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(2, v2.Version);
        Assert.Equal("Line 1\nLine 2 Added", v2.Description);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated task description and specification", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdatePriorityAsync_UpdatesPriorityAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 5, WorkspaceId = 1, Priority = TaskPriority.Normal };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdatePriorityAsync(task, TaskPriority.Critical);

        Assert.Equal(TaskPriority.Critical, task.Priority);
        var history = await _taskRepository.GetHistoryAsync(5);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(TaskPriority.Critical, v2.Priority);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated Priority from P3 to P1", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateMergeModeAsync_UpdatesMergeModeAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 6, WorkspaceId = 1, MergeMode = MergeMode.ManualReviewRequired };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateMergeModeAsync(task, MergeMode.Automatic);

        Assert.Equal(MergeMode.Automatic, task.MergeMode);
        var history = await _taskRepository.GetHistoryAsync(6);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(MergeMode.Automatic, v2.MergeMode);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated MergeMode from ManualReviewRequired to Automatic", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateDependenciesAsync_UpdatesDependenciesAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 7, WorkspaceId = 1 };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateDependenciesAsync(task, new[] { 101, 102 });

        var deps = await _taskRepository.GetDependenciesAsync(7);
        Assert.Equal(2, deps.Count);
        Assert.Contains(101, deps);
        Assert.Contains(102, deps);

        var history = await _taskRepository.GetHistoryAsync(7);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated dependencies (2 prerequisite(s))", v2.ChangeReason);
        Assert.Contains("101", v2.SerializedDependencies);
        Assert.Contains("102", v2.SerializedDependencies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public async Task UpdatePriorityAsync_ThrowsArgumentOutOfRangeException_WhenPriorityOutOfRange(int invalidPriority)
    {
        var task = new ForgeTask { Id = 8, WorkspaceId = 1, Priority = TaskPriority.Normal };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdatePriorityAsync(task, invalidPriority));
    }

    [Fact]
    public async Task UpdateMergeModeAsync_ThrowsArgumentOutOfRangeException_WhenMergeModeInvalid()
    {
        var task = new ForgeTask { Id = 9, WorkspaceId = 1, MergeMode = MergeMode.ManualReviewRequired };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdateMergeModeAsync(task, (MergeMode)999));
    }

    [Fact]
    public async Task UpdateDependenciesAsync_ThrowsArgumentException_WhenTaskDependsOnItself()
    {
        var task = new ForgeTask { Id = 10, WorkspaceId = 1 };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateDependenciesAsync(task, new[] { 10 }));
    }

    [Fact]
    public async Task UpdateDependenciesAsync_UpdatesInMemoryTaskDependencies()
    {
        var task = new ForgeTask { Id = 11, WorkspaceId = 1 };
        await _taskRepository.CreateWithBaselineAsync(task, new[] { 50 }, AuditActors.User, "baseline");

        await _service.UpdateDependenciesAsync(task, new[] { 60, 70 });

        Assert.NotNull(task.Dependencies);
        Assert.Equal(2, task.Dependencies.Count);
        Assert.Contains(task.Dependencies, d => d.DependsOnTaskId == 60);
        Assert.Contains(task.Dependencies, d => d.DependsOnTaskId == 70);
        Assert.DoesNotContain(task.Dependencies, d => d.DependsOnTaskId == 50);
    }

    [Fact]
    public async Task UpdateTitleAsync_PreservesDependenciesOnDisk_WhenPrerequisiteIdsNull()
    {
        var task = new ForgeTask { Id = 12, WorkspaceId = 1, Title = "Initial Title" };
        await _taskRepository.CreateWithBaselineAsync(task, new[] { 88, 99 }, AuditActors.User, "baseline");

        // Call UpdateTitleAsync without passing prerequisiteIds
        await _service.UpdateTitleAsync(task, "New Preserved Title");

        var syncCalls = _markdownSync.SyncTaskCalls;
        Assert.NotEmpty(syncCalls);
        var lastSync = syncCalls.Last();
        Assert.Equal(12, lastSync.Task.Id);
        Assert.NotNull(lastSync.Dependencies);
        Assert.Equal(2, lastSync.Dependencies!.Count);
        Assert.Contains(88, lastSync.Dependencies);
        Assert.Contains(99, lastSync.Dependencies);
    }

    [Fact]
    public async Task RejectTaskAsync_DelegatesToOrchestrator_WithReason_AndUpdatesEntityStatus()
    {
        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "To be rejected",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        await _service.RejectTaskAsync(task, "Not needed anymore");

        Assert.NotNull(_taskOrchestrator.LastRejectCall);
        Assert.Equal(42, _taskOrchestrator.LastRejectCall.Value.TaskId);
        Assert.Equal("Not needed anymore", _taskOrchestrator.LastRejectCall.Value.Reason);

        Assert.Equal(TaskOverallStatus.Rejected, task.Status);
        Assert.False(task.IsWaitingForHuman);
        Assert.True(task.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
    }
}
