using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class IdeationSessionStateTests
{
    [Fact]
    public void InitialState_ShouldBeEmpty()
    {
        var session = new IdeationSessionState();

        Assert.Empty(session.Messages);
        Assert.Empty(session.Proposals);
        Assert.False(session.IsGenerating);
        Assert.Empty(session.CurrentThinkingBuffer);
        Assert.Empty(session.CurrentMessageBuffer);
        Assert.Null(session.BoundWorkspaceId);
        Assert.Null(session.CustomSystemPrompt);
        Assert.Equal(TaskCostSummary.Empty, session.ActiveSessionCost);
    }

    [Fact]
    public void AddMessage_ShouldAppendAndNotify()
    {
        var session = new IdeationSessionState();
        var notified = false;
        session.OnChange += () => notified = true;

        var msg = new ChatMessage("user", "Hello architect", null, DateTime.UtcNow);
        session.AddMessage(msg);

        Assert.Single(session.Messages);
        Assert.Same(msg, session.Messages[0]);
        Assert.True(notified);
    }

    [Fact]
    public void AddProposal_And_RemoveProposal_ShouldModifyListAndNotify()
    {
        var session = new IdeationSessionState();
        var notifyCount = 0;
        session.OnChange += () => notifyCount++;

        var proposal = new IdeationProposalViewModel
        {
            Title = "Refactor logging",
            Description = "Use ILogger instead of Console",
            Priority = 2
        };

        session.AddProposal(proposal);
        Assert.Single(session.Proposals);
        Assert.Equal(1, notifyCount);

        session.RemoveProposal(proposal);
        Assert.Empty(session.Proposals);
        Assert.Equal(2, notifyCount);
    }

    [Fact]
    public void Reset_ShouldClearAllBuffersAndNotify()
    {
        var session = new IdeationSessionState();
        session.Messages.Add(new ChatMessage("user", "test"));
        session.Proposals.Add(new IdeationProposalViewModel { Title = "Task 1" });
        session.CurrentThinkingBuffer = "thinking...";
        session.CurrentMessageBuffer = "output...";
        session.IsGenerating = true;
        session.ActiveSessionCost = new TaskCostSummary(0.05, 0.04, 0.01, 0.1, "$", HasCostData: true);

        var notified = false;
        session.OnChange += () => notified = true;

        session.Reset();

        Assert.Empty(session.Messages);
        Assert.Empty(session.Proposals);
        Assert.Empty(session.CurrentThinkingBuffer);
        Assert.Empty(session.CurrentMessageBuffer);
        Assert.Empty(session.CurrentToolCalls);
        Assert.False(session.IsGenerating);
        Assert.Equal(TaskCostSummary.Empty, session.ActiveSessionCost);
        Assert.True(notified);
    }

    [Fact]
    public void FromProposal_ShouldMapPropertiesAndResolveDependencies()
    {
        var existingTasks = new List<ForgeTask>
        {
            new() { Id = 10, Title = "Setup DB migrations" },
            new() { Id = 11, Title = "Configure Redis cache" }
        };

        var domainProposal = new IdeationProposal(
            Title: "Add distributed lock",
            Description: "Prevent race condition in worker",
            Priority: 4,
            DependsOnTitles: new[] { "Setup DB migrations", "Unknown Task" });

        var vm = IdeationProposalViewModel.FromProposal(domainProposal, existingTasks);

        Assert.Equal("Add distributed lock", vm.Title);
        Assert.Equal("Prevent race condition in worker", vm.Description);
        Assert.Equal(4, vm.Priority);
        Assert.Single(vm.SelectedDependencyIds);
        Assert.Contains(10, vm.SelectedDependencyIds);
        Assert.Contains("Setup DB migrations", vm.DependsOnTitles);
        Assert.Contains("Unknown Task", vm.DependsOnTitles);
        Assert.False(vm.IsAdded);
        Assert.Null(vm.CreatedTaskId);
    }

    [Fact]
    public void ResolveDependencies_LinksNewlyCreatedTasks_WhenDependenciesAreAddedSequentially()
    {
        var proposal1 = new IdeationProposal("Task 1 - Schema", "Create DB schema");
        var proposal2 = new IdeationProposal("Task 2 - API", "Expose REST endpoints", DependsOnTitles: new[] { "Task 1 - Schema" });

        var vm1 = IdeationProposalViewModel.FromProposal(proposal1);
        var vm2 = IdeationProposalViewModel.FromProposal(proposal2);

        Assert.Empty(vm2.SelectedDependencyIds);
        Assert.Contains("Task 1 - Schema", vm2.DependsOnTitles);

        // Simulate Task 1 being added to the database
        var refreshedWorkspaceTasks = new List<ForgeTask>
        {
            new() { Id = 101, Title = "Task 1 - Schema" }
        };

        vm2.ResolveDependencies(refreshedWorkspaceTasks);

        Assert.Single(vm2.SelectedDependencyIds);
        Assert.Contains(101, vm2.SelectedDependencyIds);
    }

    [Fact]
    public void ChatMessage_WithToolCalls_StoresAndExposesToolCalls()
    {
        var toolVm = new IdeationToolCallViewModel
        {
            ToolCallId = "call_1",
            ToolName = "read_file",
            Arguments = "{\"path\": \"src/app.cs\"}",
            Result = "file contents",
            IsCompleted = true,
            DurationMs = 45
        };

        var msg = new ChatMessage("assistant", "Here is the file.", "thinking...", DateTime.UtcNow, new[] { toolVm });

        Assert.NotNull(msg.ToolCalls);
        Assert.Single(msg.ToolCalls);
        Assert.Equal("read_file", msg.ToolCalls[0].ToolName);
        Assert.Equal(45, msg.ToolCalls[0].DurationMs);
        Assert.True(msg.ToolCalls[0].IsCompleted);
        Assert.False(msg.ToolCalls[0].IsError);
    }

    [Fact]
    public void IdeationToolCallViewModel_TracksRunningAndCompletedState()
    {
        var toolVm = new IdeationToolCallViewModel
        {
            ToolCallId = "call_2",
            ToolName = "run_command",
            Arguments = "{\"cmd\": \"dotnet test\"}",
            IsCompleted = false
        };

        Assert.False(toolVm.IsCompleted);

        toolVm.IsCompleted = true;
        toolVm.Result = "Exit Code: 1\nTests failed";
        toolVm.IsError = true;
        toolVm.DurationMs = 1200;

        Assert.True(toolVm.IsCompleted);
        Assert.True(toolVm.IsError);
        Assert.Equal(1200, toolVm.DurationMs);
    }

    [Fact]
    public void ChatMessage_GetEffectiveHistoryContent_SummarizesToolCallsAndText()
    {
        var toolVm = new IdeationToolCallViewModel
        {
            ToolCallId = "call_1",
            ToolName = "execute_command",
            Arguments = "{\"command\":\"find / -name *.sln\"}",
            Result = "Exit Code: 0\n/workspace/clone/OllamaRouter.sln",
            IsCompleted = true
        };

        var parts = new List<ChatContentPart>
        {
            new TextContentPart("Let me check the solution files."),
            new ToolCallContentPart(toolVm),
            new TextContentPart("I located OllamaRouter.sln.")
        };

        var msg = new ChatMessage("assistant", "Final summary only", Parts: parts);

        var historyContent = msg.GetEffectiveHistoryContent();

        Assert.Contains("Let me check the solution files.", historyContent);
        Assert.Contains("[Tool: execute_command({\"command\":\"find / -name *.sln\"})]", historyContent);
        Assert.Contains("[Result: Exit Code: 0", historyContent);
        Assert.Contains("I located OllamaRouter.sln.", historyContent);
    }

    [Fact]
    public void SelectedRoutingTarget_DefaultsToAuto_AndCanBeConfiguredAndReset()
    {
        var session = new IdeationSessionState();
        Assert.Equal(OllamaRoutingTarget.Auto, session.SelectedRoutingTarget);
        Assert.Null(session.SelectedModelOverride);

        session.SelectedRoutingTarget = OllamaRoutingTarget.Local;
        session.SelectedModelOverride = "qwen2.5-coder:7b";

        Assert.Equal(OllamaRoutingTarget.Local, session.SelectedRoutingTarget);
        Assert.Equal("qwen2.5-coder:7b", session.SelectedModelOverride);

        session.Reset();

        Assert.Equal(OllamaRoutingTarget.Auto, session.SelectedRoutingTarget);
        Assert.Null(session.SelectedModelOverride);
    }

    [Fact]
    public void FromProposal_WithTaskIdAndDependsOnIds_PopulatesCreatedTaskIdAndDependencies_ExcludingSelf()
    {
        var proposal = new IdeationProposal(
            Title: "Draft Feature",
            Description: "Details",
            Priority: 2,
            DependsOnTitles: null,
            TaskId: 105,
            DependsOnIds: [101, 105, 102]);

        var vm = IdeationProposalViewModel.FromProposal(proposal);

        Assert.Equal(105, vm.CreatedTaskId);
        Assert.Equal([101, 102], vm.SelectedDependencyIds);
        Assert.False(vm.IsAdded);
    }

    [Fact]
    public void ResolveDependencies_WhenDependsOnTitlesIsEmpty_StillRemovesCancelledOrRejectedDependencies()
    {
        var proposal = new IdeationProposal(
            Title: "Draft Feature",
            Description: "Details",
            Priority: 2,
            DependsOnTitles: null,
            TaskId: 105,
            DependsOnIds: [101, 102]);

        var vm = IdeationProposalViewModel.FromProposal(proposal);
        Assert.Equal([101, 102], vm.SelectedDependencyIds);

        var tasks = new List<ForgeTask>
        {
            new() { Id = 101, Title = "Active", Status = TaskOverallStatus.Open },
            new() { Id = 102, Title = "Cancelled", Status = TaskOverallStatus.Cancelled }
        };

        vm.ResolveDependencies(tasks);

        Assert.Equal([101], vm.SelectedDependencyIds);
    }

    [Fact]
    public void SyncProposalsWithWorkspaceTasks_UpdatesEditedDraft_MarksPromotedAsAdded_AndRemovesCancelledDraft()
    {
        var session = new IdeationSessionState();
        var p1 = IdeationProposalViewModel.FromProposal(new IdeationProposal("Draft 1", "Desc 1", 3, TaskId: 101));
        var p2 = IdeationProposalViewModel.FromProposal(new IdeationProposal("Draft 2", "Desc 2", 3, TaskId: 102, DependsOnIds: [101]));
        var p3 = IdeationProposalViewModel.FromProposal(new IdeationProposal("Draft 3", "Desc 3", 3, TaskId: 103, DependsOnIds: [102]));
        session.Proposals.AddRange([p1, p2, p3]);

        var workspaceTasks = new List<ForgeTask>
        {
            new()
            {
                Id = 101,
                Title = "Refined Draft 1",
                Description = "Updated Desc 1",
                Priority = 1,
                Status = TaskOverallStatus.Draft,
                Dependencies = [new TaskDependency { TaskId = 101, DependsOnTaskId = 50 }]
            },
            new()
            {
                Id = 102,
                Title = "Cancelled Draft 2",
                Description = "Desc 2",
                Priority = 3,
                Status = TaskOverallStatus.Cancelled
            },
            new()
            {
                Id = 103,
                Title = "Promoted Draft 3",
                Description = "Desc 3",
                Priority = 2,
                Status = TaskOverallStatus.Suggested
            }
        };

        session.SyncProposalsWithWorkspaceTasks(workspaceTasks);

        Assert.Equal(2, session.Proposals.Count);
        Assert.Same(p1, session.Proposals[0]);
        Assert.Equal("Refined Draft 1", p1.Title);
        Assert.Equal("Updated Desc 1", p1.Description);
        Assert.Equal(1, p1.Priority);
        Assert.Equal([50], p1.SelectedDependencyIds);
        Assert.False(p1.IsAdded);

        Assert.Same(p3, session.Proposals[1]);
        Assert.True(p3.IsAdded);
        Assert.DoesNotContain(102, p3.SelectedDependencyIds);
    }

    [Fact]
    public void RemoveProposal_UnlinksDismissedTaskIdAndTitleFromRemainingProposals()
    {
        var session = new IdeationSessionState();
        var first = IdeationProposalViewModel.FromProposal(new IdeationProposal("Base Task", "Base", 2, TaskId: 101));
        var second = IdeationProposalViewModel.FromProposal(new IdeationProposal("Dependent Task", "Dep", 3, DependsOnTitles: ["Base Task"], TaskId: 102, DependsOnIds: [101]));
        session.AddProposal(first);
        session.AddProposal(second);

        session.RemoveProposal(first);

        Assert.Single(session.Proposals);
        Assert.Empty(second.SelectedDependencyIds);
        Assert.Empty(second.DependsOnTitles);
    }

    [Fact]
    public void SyncFromTask_ClearingAllDependencies_WithEmptyOrNullDependencies_ClearsSelectedAndTitlesWithoutResurrection()
    {
        var prereq = new ForgeTask { Id = 10, Title = "Prereq Task", Status = TaskOverallStatus.Draft };
        var draftEmptyDeps = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Draft Empty", "Scope", 2, DependsOnTitles: ["Prereq Task", "#10"], TaskId: 20, DependsOnIds: [10]),
            [prereq]);
        var draftNullDeps = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Draft Null", "Scope", 2, DependsOnTitles: ["Prereq Task"], TaskId: 21, DependsOnIds: [10]),
            [prereq]);

        Assert.Equal([10], draftEmptyDeps.SelectedDependencyIds);
        Assert.NotEmpty(draftEmptyDeps.DependsOnTitles);
        Assert.Equal([10], draftNullDeps.SelectedDependencyIds);
        Assert.NotEmpty(draftNullDeps.DependsOnTitles);

        var updatedEmpty = new ForgeTask
        {
            Id = 20,
            Title = "Draft Empty",
            Description = "Scope",
            Priority = 2,
            Status = TaskOverallStatus.Draft,
            Dependencies = new List<TaskDependency>()
        };
        var updatedNull = new ForgeTask
        {
            Id = 21,
            Title = "Draft Null",
            Description = "Scope",
            Priority = 2,
            Status = TaskOverallStatus.Draft,
            Dependencies = null!
        };

        var session = new IdeationSessionState();
        session.Proposals.AddRange([draftEmptyDeps, draftNullDeps]);
        session.SyncProposalsWithWorkspaceTasks([prereq, updatedEmpty, updatedNull]);

        Assert.Empty(draftEmptyDeps.SelectedDependencyIds);
        Assert.Empty(draftEmptyDeps.DependsOnTitles);
        Assert.True(draftEmptyDeps.IsSyncedFromPersistedTask);

        Assert.Empty(draftNullDeps.SelectedDependencyIds);
        Assert.Empty(draftNullDeps.DependsOnTitles);
        Assert.True(draftNullDeps.IsSyncedFromPersistedTask);

        // Even if DependsOnTitles were inspected or ResolveDependencies called again, old deps are not resurrected
        draftEmptyDeps.ResolveDependencies([prereq, updatedEmpty, updatedNull]);
        draftNullDeps.ResolveDependencies([prereq, updatedEmpty, updatedNull]);
        Assert.Empty(draftEmptyDeps.SelectedDependencyIds);
        Assert.Empty(draftNullDeps.SelectedDependencyIds);
    }

    [Fact]
    public void SyncProposalsWithWorkspaceTasks_ReversingDependencyBetweenTwoDrafts_DoesNotRestoreOldDependency()
    {
        var session = new IdeationSessionState();
        var taskAVm = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Task A", "First", 2, TaskId: 101));
        var taskBVm = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Task B", "Second", 3, DependsOnTitles: ["Task A"], TaskId: 102, DependsOnIds: [101]));
        session.Proposals.AddRange([taskAVm, taskBVm]);

        Assert.Empty(taskAVm.SelectedDependencyIds);
        Assert.Equal([101], taskBVm.SelectedDependencyIds);
        Assert.Contains("Task A", taskBVm.DependsOnTitles);

        // Agent reverses dependency direction in DB: Task A now depends on Task B; Task B has no dependencies
        var persistedTaskA = new ForgeTask
        {
            Id = 101,
            Title = "Task A",
            Description = "First",
            Priority = 2,
            Status = TaskOverallStatus.Draft,
            Dependencies = [new TaskDependency { TaskId = 101, DependsOnTaskId = 102 }]
        };
        var persistedTaskB = new ForgeTask
        {
            Id = 102,
            Title = "Task B",
            Description = "Second",
            Priority = 3,
            Status = TaskOverallStatus.Draft,
            Dependencies = new List<TaskDependency>()
        };

        session.SyncProposalsWithWorkspaceTasks([persistedTaskA, persistedTaskB]);

        Assert.Equal([102], taskAVm.SelectedDependencyIds);
        Assert.Empty(taskAVm.DependsOnTitles);
        Assert.Empty(taskBVm.SelectedDependencyIds);
        Assert.Empty(taskBVm.DependsOnTitles);

        // Re-running ResolveDependencies must not re-add Task A to Task B
        taskBVm.ResolveDependencies([persistedTaskA, persistedTaskB]);
        Assert.Empty(taskBVm.SelectedDependencyIds);
    }

    [Fact]
    public void SyncFromTask_And_FromProposal_RemoveDeletedAttachmentsFromPersistedTask()
    {
        var initialProposal = new IdeationProposal(
            Title: "Attachment Draft",
            Description: "Read @attachment:10 and @attachment:20",
            Priority: 2,
            TaskId: 201,
            AttachmentIds: [10, 20, 30]);

        var vm = IdeationProposalViewModel.FromProposal(initialProposal);
        Assert.Equal([10, 20, 30], vm.SelectedAttachmentIds);

        // Agent updates draft task in DB: removes attachment 20 and 30, keeps link 10 and adds citation 40 in description
        var persistedTask = new ForgeTask
        {
            Id = 201,
            Title = "Attachment Draft",
            Description = "Read @attachment:40 only",
            Priority = 2,
            Status = TaskOverallStatus.Draft,
            AttachmentLinks =
            [
                new TaskAttachmentLink { TaskId = 201, TaskAttachmentId = 10 }
            ]
        };

        vm.SyncFromTask(persistedTask);

        Assert.Equal([10, 40], vm.SelectedAttachmentIds);

        // Rehydrating historical turn metadata containing stale AttachmentIds [10, 20, 30] must still reflect persistedTask
        var rehydrated = IdeationProposalViewModel.FromProposal(initialProposal, [persistedTask]);
        Assert.Equal([10, 40], rehydrated.SelectedAttachmentIds);
    }

    [Fact]
    public void SyncProposalsWithWorkspaceTasks_UpdatesFieldsInPlace_AndFiresOnChangeWithoutRemovals()
    {
        var session = new IdeationSessionState();
        var vm = IdeationProposalViewModel.FromProposal(new IdeationProposal("Original Title", "Original Desc", 3, TaskId: 301));
        session.Proposals.Add(vm);

        var notifyCount = 0;
        session.OnChange += () => notifyCount++;

        var updatedTask = new ForgeTask
        {
            Id = 301,
            Title = "Updated Title",
            Description = "Updated Desc",
            Priority = 1,
            Difficulty = TaskDifficulty.Difficult,
            MergeMode = MergeMode.Automatic,
            Status = TaskOverallStatus.Draft
        };

        session.SyncProposalsWithWorkspaceTasks([updatedTask]);

        Assert.Equal(1, notifyCount);
        Assert.Single(session.Proposals);
        Assert.Equal("Updated Title", vm.Title);
        Assert.Equal("Updated Desc", vm.Description);
        Assert.Equal(1, vm.Priority);
        Assert.Equal(TaskDifficulty.Difficult, vm.Difficulty);
        Assert.Equal(MergeMode.Automatic, vm.MergeMode);
        Assert.False(vm.IsAdded);
    }

    [Theory]
    [InlineData(TaskOverallStatus.Cancelled)]
    [InlineData(TaskOverallStatus.Rejected)]
    public void ReconcileProposedTasks_DoesNotResurrectCancelledOrRejectedTasks_AndSyncsRemaining(TaskOverallStatus terminalStatus)
    {
        var session = new IdeationSessionState();
        var activeProposal = new IdeationProposal("Active Draft", "Initial", 3, TaskId: 401);
        var cancelledProposal = new IdeationProposal("Cancelled Draft", "Removed", 2, TaskId: 402);

        session.TryAddProposal(activeProposal, null);
        session.TryAddProposal(cancelledProposal, null);

        var workspaceTasks = new List<ForgeTask>
        {
            new()
            {
                Id = 401,
                Title = "Active Draft Refined",
                Description = "Updated scope",
                Priority = 1,
                Status = TaskOverallStatus.Draft
            },
            new()
            {
                Id = 402,
                Title = "Cancelled Draft",
                Description = "Removed",
                Priority = 2,
                Status = terminalStatus
            }
        };

        session.ReconcileProposedTasks([activeProposal, cancelledProposal], workspaceTasks);

        var remaining = Assert.Single(session.Proposals);
        Assert.Equal(401, remaining.CreatedTaskId);
        Assert.Equal("Active Draft Refined", remaining.Title);
        Assert.Equal("Updated scope", remaining.Description);
        Assert.Equal(1, remaining.Priority);
    }

    [Theory]
    [InlineData(CanonicalToolNames.EditTask, true)]
    [InlineData(CanonicalToolNames.SetTaskPriority, true)]
    [InlineData(CanonicalToolNames.ReworkPriorities, true)]
    [InlineData(CanonicalToolNames.RemoveTask, true)]
    [InlineData(CanonicalToolNames.GetTask, true)]
    [InlineData(CanonicalToolNames.ProposeTasks, true)]
    [InlineData("backlog.edit_task", true)]
    [InlineData("mcp:propose_tasks", true)]
    [InlineData("create_tasks", true)]
    [InlineData("read", false)]
    [InlineData("bash", false)]
    [InlineData("ask", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void RequiresWorkspaceTaskSync_MatchesBacklogAndProposalToolsOnly(string? toolName, bool expected)
    {
        Assert.Equal(expected, IdeationPresentationHelper.RequiresWorkspaceTaskSync(toolName));
    }
}

