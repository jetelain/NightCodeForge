using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class BacklogToolWiringTests
{
    private const int WorkspaceId = 7;

    private static (CanonicalTools Tools, Mock<IBacklogMutationToolService> Service) CreateTools(bool attachService = true)
    {
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            isReadOnly: true,
            workspaceId: WorkspaceId);
        var service = new Mock<IBacklogMutationToolService>();
        service.Setup(s => s.GetTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("{}");
        service.Setup(s => s.EditTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<TaskEditRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync("edited");
        service.Setup(s => s.SetTaskPriorityAsync(It.IsAny<BacklogToolScope>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync("prio");
        service.Setup(s => s.ReworkPrioritiesAsync(It.IsAny<BacklogToolScope>(), It.IsAny<IReadOnlyList<TaskPriorityUpdate>>(), It.IsAny<CancellationToken>())).ReturnsAsync("rework");
        service.Setup(s => s.RemoveTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync("removed");
        if (attachService)
        {
            tools.BacklogMutationService = service.Object;
        }

        return (tools, service);
    }

    [Fact]
    public void CreateTools_AdvertisesBacklogTools_OnlyWhenServiceIsAttached()
    {
        var (withService, _) = CreateTools();
        var (withoutService, _) = CreateTools(attachService: false);

        var advertised = CanonicalAIFunctionFactory.CreateTools(withService).Select(t => t.Name).ToList();
        var notAdvertised = CanonicalAIFunctionFactory.CreateTools(withoutService).Select(t => t.Name).ToList();

        foreach (var tool in CanonicalToolNames.BacklogMutationTools)
        {
            Assert.Contains(tool, advertised);
            Assert.DoesNotContain(tool, notAdvertised);
        }
    }

    [Fact]
    public void CreateTools_PermittedToolsFilterBacklogTools()
    {
        var (tools, _) = CreateTools();

        var readOnly = CanonicalAIFunctionFactory.CreateTools(tools, DefaultAgentTools.ReadOnly).Select(t => t.Name).ToList();
        var ideation = CanonicalAIFunctionFactory.CreateTools(tools, DefaultAgentTools.Ideation).Select(t => t.Name).ToList();

        Assert.DoesNotContain(CanonicalToolNames.EditTask, readOnly);
        Assert.Contains(CanonicalToolNames.EditTask, ideation);
        Assert.DoesNotContain(CanonicalToolNames.Edit, ideation);
        Assert.DoesNotContain(CanonicalToolNames.Write, ideation);
    }

    [Fact]
    public async Task Dispatcher_RoutesGetTaskToServiceWithWorkspaceScope()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var result = await dispatcher.ExecuteAsync("get_task", "{\"taskId\": \"#42\"}");

        Assert.Equal("{}", result);
        service.Verify(s => s.GetTaskAsync(It.Is<BacklogToolScope>(sc => sc.WorkspaceId == WorkspaceId), 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_RoutesEditTaskArgumentsIncludingDependencyList()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync(
            "edit_task",
            "{\"task_id\": 5, \"title\": \"T\", \"priority\": 2, \"depends_on_ids\": [1, \"#3\"], \"merge_mode\": \"Automatic\", \"base_branch\": \"dev\"}");

        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r =>
                r.TaskId == 5 && r.Title == "T" && r.Description == null && r.Priority == 2
                && r.DependsOnIds != null && r.DependsOnIds.SequenceEqual(new[] { 1, 3 })
                && r.MergeMode == "Automatic" && r.BaseBranch == "dev"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_RoutesReworkPrioritiesAndRemoveTask()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("rework_priorities", "{\"updates\": [{\"task_id\": 1, \"priority\": 2}, {\"taskId\": 3, \"priority\": 5}]}");
        await dispatcher.ExecuteAsync("remove_task", "{\"task_id\": 9, \"reason\": \"obsolete\"}");
        await dispatcher.ExecuteAsync("set_task_priority", "{\"task_id\": 4, \"priority\": 1}");

        service.Verify(s => s.ReworkPrioritiesAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<IReadOnlyList<TaskPriorityUpdate>>(u => u.Count == 2 && u[0] == new TaskPriorityUpdate(1, 2) && u[1] == new TaskPriorityUpdate(3, 5)),
            It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RemoveTaskAsync(It.IsAny<BacklogToolScope>(), 9, "obsolete", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.SetTaskPriorityAsync(It.IsAny<BacklogToolScope>(), 4, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AiFunction_ReturnsModelVisibleErrorWhenServiceRejectsMutation()
    {
        var (tools, service) = CreateTools();
        service.Setup(s => s.RemoveTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NightCodeForge.Core.Exceptions.TaskCannotBeModifiedException(3, "it is Closed"));
        var function = CanonicalAIFunctionFactory.CreateTools(tools).OfType<AIFunction>().Single(t => t.Name == CanonicalToolNames.RemoveTask);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["task_id"] = 3 });

        Assert.Contains("Task #3 cannot be modified", result?.ToString());
    }

    [Fact]
    public async Task AiFunction_GetTask_WhenTaskNotFound_ReturnsModelVisibleError()
    {
        var (tools, service) = CreateTools();
        service.Setup(s => s.GetTaskAsync(It.IsAny<BacklogToolScope>(), 999, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NightCodeForge.Core.Exceptions.TaskNotFoundException(999));
        var function = CanonicalAIFunctionFactory.CreateTools(tools).OfType<AIFunction>().Single(t => t.Name == CanonicalToolNames.GetTask);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["task_id"] = 999 });

        Assert.Equal("Error: Task with ID #999 was not found.", result?.ToString());
    }

    [Fact]
    public async Task AiFunction_EditTask_WhenCircularDependency_ReturnsModelVisibleError()
    {
        var (tools, service) = CreateTools();
        service.Setup(s => s.EditTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<TaskEditRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NightCodeForge.Core.Dependencies.CircularDependencyException("Task #5 cannot depend on itself.", [5, 5]));
        var function = CanonicalAIFunctionFactory.CreateTools(tools).OfType<AIFunction>().Single(t => t.Name == CanonicalToolNames.EditTask);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["task_id"] = 5, ["depends_on_ids"] = new[] { 5 } });

        Assert.Equal("Error: Task #5 cannot depend on itself.", result?.ToString());
    }

    [Fact]
    public async Task AiFunction_RemoveTask_WhenActiveDependentsExist_ReturnsModelVisibleError()
    {
        var (tools, service) = CreateTools();
        service.Setup(s => s.RemoveTaskAsync(It.IsAny<BacklogToolScope>(), 10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Task #10 cannot be removed because active task(s) depend on it: #12."));
        var function = CanonicalAIFunctionFactory.CreateTools(tools).OfType<AIFunction>().Single(t => t.Name == CanonicalToolNames.RemoveTask);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["task_id"] = 10 });

        Assert.Equal("Error: Task #10 cannot be removed because active task(s) depend on it: #12.", result?.ToString());
    }

    [Fact]
    public async Task AiFunction_EditTask_WhenServiceThrowsArgumentException_ReturnsModelVisibleError()
    {
        var (tools, service) = CreateTools();
        service.Setup(s => s.EditTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<TaskEditRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Invalid mergeMode 'InvalidMode'.", "request"));
        var function = CanonicalAIFunctionFactory.CreateTools(tools).OfType<AIFunction>().Single(t => t.Name == CanonicalToolNames.EditTask);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["task_id"] = 5, ["merge_mode"] = "InvalidMode" });

        Assert.StartsWith("Error: Invalid mergeMode 'InvalidMode'.", result?.ToString());
    }

    [Fact]
    public async Task Dispatcher_RoutesBacklogAliases_PrerequisitesAndDeps()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("edittask", "{\"taskId\": 7, \"prerequisites\": [2, 3]}");
        await dispatcher.ExecuteAsync("updatetask", "{\"taskId\": 8, \"deps\": [4, 5]}");

        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r => r.TaskId == 7 && r.DependsOnIds != null && r.DependsOnIds.SequenceEqual(new[] { 2, 3 })),
            It.IsAny<CancellationToken>()), Times.Once);

        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r => r.TaskId == 8 && r.DependsOnIds != null && r.DependsOnIds.SequenceEqual(new[] { 4, 5 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CanonicalTools_BacklogToolWithoutService_FailsFast()
    {
        var (tools, _) = CreateTools(attachService: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.GetTaskAsync(1));
    }

    [Fact]
    public async Task AiFunction_EditTask_WithNullJsonElementDependsOnIds_LeavesDependsOnIdsNull()
    {
        var (tools, service) = CreateTools();
        var function = CanonicalAIFunctionFactory.CreateTools(tools).OfType<AIFunction>().Single(t => t.Name == CanonicalToolNames.EditTask);
        using var doc = System.Text.Json.JsonDocument.Parse("{\"task_id\": 5, \"title\": \"Keep deps\", \"depends_on_ids\": null}");
        var args = new AIFunctionArguments();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            args[prop.Name] = prop.Value.Clone();
        }

        var result = await function.InvokeAsync(args);

        Assert.Equal("edited", result?.ToString());
        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r => r.TaskId == 5 && r.Title == "Keep deps" && r.DependsOnIds == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_EditTask_WithEmptyArrayDependsOnIds_PassesEmptyListToClearDependencies()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("edit_task", "{\"task_id\": 5, \"depends_on_ids\": []}");

        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r => r.TaskId == 5 && r.DependsOnIds != null && r.DependsOnIds.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_EditTask_WithInvalidNonNumericDependency_ThrowsArgumentException()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            dispatcher.ExecuteAsync("edit_task", "{\"task_id\": 5, \"depends_on_ids\": [\"not-a-task-id\"]}"));

        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.IsAny<TaskEditRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatcher_EditTask_WithNullJsonElementOnFirstAliasKey_ReadsSecondAliasKey()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("edit_task", "{\"task_id\": 5, \"merge_mode\": null, \"mergeMode\": \"Automatic\"}");

        service.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r => r.TaskId == 5 && r.MergeMode == "Automatic"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_ReworkPriorities_SupportsTopLevelJsonArrayPayload()
    {
        var (tools, service) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("rework_priorities", "[{\"task_id\": 1, \"priority\": 2}, {\"taskId\": 3, \"priority\": 4}]");

        service.Verify(s => s.ReworkPrioritiesAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<IReadOnlyList<TaskPriorityUpdate>>(u => u.Count == 2 && u[0] == new TaskPriorityUpdate(1, 2) && u[1] == new TaskPriorityUpdate(3, 4)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatcher_Ask_SupportsTruncatedTopLevelJsonArrayPayload()
    {
        var (tools, _) = CreateTools();
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var ex = await Assert.ThrowsAsync<HumanInterruptionException>(() =>
            dispatcher.ExecuteAsync("ask", "[{\"prompt\": \"Q1\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Q2\", \"options\": [\"C\", \"D\"]}"));

        Assert.Equal(2, ex.Questions.Count);
        Assert.Equal("Q1", ex.Questions[0].Prompt);
        Assert.Equal("Q2", ex.Questions[1].Prompt);
    }
}
