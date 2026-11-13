using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public sealed class BacklogManagementToolModuleTests
{
    [Fact]
    public void Constructors_ValidateArguments_AndSupportVirtualAndHostWorkspaceRoots()
    {
        Assert.Throws<ArgumentNullException>(() => new BacklogManagementToolModule(null!));
        Assert.Throws<ArgumentException>(() => new BacklogManagementToolModule(new CanonicalToolScope("   ")));

        var validScope = new CanonicalToolScope("/workspace", WorkspaceId: 7);
        Assert.Throws<ArgumentNullException>(() => new BacklogManagementToolModule(validScope, null!, attachBacklogMutationService: true));

        var virtualModule = new BacklogManagementToolModule(validScope);
        Assert.False(virtualModule.IsBacklogMutationAvailable);
    }

    [Fact]
    public async Task CreateFunctions_WhenSchemaOnly_ThrowsMissingModuleInstanceMessageForAllSixTools()
    {
        var functions = BacklogManagementToolModule.CreateFunctions(null);
        Assert.Equal(6, functions.Count);

        foreach (var fn in functions)
        {
            var args = new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["task_id"] = 1,
                ["priority"] = 2,
                ["updates"] = new List<TaskPriorityUpdate> { new(1, 2) }
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fn.InvokeAsync(args).AsTask());
            Assert.Equal(BacklogManagementToolModule.MissingModuleInstanceMessage, ex.Message);
        }
    }

    [Fact]
    public async Task BacklogMutationOperations_ThrowWhenServiceNotAttachedOrWorkspaceIdMissing()
    {
        var unattached = new BacklogManagementToolModule(new CanonicalToolScope("/workspace", WorkspaceId: 1));
        var unattachedEx = await Assert.ThrowsAsync<InvalidOperationException>(() => unattached.GetTaskAsync(10));
        Assert.Contains("no backlog mutation service is attached", unattachedEx.Message);

        var mockService = new Mock<IBacklogMutationToolService>(MockBehavior.Strict);
        var noWorkspaceId = new BacklogManagementToolModule(new CanonicalToolScope("/workspace", WorkspaceId: null))
        {
            BacklogMutationService = mockService.Object
        };
        Assert.True(noWorkspaceId.IsBacklogMutationAvailable);

        var scopeEx = await Assert.ThrowsAsync<InvalidOperationException>(() => noWorkspaceId.GetTaskAsync(10));
        Assert.Contains("requires a workspace-scoped session", scopeEx.Message);
    }

    [Fact]
    public async Task BacklogMutationOperations_DelegateToService_ThroughCreateFunctions_AndFormatDomainExceptions()
    {
        var mockService = new Mock<IBacklogMutationToolService>(MockBehavior.Strict);
        mockService
            .Setup(s => s.GetTaskAsync(new BacklogToolScope(5, "/workspace"), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Task #10 details");
        mockService
            .Setup(s => s.EditTaskAsync(
                new BacklogToolScope(5, "/workspace"),
                It.Is<TaskEditRequest>(r => r.TaskId == 10 && r.Title == "New" && r.Priority == 1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Edited #10");
        mockService
            .Setup(s => s.SetTaskPriorityAsync(new BacklogToolScope(5, "/workspace"), 10, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Priority updated");
        mockService
            .Setup(s => s.ReworkPrioritiesAsync(
                new BacklogToolScope(5, "/workspace"),
                It.Is<IReadOnlyList<TaskPriorityUpdate>>(u => u.Count == 1 && u[0].TaskId == 10 && u[0].Priority == 3),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Reworked 1 task");
        mockService
            .Setup(s => s.RemoveTaskAsync(new BacklogToolScope(5, "/workspace"), 10, "Obsolete", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Removed #10");

        var module = new BacklogManagementToolModule(new CanonicalToolScope("/workspace", WorkspaceId: 5))
        {
            BacklogMutationService = mockService.Object
        };
        Assert.Same(mockService.Object, module.BacklogMutationService);

        var fns = module.CreateFunctions().ToDictionary(f => f.Name);

        Assert.Equal("Task #10 details", (await fns[CanonicalToolNames.GetTask].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["task_id"] = 10 })))?.ToString());
        Assert.Equal("Edited #10", (await fns[CanonicalToolNames.EditTask].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["task_id"] = 10, ["title"] = "New", ["priority"] = 1 })))?.ToString());
        Assert.Equal("Priority updated", (await fns[CanonicalToolNames.SetTaskPriority].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["task_id"] = 10, ["priority"] = 2 })))?.ToString());
        Assert.Equal("Reworked 1 task", (await fns[CanonicalToolNames.ReworkPriorities].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["updates"] = new List<TaskPriorityUpdate> { new(10, 3) } })))?.ToString());
        Assert.Equal("Removed #10", (await fns[CanonicalToolNames.RemoveTask].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["task_id"] = 10, ["reason"] = "Obsolete" })))?.ToString());

        // Verify domain exception catching
        mockService
            .Setup(s => s.GetTaskAsync(new BacklogToolScope(5, "/workspace"), 999, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskNotFoundException(999));
        mockService
            .Setup(s => s.RemoveTaskAsync(new BacklogToolScope(5, "/workspace"), 11, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCannotBeModifiedException(11, "Task #11 is already running."));
        mockService
            .Setup(s => s.SetTaskPriorityAsync(new BacklogToolScope(5, "/workspace"), 11, 99, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Priority must be between 1 and 5."));

        Assert.StartsWith("Error:", await module.GetTaskAsync(999));
        Assert.StartsWith("Error:", await module.RemoveTaskAsync(11));
        Assert.Equal("Error: Priority must be between 1 and 5.", await module.SetTaskPriorityAsync(11, 99));
    }

    [Fact]
    public async Task BacklogMutationOperations_WithScopeFactory_ResolvesScopedServiceAndFailsFastWhenUnregistered()
    {
        var scopedService = new Mock<IBacklogMutationToolService>(MockBehavior.Strict);
        scopedService
            .Setup(s => s.GetTaskAsync(new BacklogToolScope(3, "/workspace"), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Scoped Task #42");

        var services = new ServiceCollection();
        services.AddScoped(_ => scopedService.Object);
        using var provider = services.BuildServiceProvider();

        var module = new BacklogManagementToolModule(
            new CanonicalToolScope("/workspace", WorkspaceId: 3),
            provider.GetRequiredService<IServiceScopeFactory>(),
            attachBacklogMutationService: true);

        Assert.True(module.IsBacklogMutationAvailable);
        Assert.Equal("Scoped Task #42", await module.GetTaskAsync(42));

        using var emptyProvider = new ServiceCollection().BuildServiceProvider();
        var brokenModule = new BacklogManagementToolModule(
            new CanonicalToolScope("/workspace", WorkspaceId: 3),
            emptyProvider.GetRequiredService<IServiceScopeFactory>(),
            attachBacklogMutationService: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => brokenModule.GetTaskAsync(42));
    }

    [Fact]
    public async Task ProposeTasks_HandlesAllInputShapes_SequentialAutoChaining_AndDerivedTitles()
    {
        var module = new BacklogManagementToolModule(new CanonicalToolScope("/workspace", WorkspaceId: 1));

        // 1. Empty input
        Assert.Equal("No tasks proposed.", await module.ProposeTasksAsync());

        // 2. Single task with generic "Task" title derives title from long description
        var longFirstLine = "# 1. Implement deterministic token budget calculator for multi-turn agent tool loops and verify limits\nBody details";
        var singleResult = module.ProposeTasks(tasks: null, title: "Task", description: longFirstLine, priority: 2, dependsOnIds: [7]);
        Assert.Single(module.PendingProposals);
        Assert.True(module.PendingProposals[0].Title.Length <= 80);
        Assert.StartsWith("Implement deterministic token budget", module.PendingProposals[0].Title);
        Assert.Equal(2, module.PendingProposals[0].Priority);
        Assert.Equal([7], module.PendingProposals[0].DependsOnIds);
        Assert.Contains(module.PendingProposals[0].Title, singleResult);

        module.PendingProposals.Clear();

        // 3. JSON string with nested wrapper { "tasks": [...] }, string/number dependsOnTitles, and "#12" / title depends_on_ids
        var rawJson = """
        {
          "tasks": [
            {
              "name": "Step A",
              "desc": "First step",
              "priority": 1,
              "type": "chore"
            },
            {
              "title": "Step B",
              "description": "Second step",
              "priority": 3
            },
            {
              "title": "Step C",
              "description": "Third step",
              "dependsOnTitles": ["Step A", 101],
              "depends_on_ids": [15, "#25", "External Prereq"]
            }
          ]
        }
        """;

        var batchResult = await module.ProposeTasksAsync(tasks: rawJson);
        Assert.Contains("Proposed 3 backlog task(s):", batchResult);
        Assert.Equal(3, module.PendingProposals.Count);

        // Step B had no explicit deps, so it auto-chained to Step A
        Assert.Equal(["Step A"], module.PendingProposals[1].DependsOnTitles);

        // Step C parsed explicit dependsOnTitles and depends_on_ids
        Assert.Equal([15, 25], module.PendingProposals[2].DependsOnIds);
        Assert.Contains("Step A", module.PendingProposals[2].DependsOnTitles!);
        Assert.Contains("101", module.PendingProposals[2].DependsOnTitles!);
        Assert.Contains("External Prereq", module.PendingProposals[2].DependsOnTitles!);

        module.PendingProposals.Clear();

        // 4. JsonElement containing a JSON-encoded string array & arbitrary anonymous object
        using var stringElementDoc = JsonDocument.Parse("\"[{\\\"title\\\":\\\"FromEncodedArray\\\",\\\"description\\\":\\\"Desc\\\"}]\"");
        await module.ProposeTasksAsync(tasks: stringElementDoc.RootElement);
        Assert.Single(module.PendingProposals);
        Assert.Equal("FromEncodedArray", module.PendingProposals[0].Title);

        module.PendingProposals.Clear();
        await module.ProposeTasksAsync(tasks: new[] { new { title = "FromAnonObject", description = "Anon desc", priority = 4 } });
        Assert.Single(module.PendingProposals);
        Assert.Equal("FromAnonObject", module.PendingProposals[0].Title);
    }

    [Fact]
    public async Task ProposeTasksAsync_WithAttachedBacklogService_ParsesObjectArrayEmptyAndFallbackResponses()
    {
        var mockService = new Mock<IBacklogMutationToolService>(MockBehavior.Strict);
        var module = new BacklogManagementToolModule(new CanonicalToolScope("/workspace", WorkspaceId: 9))
        {
            BacklogMutationService = mockService.Object
        };

        // Case 1: Service returns single JSON object with id & prerequisites
        mockService
            .Setup(s => s.ProposeTasksAsync(
                new BacklogToolScope(9, "/workspace"),
                It.Is<IReadOnlyList<TaskProposal>>(p => p.Count == 1 && p[0].Title == "Single"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"id": 101, "prerequisites": [50, 51]}""");

        await module.ProposeTasksAsync(title: "Single", description: "Desc");
        Assert.Single(module.PendingProposals);
        Assert.Equal(101, module.PendingProposals[0].TaskId);
        Assert.Equal([50, 51], module.PendingProposals[0].DependsOnIds);

        module.PendingProposals.Clear();

        // Case 2: Service returns JSON array of created tasks
        mockService
            .Setup(s => s.ProposeTasksAsync(
                new BacklogToolScope(9, "/workspace"),
                It.Is<IReadOnlyList<TaskProposal>>(p => p.Count == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("""[{"id": 201, "prerequisites": []}, {"id": 202, "prerequisites": [201]}]""");

        module.ProposeTasks((IReadOnlyList<TaskProposal>)[
            new TaskProposal("First", "D1"),
            new TaskProposal("Second", "D2")
        ]);
        Assert.Equal(2, module.PendingProposals.Count);
        Assert.Equal(201, module.PendingProposals[0].TaskId);
        Assert.Equal(202, module.PendingProposals[1].TaskId);
        Assert.Equal([201], module.PendingProposals[1].DependsOnIds);

        module.PendingProposals.Clear();

        // Case 3: Service returns plain text (non-JSON) or empty string
        mockService
            .Setup(s => s.ProposeTasksAsync(
                new BacklogToolScope(9, "/workspace"),
                It.Is<IReadOnlyList<TaskProposal>>(p => p[0].Title == "PlainText"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Created custom task summary");

        var plainRes = await module.ProposeTasksAsync(title: "PlainText", description: "Desc");
        Assert.Equal("Created custom task summary", plainRes);
        Assert.Single(module.PendingProposals);

        module.PendingProposals.Clear();
        mockService
            .Setup(s => s.ProposeTasksAsync(
                new BacklogToolScope(9, "/workspace"),
                It.Is<IReadOnlyList<TaskProposal>>(p => p[0].Title == "EmptyReturn"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var emptyRes = await module.ProposeTasksAsync(title: "EmptyReturn", description: "Desc");
        Assert.Contains("EmptyReturn", emptyRes);
        Assert.Single(module.PendingProposals);
    }

    [Fact]
    public async Task SettingBacklogMutationServiceToNull_DetachesConfiguredDefaultAndScopeFactoryService()
    {
        var defaultService = new Mock<IBacklogMutationToolService>(MockBehavior.Strict);
        var scopedService = new Mock<IBacklogMutationToolService>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddScoped(_ => scopedService.Object);
        using var provider = services.BuildServiceProvider();

        var module = new BacklogManagementToolModule(new CanonicalToolScope("/workspace", WorkspaceId: 9));
        module.ConfigureBacklogMutation(
            defaultService.Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            attachBacklogMutationService: true);

        Assert.True(module.IsBacklogMutationAvailable);
        Assert.Same(defaultService.Object, module.BacklogMutationService);

        // Explicitly detaching BacklogMutationService via null must disable backlog mutation
        module.BacklogMutationService = null;
        Assert.Null(module.BacklogMutationService);
        Assert.False(module.IsBacklogMutationAvailable);

        await Assert.ThrowsAsync<InvalidOperationException>(() => module.GetTaskAsync(1));

        // ProposeTasksAsync falls back to in-memory PendingProposals without calling detached service
        var proposalJson = await module.ProposeTasksAsync(title: "Detached Proposal", description: "Local only");
        Assert.Contains("Detached Proposal", proposalJson);
        Assert.Single(module.PendingProposals);
    }
}
