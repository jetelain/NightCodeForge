using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class BacklogMutationToolServiceTests
{
    private const int WorkspaceId = 1;
    private const int OtherWorkspaceId = 2;
    private const string RepoPath = "/repo";

    private readonly InMemoryTaskRepository _repository = new();
    private readonly Mock<ITaskMarkdownSyncService> _markdownSync = new();
    private readonly Mock<IAgentActivityStreamer> _streamer = new();
    private readonly WorkspaceLockProvider _locks = new();
    private readonly BacklogMutationToolService _service;
    private readonly BacklogToolScope _scope = new(WorkspaceId, RepoPath);

    public BacklogMutationToolServiceTests()
    {
        _service = new BacklogMutationToolService(
            _repository,
            new DependencyResolutionEngine(new Mock<ITaskDependencyStore>().Object),
            _markdownSync.Object,
            _locks,
            _streamer.Object,
            NullLogger<BacklogMutationToolService>.Instance);
    }

    private async Task<ForgeTask> AddTaskAsync(
        string title,
        TaskOverallStatus status = TaskOverallStatus.Suggested,
        int workspaceId = WorkspaceId,
        bool hasExecutionRun = false,
        IEnumerable<int>? dependsOn = null,
        int priority = TaskPriority.Normal)
    {
        return await _repository.CreateAsync(
            new ForgeTask
            {
                WorkspaceId = workspaceId,
                Title = title,
                Description = $"{title} description",
                Status = status,
                HasExecutionRun = hasExecutionRun,
                Priority = priority
            },
            dependsOn);
    }

    private void VerifyMarkdownSynced(int taskId, Times times) =>
        _markdownSync.Verify(
            s => s.SyncTaskToDiskAsync(
                It.Is<ForgeTask>(t => t.Id == taskId),
                RepoPath,
                It.IsAny<IReadOnlyList<int>?>(),
                null,
                It.IsAny<CancellationToken>()),
            times);

    // ---- get_task ----

    [Fact]
    public async Task GetTask_ReturnsFullDetailsIncludingPrerequisitesAndDependents()
    {
        var prerequisite = await AddTaskAsync("Prerequisite");
        var task = await AddTaskAsync("Target", TaskOverallStatus.Open, dependsOn: [prerequisite.Id], priority: TaskPriority.High);
        var dependent = await AddTaskAsync("Dependent", dependsOn: [task.Id]);

        var json = await _service.GetTaskAsync(_scope, task.Id, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(task.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("Target", root.GetProperty("title").GetString());
        Assert.Equal("Target description", root.GetProperty("description").GetString());
        Assert.Equal("Open", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("hasExecutionRun").GetBoolean());
        Assert.Equal(TaskPriority.High, root.GetProperty("priority").GetInt32());
        Assert.Equal("ManualReviewRequired", root.GetProperty("mergeMode").GetString());
        Assert.Equal("main", root.GetProperty("baseBranch").GetString());
        Assert.Equal([prerequisite.Id], root.GetProperty("prerequisites").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal([dependent.Id], root.GetProperty("dependentTaskIds").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public async Task GetTask_UnknownTask_ThrowsTaskNotFound()
    {
        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.GetTaskAsync(_scope, 999, CancellationToken.None));
    }

    [Fact]
    public async Task GetTask_TaskOfAnotherWorkspace_ThrowsTaskNotFound()
    {
        var foreign = await AddTaskAsync("Foreign", workspaceId: OtherWorkspaceId);

        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.GetTaskAsync(_scope, foreign.Id, CancellationToken.None));
    }

    // ---- edit_task ----

    [Fact]
    public async Task EditTask_UpdatesFieldsRecordsIdeationAuditAndSyncsMarkdown()
    {
        var task = await AddTaskAsync("Old title");

        var result = await _service.EditTaskAsync(
            _scope,
            new TaskEditRequest(task.Id, Title: "  New title ", Description: "New description", Priority: TaskPriority.Critical, MergeMode: "automatic", BaseBranch: "develop"),
            CancellationToken.None);

        Assert.Contains($"#{task.Id}", result);
        var stored = (await _repository.GetByIdAsync(task.Id))!;
        Assert.Equal("New title", stored.Title);
        Assert.Equal("New description", stored.Description);
        Assert.Equal(TaskPriority.Critical, stored.Priority);
        Assert.Equal(MergeMode.Automatic, stored.MergeMode);
        Assert.Equal("develop", stored.BaseBranch);

        var history = await _repository.GetHistoryAsync(task.Id);
        var last = history[^1];
        Assert.Equal(AuditActors.IdeationAgent, last.ChangedBy);
        Assert.False(string.IsNullOrWhiteSpace(last.ChangeReason));
        VerifyMarkdownSynced(task.Id, Times.Once());
        _streamer.Verify(s => s.BroadcastAsync(It.Is<AgentActivityEvent>(e => e.TaskId == task.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EditTask_ReplacesDependencies()
    {
        var a = await AddTaskAsync("A");
        var b = await AddTaskAsync("B");
        var task = await AddTaskAsync("Target", dependsOn: [a.Id]);

        await _service.EditTaskAsync(_scope, new TaskEditRequest(task.Id, DependsOnIds: [b.Id]), CancellationToken.None);

        Assert.Equal([b.Id], await _repository.GetDependenciesAsync(task.Id));
    }

    [Fact]
    public async Task EditTask_CircularDependency_ThrowsAndLeavesTaskUntouched()
    {
        var first = await AddTaskAsync("First");
        var second = await AddTaskAsync("Second", dependsOn: [first.Id]);

        await Assert.ThrowsAsync<CircularDependencyException>(() => _service.EditTaskAsync(
            _scope,
            new TaskEditRequest(first.Id, Title: "Renamed", DependsOnIds: [second.Id]),
            CancellationToken.None));

        Assert.Equal("First", (await _repository.GetByIdAsync(first.Id))!.Title);
        Assert.Empty(await _repository.GetDependenciesAsync(first.Id));
        VerifyMarkdownSynced(first.Id, Times.Never());
    }

    [Fact]
    public async Task EditTask_SelfDependency_ThrowsCircularDependency()
    {
        var task = await AddTaskAsync("Task");

        await Assert.ThrowsAsync<CircularDependencyException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(task.Id, DependsOnIds: [task.Id]), CancellationToken.None));
    }

    [Fact]
    public async Task EditTask_UnknownPrerequisite_ThrowsTaskNotFound()
    {
        var task = await AddTaskAsync("Task");

        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(task.Id, DependsOnIds: [12345]), CancellationToken.None));
    }

    [Theory]
    [InlineData(TaskOverallStatus.Cancelled)]
    [InlineData(TaskOverallStatus.Rejected)]
    public async Task EditTask_CancelledOrRejectedPrerequisite_ThrowsInvalidOperationException(TaskOverallStatus terminalStatus)
    {
        var prereq = await AddTaskAsync("Terminal Prereq", terminalStatus);
        var task = await AddTaskAsync("Task");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(task.Id, DependsOnIds: [prereq.Id]), CancellationToken.None));

        Assert.Contains($"#{prereq.Id}", ex.Message);
        Assert.Contains(terminalStatus.ToString(), ex.Message);
    }

    [Fact]
    public async Task EditTask_InvalidMergeMode_ThrowsWithoutMutating()
    {
        var task = await AddTaskAsync("Task");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(task.Id, Title: "Changed", MergeMode: "nonsense"), CancellationToken.None));

        Assert.Equal("Task", (await _repository.GetByIdAsync(task.Id))!.Title);
    }

    [Fact]
    public async Task EditTask_UnknownTask_ThrowsTaskNotFound()
    {
        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(404, Title: "x"), CancellationToken.None));
    }

    [Fact]
    public async Task EditTask_TaskOfAnotherWorkspace_ThrowsTaskNotFound()
    {
        var foreign = await AddTaskAsync("Foreign", workspaceId: OtherWorkspaceId);

        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(foreign.Id, Title: "x"), CancellationToken.None));
    }

    [Theory]
    [InlineData(TaskOverallStatus.Reviewed)]
    [InlineData(TaskOverallStatus.Implemented)]
    [InlineData(TaskOverallStatus.Ready)]
    [InlineData(TaskOverallStatus.Closed)]
    [InlineData(TaskOverallStatus.Rejected)]
    [InlineData(TaskOverallStatus.Cancelled)]
    [InlineData(TaskOverallStatus.Failed)]
    public async Task EditTask_NonEditableStatus_ThrowsTaskCannotBeModified(TaskOverallStatus status)
    {
        var task = await AddTaskAsync("Task", status);

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(task.Id, Title: "x"), CancellationToken.None));
        VerifyMarkdownSynced(task.Id, Times.Never());
    }

    [Fact]
    public async Task EditTask_TaskThatHasExecutionRun_ThrowsTaskCannotBeModified()
    {
        var task = await AddTaskAsync("Task", TaskOverallStatus.Open, hasExecutionRun: true);

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => _service.EditTaskAsync(
            _scope, new TaskEditRequest(task.Id, Title: "x"), CancellationToken.None));
    }

    [Fact]
    public async Task EditTask_TaskLockedByRunningStage_ThrowsTaskCannotBeModified()
    {
        var task = await AddTaskAsync("Task", TaskOverallStatus.Open);
        Assert.True(_locks.TryAcquireTaskLock(WorkspaceId, task.Id, out var releaser));
        using (releaser)
        {
            await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => _service.EditTaskAsync(
                _scope, new TaskEditRequest(task.Id, Title: "x"), CancellationToken.None));
        }

        // Once the lock is released the task is editable again.
        await _service.EditTaskAsync(_scope, new TaskEditRequest(task.Id, Title: "x"), CancellationToken.None);
    }

    // ---- set_task_priority / rework_priorities ----

    [Fact]
    public async Task SetTaskPriority_UpdatesPriorityWithAuditAndMarkdownSync()
    {
        var task = await AddTaskAsync("Task");

        await _service.SetTaskPriorityAsync(_scope, task.Id, TaskPriority.Low, CancellationToken.None);

        Assert.Equal(TaskPriority.Low, (await _repository.GetByIdAsync(task.Id))!.Priority);
        Assert.Equal(AuditActors.IdeationAgent, (await _repository.GetHistoryAsync(task.Id))[^1].ChangedBy);
        VerifyMarkdownSynced(task.Id, Times.Once());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task SetTaskPriority_OutOfRange_Throws(int priority)
    {
        var task = await AddTaskAsync("Task");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.SetTaskPriorityAsync(_scope, task.Id, priority, CancellationToken.None));
    }

    [Fact]
    public async Task SetTaskPriority_ClosedTask_ThrowsTaskCannotBeModified()
    {
        var task = await AddTaskAsync("Task", TaskOverallStatus.Closed);

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => _service.SetTaskPriorityAsync(_scope, task.Id, TaskPriority.Low, CancellationToken.None));
    }

    [Fact]
    public async Task ReworkPriorities_UpdatesAllTasksAndSyncsEach()
    {
        var a = await AddTaskAsync("A");
        var b = await AddTaskAsync("B", TaskOverallStatus.Open);

        var result = await _service.ReworkPrioritiesAsync(
            _scope,
            [new TaskPriorityUpdate(a.Id, TaskPriority.Critical), new TaskPriorityUpdate(b.Id, TaskPriority.Trivial)],
            CancellationToken.None);

        Assert.Contains("2 task(s)", result);
        Assert.Equal(TaskPriority.Critical, (await _repository.GetByIdAsync(a.Id))!.Priority);
        Assert.Equal(TaskPriority.Trivial, (await _repository.GetByIdAsync(b.Id))!.Priority);
        VerifyMarkdownSynced(a.Id, Times.Once());
        VerifyMarkdownSynced(b.Id, Times.Once());
    }

    [Fact]
    public async Task ReworkPriorities_OneInvalidEntry_AppliesNothing()
    {
        var editable = await AddTaskAsync("Editable");
        var closed = await AddTaskAsync("Closed", TaskOverallStatus.Closed);

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => _service.ReworkPrioritiesAsync(
            _scope,
            [new TaskPriorityUpdate(editable.Id, TaskPriority.Critical), new TaskPriorityUpdate(closed.Id, TaskPriority.Critical)],
            CancellationToken.None));

        Assert.Equal(TaskPriority.Normal, (await _repository.GetByIdAsync(editable.Id))!.Priority);
        VerifyMarkdownSynced(editable.Id, Times.Never());
    }

    [Fact]
    public async Task ReworkPriorities_EmptyUpdates_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReworkPrioritiesAsync(_scope, [], CancellationToken.None));
    }

    // ---- remove_task ----

    [Fact]
    public async Task RemoveTask_CancelsTaskDetachesDependenciesAndSyncsMarkdown()
    {
        var prerequisite = await AddTaskAsync("Prerequisite");
        var task = await AddTaskAsync("Obsolete", dependsOn: [prerequisite.Id]);

        await _service.RemoveTaskAsync(_scope, task.Id, "Duplicate of another task", CancellationToken.None);

        var stored = (await _repository.GetByIdAsync(task.Id))!;
        Assert.Equal(TaskOverallStatus.Cancelled, stored.Status);
        Assert.Empty(await _repository.GetDependenciesAsync(task.Id));
        var last = (await _repository.GetHistoryAsync(task.Id))[^1];
        Assert.Equal(AuditActors.IdeationAgent, last.ChangedBy);
        Assert.Contains("Duplicate of another task", last.ChangeReason);
        VerifyMarkdownSynced(task.Id, Times.Once());
        _markdownSync.Verify(s => s.ArchiveOrCleanupTaskFileAsync(It.Is<ForgeTask>(t => t.Id == task.Id), RepoPath, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveTask_WithActiveDependents_FailsListingDependentIds()
    {
        var task = await AddTaskAsync("Prerequisite");
        var dependent = await AddTaskAsync("Dependent", dependsOn: [task.Id]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveTaskAsync(_scope, task.Id, null, CancellationToken.None));

        Assert.Contains($"#{dependent.Id}", ex.Message);
        Assert.Equal(TaskOverallStatus.Suggested, (await _repository.GetByIdAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task RemoveTask_WithOnlyCancelledOrRejectedDependents_Succeeds()
    {
        var task = await AddTaskAsync("Prerequisite");
        await AddTaskAsync("Cancelled dependent", TaskOverallStatus.Cancelled, dependsOn: [task.Id]);
        await AddTaskAsync("Rejected dependent", TaskOverallStatus.Rejected, dependsOn: [task.Id]);

        await _service.RemoveTaskAsync(_scope, task.Id, null, CancellationToken.None);

        Assert.Equal(TaskOverallStatus.Cancelled, (await _repository.GetByIdAsync(task.Id))!.Status);
    }

    [Fact]
    public async Task RemoveTask_InProgressRun_ThrowsTaskCannotBeModified()
    {
        var task = await AddTaskAsync("Running", TaskOverallStatus.Open, hasExecutionRun: true);

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => _service.RemoveTaskAsync(_scope, task.Id, null, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveTask_UnknownTask_ThrowsTaskNotFound()
    {
        await Assert.ThrowsAsync<TaskNotFoundException>(() => _service.RemoveTaskAsync(_scope, 777, null, CancellationToken.None));
    }

    [Fact]
    public async Task Mutations_HoldExclusiveTaskLockDuringExecution_AndReleaseAfterCompletion()
    {
        var taskA = await AddTaskAsync("Task A", TaskOverallStatus.Open);
        var taskB = await AddTaskAsync("Task B", TaskOverallStatus.Open);

        var lockHeldDuringSync = new List<int>();
        _markdownSync
            .Setup(s => s.SyncTaskToDiskAsync(
                It.IsAny<ForgeTask>(),
                RepoPath,
                It.IsAny<IReadOnlyList<int>?>(),
                null,
                It.IsAny<CancellationToken>()))
            .Callback<ForgeTask, string, IReadOnlyList<int>?, string?, CancellationToken>((t, _, _, _, _) =>
            {
                if (_locks.IsTaskLocked(WorkspaceId, t.Id))
                {
                    lockHeldDuringSync.Add(t.Id);
                }
            })
            .ReturnsAsync("path.md");

        await _service.EditTaskAsync(_scope, new TaskEditRequest(taskA.Id, Title: "Edited A"), CancellationToken.None);
        Assert.Contains(taskA.Id, lockHeldDuringSync);
        Assert.False(_locks.IsTaskLocked(WorkspaceId, taskA.Id));

        lockHeldDuringSync.Clear();
        await _service.ReworkPrioritiesAsync(
            _scope,
            [new TaskPriorityUpdate(taskB.Id, TaskPriority.High), new TaskPriorityUpdate(taskA.Id, TaskPriority.Low)],
            CancellationToken.None);
        Assert.Contains(taskA.Id, lockHeldDuringSync);
        Assert.Contains(taskB.Id, lockHeldDuringSync);
        Assert.False(_locks.IsTaskLocked(WorkspaceId, taskA.Id));
        Assert.False(_locks.IsTaskLocked(WorkspaceId, taskB.Id));

        lockHeldDuringSync.Clear();
        await _service.RemoveTaskAsync(_scope, taskA.Id, "Cleanup", CancellationToken.None);
        Assert.Contains(taskA.Id, lockHeldDuringSync);
        Assert.False(_locks.IsTaskLocked(WorkspaceId, taskA.Id));
    }

    // ---- propose_tasks ----

    [Fact]
    public async Task ProposeTasks_CreatesDraftTask_ReturnsDraftTaskJson_AndOtherToolsCanSeeIt()
    {
        var prereq = await AddTaskAsync("Base Prereq");

        var resultJson = await _service.ProposeTasksAsync(
            _scope,
            [new TaskProposal("Draft Migration Task", "Initial migration details", priority: 2, dependsOnIds: [prereq.Id])],
            cancellationToken: CancellationToken.None);

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        var createdId = root.GetProperty("id").GetInt32();
        Assert.True(createdId > 0);
        Assert.Equal("Draft Migration Task", root.GetProperty("title").GetString());
        Assert.Equal("Initial migration details", root.GetProperty("description").GetString());
        Assert.Equal("Draft", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("priority").GetInt32());
        Assert.Equal([prereq.Id], root.GetProperty("prerequisites").EnumerateArray().Select(e => e.GetInt32()));

        var stored = await _repository.GetByIdAsync(createdId);
        Assert.NotNull(stored);
        Assert.Equal(TaskOverallStatus.Draft, stored.Status);
        VerifyMarkdownSynced(createdId, Times.Once());

        // 1. get_task can see draft task
        var getJson = await _service.GetTaskAsync(_scope, createdId, CancellationToken.None);
        using var getDoc = JsonDocument.Parse(getJson);
        Assert.Equal(createdId, getDoc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("Draft", getDoc.RootElement.GetProperty("status").GetString());

        // 2. edit_task can edit draft task
        var editResult = await _service.EditTaskAsync(
            _scope,
            new TaskEditRequest(createdId, Title: "Refined Draft Migration", Priority: 1),
            CancellationToken.None);
        Assert.Contains("Refined Draft Migration", editResult);

        var updated = await _repository.GetByIdAsync(createdId);
        Assert.Equal("Refined Draft Migration", updated!.Title);
        Assert.Equal(1, updated.Priority);
        Assert.Equal(TaskOverallStatus.Draft, updated.Status);

        // 3. set_task_priority can change draft task priority
        await _service.SetTaskPriorityAsync(_scope, createdId, 3, CancellationToken.None);
        var reprioritized = await _repository.GetByIdAsync(createdId);
        Assert.Equal(3, reprioritized!.Priority);

        // 4. another task can depend on the draft task
        var followUpJson = await _service.ProposeTasksAsync(
            _scope,
            [new TaskProposal("Follow-up Task", "Depends on draft", priority: 3, dependsOnIds: [createdId])],
            cancellationToken: CancellationToken.None);
        using var followDoc = JsonDocument.Parse(followUpJson);
        var followId = followDoc.RootElement.GetProperty("id").GetInt32();
        var followDeps = await _repository.GetDependenciesAsync(followId);
        Assert.Contains(createdId, followDeps);

        // 5. remove_task can cancel draft task after dependents are removed
        await _service.RemoveTaskAsync(_scope, followId, "remove follow up first", CancellationToken.None);
        var removeResult = await _service.RemoveTaskAsync(_scope, createdId, "no longer needed", CancellationToken.None);
        Assert.Contains("status Cancelled", removeResult);
        var cancelled = await _repository.GetByIdAsync(createdId);
        Assert.Equal(TaskOverallStatus.Cancelled, cancelled!.Status);
    }

    [Fact]
    public async Task ProposeTasks_MultipleProposals_CreatesSequentialDraftTasks_AndReturnsJsonArray()
    {
        var proposals = new List<TaskProposal>
        {
            new("Task 1", "First step", 2),
            new("Task 2", "Second step", 3)
        };

        var json = await _service.ProposeTasksAsync(_scope, proposals, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());

        var id1 = doc.RootElement[0].GetProperty("id").GetInt32();
        var id2 = doc.RootElement[1].GetProperty("id").GetInt32();
        Assert.Equal("Draft", doc.RootElement[0].GetProperty("status").GetString());
        Assert.Equal("Draft", doc.RootElement[1].GetProperty("status").GetString());

        var task2Deps = await _repository.GetDependenciesAsync(id2);
        Assert.Contains(id1, task2Deps);
    }

    [Fact]
    public async Task ProposeTasks_PreValidatesBatch_DoesNotCreatePartialTasksWhenLaterProposalIsInvalid()
    {
        var proposals = new List<TaskProposal>
        {
            new("Valid First Task", "Step 1", 2),
            new("   ", "Invalid empty title", 3)
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ProposeTasksAsync(_scope, proposals, CancellationToken.None));

        var workspaceTasks = await _repository.GetByWorkspaceIdAsync(WorkspaceId);
        Assert.Empty(workspaceTasks);
    }

    [Fact]
    public async Task ProposeTasks_ResolvesDependsOnTitles_ToExistingAndEarlierBatchTaskIds()
    {
        var existing = await AddTaskAsync("Existing Auth Service");

        var proposals = new List<TaskProposal>
        {
            new("Task A - Core Schema", "Schema", 2, dependsOnTitles: ["Existing Auth Service"]),
            new("Task B - Worker", "Worker", 3, dependsOnTitles: ["Task A - Core Schema"]),
            new("Task C - UI", "UI depends on A, not B", 3, dependsOnTitles: ["Task A - Core Schema", $"#{existing.Id}"])
        };

        var json = await _service.ProposeTasksAsync(_scope, proposals, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetArrayLength());

        var idA = doc.RootElement[0].GetProperty("id").GetInt32();
        var idB = doc.RootElement[1].GetProperty("id").GetInt32();
        var idC = doc.RootElement[2].GetProperty("id").GetInt32();

        Assert.Equal([existing.Id], await _repository.GetDependenciesAsync(idA));
        Assert.Equal([idA], await _repository.GetDependenciesAsync(idB));
        Assert.Equal(new[] { existing.Id, idA }.OrderBy(x => x), (await _repository.GetDependenciesAsync(idC)).OrderBy(x => x));
    }
}

