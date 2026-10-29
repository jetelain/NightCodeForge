using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
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

    private TaskOrchestrator CreateOrchestrator() =>
        new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            PipelineProvider = _pipelineProvider,
            JobQueue = _jobQueue,
            DependencyStore = _depStore
        }.Build();

    public TaskOrchestratorDynamicWorkflowTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        var profiles = AgentProfile.CreateStandardGlobalProfiles("test-model").ToList();
        profiles.AddRange(new[]
        {
            new AgentProfile { Id = "spec", Name = "Spec Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Full },
            new AgentProfile { Id = "dev", Name = "Dev Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Full },
            new AgentProfile { Id = "audit", Name = "Audit Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Review },
            new AgentProfile { Id = "init-stage", Name = "Init Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Full },
            new AgentProfile { Id = "deterministic-pre-impl", Name = "Pre-Impl Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Full },
            new AgentProfile { Id = "deterministic-post-impl", Name = "Post-Impl Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Full },
            new AgentProfile { Id = "deterministic-post-review", Name = "Post-Review Agent", HarnessId = StandardHarnessIds.Crucible, ModelName = "test-model", Mode = AgentMode.Full },
        });
        _workspaceRepo.SeedGlobalProfiles(profiles);
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
        sp.AddService<TimeProvider>(TimeProvider.System);
        var fakeEnv = new DynamicFakeExecutionEnvironment();
        sp.AddService<IExecutionEnvironment>(fakeEnv);
        sp.AddService<IExecutionEnvironmentProvider>(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(fakeEnv));
        sp.AddService<ILlmModelPreloadService>(new NightCodeForge.Core.Tests.Fixtures.FakePreloadService());
        sp.AddService<NightCodeForge.Core.Execution.IBackgroundTaskRunner>(new NightCodeForge.Core.Execution.BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForge.Core.Execution.BackgroundTaskRunner>.Instance));

        _registry = new WorkflowStageRegistry();
        _stageFactory = new WorkflowStageFactory(_registry, sp);
        _workflowRepo = new InMemoryWorkflowRepository();
        _pipelineProvider = new DatabaseWorkflowPipelineProvider(_workflowRepo, _stageFactory);
    }

    private class DynamicFakeExecutionEnvironment : IExecutionEnvironment
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CommandResult(0, "Build succeeded: 0 Error(s), 0 Warning(s)", string.Empty, TimeSpan.FromMilliseconds(20)));
        }
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
                    QueueTier = StageQueueTier.TaskReview,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 1}"""
                },
                new()
                {
                    StageId = "dev",
                    DisplayName = "Development & Unit Test",
                    SequenceOrder = 2,
                    QueueTier = StageQueueTier.Implementation,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 1}"""
                },
                new()
                {
                    StageId = "audit",
                    DisplayName = "Final Code Audit",
                    SequenceOrder = 3,
                    QueueTier = StageQueueTier.CodeReview,
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

        var orchestrator = CreateOrchestrator();

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
                new() { StageId = "task-review", DisplayName = "ForgeTask Review", SequenceOrder = 1, QueueTier = StageQueueTier.TaskReview, CsharpImplementationType = "agentic" },
                new() { StageId = "deterministic-pre-impl", DisplayName = "Pre-Impl Baseline", SequenceOrder = 2, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new() { StageId = "implementation", DisplayName = "Implementation", SequenceOrder = 3, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new() { StageId = "deterministic-post-impl", DisplayName = "Post-Impl Gates", SequenceOrder = 4, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new() { StageId = "code-review-fix", DisplayName = "Code Review & Fix", SequenceOrder = 5, QueueTier = StageQueueTier.CodeReview, CsharpImplementationType = "agentic" },
                new() { StageId = "deterministic-post-review", DisplayName = "Post-Review Verification", SequenceOrder = 6, QueueTier = StageQueueTier.CodeReview, CsharpImplementationType = "agentic" }
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

        var orchestrator = CreateOrchestrator();

        // Turn 1: Task Review (Stage 1) -> transitions to Stage 2 and enqueues Implementation cohort
        await orchestrator.ProcessTaskAsync(task.Id);
        var afterTurn1 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.Equal("deterministic-pre-impl", afterTurn1!.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.InferenceQueue);
        _jobQueue.InferenceQueue.Clear();

        // Turn 2: Implementation Cohort (Stages 2, 3, 4 execute in-process) -> transitions to Stage 5 and enqueues Delivery cohort
        await orchestrator.ProcessTaskAsync(task.Id);
        var afterTurn2 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.Equal("code-review-fix", afterTurn2!.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.InferenceQueue);
        _jobQueue.InferenceQueue.Clear();

        // Turn 3: Review & Delivery Cohort (Stages 5, 6 + Gate 2 execute in-process) -> reaches Ready
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
                new() { StageId = "custom-planning", DisplayName = "Planning", SequenceOrder = 1, QueueTier = StageQueueTier.TaskReview, CsharpImplementationType = "agentic" },
                new() { StageId = "custom-coding", DisplayName = "Coding", SequenceOrder = 2, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new()
                {
                    StageId = "custom-review",
                    DisplayName = "Custom Review",
                    SequenceOrder = 3,
                    QueueTier = StageQueueTier.CodeReview,
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

        var orchestrator = CreateOrchestrator();

        // Maintainer rejects code review
        await orchestrator.RequestChangesAsync(task.Id, "Unit tests are missing for edge cases.");

        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        // Verified: looped back to dynamically configured "custom-coding", not "implementation"!
        Assert.Equal("custom-coding", reloaded.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.ImplementationQueue);

        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Equal("Unit tests are missing for edge cases.", bag.Get<string>(PipelineDataBagKeys.ReviewerFeedback));
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
                new() { StageId = "dev-step", DisplayName = "Dev", SequenceOrder = 1, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new()
                {
                    StageId = "strict-gate",
                    DisplayName = "Strict Gate",
                    SequenceOrder = 2,
                    QueueTier = StageQueueTier.CodeReview,
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

        var orchestrator = CreateOrchestrator();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        // Loops back to dev-step with stage tier enqueue
        Assert.Equal("dev-step", updated.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.ImplementationQueue);
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
                new() { StageId = "init-stage", DisplayName = "Init", SequenceOrder = 1, QueueTier = StageQueueTier.TaskReview, CsharpImplementationType = "agentic" }
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

        var orchestrator = CreateOrchestrator();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(77, updated.WorkflowId);
    }

    [Fact]
    public async Task Custom3StageWorkflow_TaskReview_DeterministicBuild_Implementation_ExecutesSequentiallyAndCompletesToReady()
    {
        // Custom 3-stage: task-review -> deterministic-build -> implementation
        var workflow = new WorkflowDefinition
        {
            Id = 404,
            Name = "Review Build Impl Pipeline",
            Code = "review-build-impl",
            Stages = new List<StageDefinition>
            {
                new()
                {
                    StageId = "task-review",
                    DisplayName = "Task Review",
                    SequenceOrder = 1,
                    QueueTier = StageQueueTier.TaskReview,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 1}"""
                },
                new()
                {
                    StageId = "deterministic-build",
                    DisplayName = "Deterministic Build",
                    SequenceOrder = 2,
                    QueueTier = StageQueueTier.Implementation,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-build",
                    ConfigurationJson = """{"Action": "Build", "FailOnError": true}"""
                },
                new()
                {
                    StageId = "implementation",
                    DisplayName = "Implementation",
                    SequenceOrder = 3,
                    QueueTier = StageQueueTier.Implementation,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode": 1}"""
                }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(workflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 60,
            WorkspaceId = _workspace.Id,
            WorkflowId = 404,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(60, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = CreateOrchestrator();

        // Stage 1: task-review completes -> advances to deterministic-build
        await orchestrator.ProcessTaskAsync(task.Id);
        var state1 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(state1);
        Assert.Equal("deterministic-build", state1.CurrentStageId);

        // Stage 2: deterministic-build completes -> advances to implementation
        await orchestrator.ProcessTaskAsync(task.Id);
        var state2 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(state2);
        Assert.Equal("implementation", state2.CurrentStageId);

        // Stage 3: implementation completes -> pipeline finished -> transitions to Ready
        await orchestrator.ProcessTaskAsync(task.Id);
        var state3 = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(state3);
        Assert.Equal(TaskOverallStatus.Ready, state3.Status);
    }

    [Fact]
    public async Task DynamicFailureLoopback_Stage4Failure_RoutesBackToStage3AsConfigured()
    {
        _registry.RegisterHandler("failing-stage-4", (sp, def) =>
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<DeterministicStageConfig>(def.ConfigurationJson) ?? new DeterministicStageConfig();
            return new FailingQualityGateStage(def.StageId, def.DisplayName, config.LoopBackStageId);
        });

        var workflow = new WorkflowDefinition
        {
            Id = 505,
            Name = "4-Stage Loopback Workflow",
            Code = "4stage-loopback",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "stage-1", DisplayName = "Stage 1", SequenceOrder = 1, QueueTier = StageQueueTier.TaskReview, CsharpImplementationType = "agentic" },
                new() { StageId = "stage-2", DisplayName = "Stage 2", SequenceOrder = 2, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new() { StageId = "stage-3", DisplayName = "Stage 3", SequenceOrder = 3, QueueTier = StageQueueTier.Implementation, CsharpImplementationType = "agentic" },
                new()
                {
                    StageId = "stage-4",
                    DisplayName = "Stage 4 Failing Gate",
                    SequenceOrder = 4,
                    QueueTier = StageQueueTier.CodeReview,
                    CsharpImplementationType = "failing-stage-4",
                    ConfigurationJson = """{"LoopBackStageId": "stage-3"}"""
                }
            }
        };
        await _workflowRepo.CreateWorkflowAsync(workflow);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 70,
            WorkspaceId = _workspace.Id,
            WorkflowId = 505,
            CurrentStageId = "stage-4",
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(70, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = CreateOrchestrator();

        // Process stage 4 -> fails and routes back to stage-3 with stage tier enqueue
        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal("stage-3", updated.CurrentStageId);
        Assert.Contains(task.Id, _jobQueue.ImplementationQueue);
    }

    private class FailingQualityGateStage : IWorkflowStage
    {
        public string StageId { get; }
        public string DisplayName { get; }
        public WorkflowStageKind Kind => WorkflowStageKind.Deterministic;
        public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
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
