using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class WorkflowRepositoryAndPipelineProviderTests
{
    private class TestServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public void AddService<T>(T service) where T : notnull => _services[typeof(T)] = service;
        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var s) ? s : null;
    }

    private readonly TestServiceProvider _serviceProvider;
    private readonly IWorkflowStageRegistry _registry;
    private readonly IWorkflowStageFactory _stageFactory;

    public WorkflowRepositoryAndPipelineProviderTests()
    {
        _serviceProvider = new TestServiceProvider();
        _serviceProvider.AddService<IPromptTemplateRenderer>(new ScribanPromptTemplateRenderer());
        _registry = new WorkflowStageRegistry();
        _stageFactory = new WorkflowStageFactory(_registry, _serviceProvider);
    }

    [Fact]
    public async Task InMemoryWorkflowRepository_CrudAndQuerying_WorksCorrectly()
    {
        var repo = new InMemoryWorkflowRepository();

        var wf1 = new WorkflowDefinition
        {
            Id = 1,
            Name = "Global Default Workflow",
            Code = "global-default",
            IsDefault = true,
            WorkspaceId = null,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "stage-1", DisplayName = "Stage 1", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { StageId = "stage-2", DisplayName = "Stage 2", SequenceOrder = 2, CsharpImplementationType = "agentic" }
            }
        };

        var wf2 = new WorkflowDefinition
        {
            Id = 2,
            Name = "Workspace Specific Workflow",
            Code = "ws-1-custom",
            IsDefault = false,
            WorkspaceId = 42,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "custom-1", DisplayName = "Custom 1", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };

        await repo.CreateWorkflowAsync(wf1);
        await repo.CreateWorkflowAsync(wf2);

        // Get by ID
        var loaded1 = await repo.GetByIdWithStagesAsync(1);
        Assert.NotNull(loaded1);
        Assert.Equal("Global Default Workflow", loaded1.Name);
        Assert.Equal(2, loaded1.Stages.Count);

        // Get Default
        var defaultWf = await repo.GetDefaultWorkflowWithStagesAsync();
        Assert.NotNull(defaultWf);
        Assert.Equal(1, defaultWf.Id);

        // Get Workflows for workspace 42 (should return global + ws 42)
        var ws42Workflows = await repo.GetWorkflowsAsync(42);
        Assert.Equal(2, ws42Workflows.Count);

        // Get Workflows for workspace 99 (should return only global)
        var ws99Workflows = await repo.GetWorkflowsAsync(99);
        Assert.Single(ws99Workflows);
        Assert.Equal(1, ws99Workflows[0].Id);

        // Update
        loaded1.Name = "Updated Global Workflow";
        await repo.UpdateWorkflowAsync(loaded1);
        var reloaded = await repo.GetByIdWithStagesAsync(1);
        Assert.Equal("Updated Global Workflow", reloaded!.Name);

        // Delete
        await repo.DeleteWorkflowAsync(2);
        var deleted = await repo.GetByIdWithStagesAsync(2);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task DatabaseWorkflowPipelineProvider_ConstructsOrderedPipelineFromDatabase()
    {
        var customWorkflow = new WorkflowDefinition
        {
            Id = 10,
            Name = "Fast 3-Stage Pipeline",
            Code = "fast-3-stage",
            IsDefault = false,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "stage-c", DisplayName = "Stage C", SequenceOrder = 3, CsharpImplementationType = "agentic" },
                new() { StageId = "stage-a", DisplayName = "Stage A", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { StageId = "stage-b", DisplayName = "Stage B", SequenceOrder = 2, CsharpImplementationType = "agentic" }
            }
        };

        var repo = new InMemoryWorkflowRepository(new[] { customWorkflow });
        var provider = new DatabaseWorkflowPipelineProvider(repo, _stageFactory);

        var task = new ForgeTask { Id = 1, WorkflowId = 10 };
        var pipeline = await provider.GetPipelineAsync(task);

        Assert.NotNull(pipeline);
        Assert.Equal(3, pipeline.Stages.Count);
        Assert.Equal("stage-a", pipeline.Stages[0].StageId);
        Assert.Equal("stage-b", pipeline.Stages[1].StageId);
        Assert.Equal("stage-c", pipeline.Stages[2].StageId);

        Assert.Equal("stage-b", pipeline.GetNextStage("stage-a")?.StageId);
        Assert.Equal("stage-c", pipeline.GetNextStage("stage-b")?.StageId);
        Assert.Null(pipeline.GetNextStage("stage-c"));
    }

    [Fact]
    public async Task DatabaseWorkflowPipelineProvider_UnsetWorkflowId_ResolvesDefaultAndSetsTaskWorkflowId()
    {
        var defaultWorkflow = new WorkflowDefinition
        {
            Id = 5,
            Name = "Standard Default Pipeline",
            Code = "standard-3-agent",
            IsDefault = true,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "default-stage-1", DisplayName = "Stage 1", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };

        var repo = new InMemoryWorkflowRepository(new[] { defaultWorkflow });
        var provider = new DatabaseWorkflowPipelineProvider(repo, _stageFactory);

        var task = new ForgeTask { Id = 42, WorkflowId = null };
        var pipeline = await provider.GetPipelineAsync(task);

        Assert.NotNull(pipeline);
        Assert.Single(pipeline.Stages);
        Assert.Equal("default-stage-1", pipeline.Stages[0].StageId);
        Assert.Equal(5, task.WorkflowId);
    }

    [Fact]
    public async Task DatabaseWorkflowPipelineProvider_NonExistentWorkflowId_ThrowsWorkflowNotFoundException()
    {
        var repo = new InMemoryWorkflowRepository(); // empty
        var provider = new DatabaseWorkflowPipelineProvider(repo, _stageFactory);

        var task = new ForgeTask { Id = 1, WorkflowId = 999 };
        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(() => provider.GetPipelineAsync(task));
        Assert.Equal(999, ex.WorkflowId);
    }

    [Fact]
    public async Task DatabaseWorkflowPipelineProvider_NoWorkflowAvailable_ThrowsWorkflowNotFoundException()
    {
        var repo = new InMemoryWorkflowRepository(); // empty
        var provider = new DatabaseWorkflowPipelineProvider(repo, _stageFactory);

        var task = new ForgeTask { Id = 99 };
        await Assert.ThrowsAsync<WorkflowNotFoundException>(() => provider.GetPipelineAsync(task));
    }
}
