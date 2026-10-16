using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorDynamicWorkflowTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly InMemoryWorkflowRepository _workflowRepo;
    private readonly WorkflowStageRegistry _registry;
    private readonly WorkflowStageFactory _stageFactory;
    private readonly DatabaseWorkflowPipelineProvider _pipelineProvider;
    private readonly Workspace _workspace;

    public TaskOrchestratorDynamicWorkflowTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeTaskJobQueue();

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Dynamic Pipeline Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();

        var sp = new TestServiceProvider();
        sp.AddService<IPromptTemplateRenderer>(new ScribanPromptTemplateRenderer());

        _registry = new WorkflowStageRegistry();
        _stageFactory = new WorkflowStageFactory(_registry, sp);
        _workflowRepo = new InMemoryWorkflowRepository();
        _pipelineProvider = new DatabaseWorkflowPipelineProvider(_workflowRepo, _stageFactory);
    }

    private class TestServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public void AddService<T>(T service) where T : notnull => _services[typeof(T)] = service;
        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var s) ? s : null;
    }

    [Fact]
    public async Task VerificationCriteria1_Custom3StageWorkflow_ExecutesOnlyConfiguredStagesAndCompletesToReady()
    {
        // 1. Define custom 3-stage workflow: spec -> dev -> audit
        var customWorkflow = new WorkflowDefinition
        {
            Id = 101,
            Name = "Custom 3-Stage Fast Pipeline",
            Code = "custom-3-stage",
            IsDefault = false,
            Stages = new List<StageDefinition>
            {
                new()
                {
                    StageId = "spec",
                    DisplayName = "Requirements Specification",
                    SequenceOrder = 1,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 1}"""
                },
                new()
                {
                    StageId = "dev",
                    DisplayName = "Development & Unit Test",
                    SequenceOrder = 2,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 1}"""
                },
                new()
                {
                    StageId = "audit",
                    DisplayName = "Final Code Audit",
                    SequenceOrder = 3,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 2, "LoopBackStageId": "dev"}"""
                }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(customWorkflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = _workspace.Id,
            WorkflowId = 101,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(10, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, _pipelineProvider, _jobQueue, _depStore);

        // Step 1: Execute stage 1 (spec)
        await orchestrator.ProcessTaskAsync(task.Id);
        var state1 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(state1);
        Assert.Equal("dev", state1.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.InferenceQueue);
        _jobQueue.InferenceQueue.Clear();

        // Step 2: Execute stage 2 (dev)
        await orchestrator.ProcessTaskAsync(task.Id);
        var state2 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(state2);
        Assert.Equal("audit", state2.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.InferenceQueue);
        _jobQueue.InferenceQueue.Clear();

        // Step 3: Execute stage 3 (audit) - this is the final stage
        await orchestrator.ProcessTaskAsync(task.Id);
        var state3 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(state3);
        Assert.Equal(TaskOverallStatus.Ready, state3.Status);
        Assert.Empty(_jobQueue.InferenceQueue); // No 4th stage to enqueue!
    }

    [Fact]
    public async Task VerificationCriteria2_Standard6StageWorkflow_ExecutesFullPipelineSequentially()
    {
        var standardWorkflow = new WorkflowDefinition
        {
            Id = 1,
            Name = "Standard 6-Stage Autonomous Pipeline",
            Code = "standard-3-agent",
            IsDefault = true,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "task-review", DisplayName = "ForgeTask Review", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { StageId = "deterministic-pre-impl", DisplayName = "Pre-Impl Baseline", SequenceOrder = 2, CsharpImplementationType = "agentic" },
                new() { StageId = "implementation", DisplayName = "Implementation", SequenceOrder = 3, CsharpImplementationType = "agentic" },
                new() { StageId = "deterministic-post-impl", DisplayName = "Post-Impl Gates", SequenceOrder = 4, CsharpImplementationType = "agentic" },
                new() { StageId = "code-review-fix", DisplayName = "Code Review & Fix", SequenceOrder = 5, CsharpImplementationType = "agentic" },
                new() { StageId = "deterministic-post-review", DisplayName = "Post-Review Verification", SequenceOrder = 6, CsharpImplementationType = "agentic" }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(standardWorkflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = _workspace.Id,
            WorkflowId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(20, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, _pipelineProvider, _jobQueue, _depStore);

        var expectedSequence = new[]
        {
            "deterministic-pre-impl",
            "implementation",
            "deterministic-post-impl",
            "code-review-fix",
            "deterministic-post-review"
        };

        foreach (var expectedNextStage in expectedSequence)
        {
            await orchestrator.ProcessTaskAsync(task.Id);
            var updated = await _taskRepo.GetByIdAsync(task.Id);
            Assert.Equal(expectedNextStage, updated!.CurrentStageId);
            Assert.Contains(task.Id, _jobQueue.InferenceQueue);
            _jobQueue.InferenceQueue.Clear();
        }

        // Final stage execution
        await orchestrator.ProcessTaskAsync(task.Id);
        var finalTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(TaskOverallStatus.Ready, finalTask!.Status);
    }

    [Fact]
    public async Task VerificationCriteria3_CodeReviewRejection_LoopsBackToConfiguredLoopBackStageId()
    {
        // Custom workflow where review loops back to "custom-coding" instead of "implementation"
        var customWorkflow = new WorkflowDefinition
        {
            Id = 202,
            Name = "Custom Review Loopback Pipeline",
            Code = "custom-loopback",
            IsDefault = false,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "custom-planning", DisplayName = "Planning", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { StageId = "custom-coding", DisplayName = "Coding", SequenceOrder = 2, CsharpImplementationType = "agentic" },
                new()
                {
                    StageId = "custom-review",
                    DisplayName = "Custom Review",
                    SequenceOrder = 3,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 2, "LoopBackStageId": "custom-coding"}"""
                }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(customWorkflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = _workspace.Id,
            WorkflowId = 202,
            Status = TaskOverallStatus.Ready,
            CurrentStageId = "custom-review"
        });
        _depStore.AddTask(30, _workspace.Id, TaskOverallStatus.Ready);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, _pipelineProvider, _jobQueue, _depStore);

        // Maintainer rejects code review
        await orchestrator.RequestChangesAsync(task.Id, "Unit tests are missing for edge cases.");

        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Reviewed, reloaded.Status);
        // Verified: looped back to dynamically configured "custom-coding", not "implementation"!
        Assert.Equal("custom-coding", reloaded.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.UrgentQueue);

        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Equal("Unit tests are missing for edge cases.", bag.Get<string>("ReviewerFeedback"));
    }

    [Fact]
    public async Task QualityGateFailure_LoopsBackToConfiguredLoopBackStageId()
    {
        // Custom stage with configured loopback target
        _registry.RegisterHandler("failing-gate", (sp, def) =>
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<DeterministicStageConfig>(def.ConfigurationJson) ?? new DeterministicStageConfig();
            return new FailingQualityGateStage(def.StageId, def.DisplayName, config.LoopBackStageId);
        });

        var workflow = new WorkflowDefinition
        {
            Id = 303,
            Name = "Gate Loopback Workflow",
            Code = "gate-loopback",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "dev-step", DisplayName = "Dev", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new()
                {
                    StageId = "strict-gate",
                    DisplayName = "Strict Gate",
                    SequenceOrder = 2,
                    CsharpImplementationType = "failing-gate",
                    ConfigurationJson = """{"LoopBackStageId": "dev-step"}"""
                }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(workflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 40,
            WorkspaceId = _workspace.Id,
            WorkflowId = 303,
            CurrentStageId = "strict-gate",
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(40, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, _pipelineProvider, _jobQueue, _depStore);

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        // Loops back to dev-step with urgent enqueue
        Assert.Equal("dev-step", updated.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task UnsetWorkflowId_ResolvesDefaultWorkflowAndPersistsWorkflowId()
    {
        var defaultWorkflow = new WorkflowDefinition
        {
            Id = 77,
            Name = "Global Standard Workflow",
            Code = "standard-3-agent",
            IsDefault = true,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "init-stage", DisplayName = "Init", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(defaultWorkflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 50,
            WorkspaceId = _workspace.Id,
            WorkflowId = null, // Unset
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(50, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, _pipelineProvider, _jobQueue, _depStore);

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(77, updated.WorkflowId);
    }

    private class FailingQualityGateStage : IWorkflowStage
    {
        public string StageId { get; }
        public string DisplayName { get; }
        public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
        public string? LoopBackStageId { get; }

        public FailingQualityGateStage(string stageId, string displayName, string loopBackStageId)
        {
            StageId = stageId;
            DisplayName = displayName;
            LoopBackStageId = loopBackStageId;
        }

        public Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StageExecutionResult.Failure(
                "Gate checks failed with 3 errors.",
                loopBackStageId: LoopBackStageId));
        }
    }
}
