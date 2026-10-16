using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;

namespace NightCodeForge.Infrastructure.Tests;

public class AppDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_CreatesSchemaSuccessfully()
    {
        using var context = new AppDbContext(_options);
        await context.EnsureDatabaseCreatedAndWalAsync();

        // Verify tables exist by inserting a workspace
        var workspace = new Workspace
        {
            Name = "TestRepo",
            RepositoryPath = "/repos/test",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        Assert.True(workspace.Id > 0);
    }

    [Fact]
    public async Task AgentProfile_SerializesComplexJsonColumns()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var profile = new AgentProfile
        {
            Id = "test-agent",
            Name = "Test Agent",
            HarnessId = HarnessType.Crucible,
            ModelName = "qwen2.5-coder:32b",
            ThinkingEffort = ThinkingEffort.Medium,
            OptimizationFlags = new LocalOptimizationFlags
            {
                LoopCircuitBreaker = true,
                ThinkingTraceCompaction = false
            },
            PermittedTools = new[] { "read", "grep", "propose_tasks" },
            SecurityTier = SandboxSecurityTier.Standard
        };

        context.AgentProfiles.Add(profile);
        await context.SaveChangesAsync();

        // Reload fresh from DB
        using var context2 = new AppDbContext(_options);
        var loaded = await context2.AgentProfiles.FindAsync("test-agent");

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.OptimizationFlags);
        Assert.True(loaded.OptimizationFlags.LoopCircuitBreaker);
        Assert.False(loaded.OptimizationFlags.ThinkingTraceCompaction);

        Assert.NotNull(loaded.PermittedTools);
        Assert.Equal(3, loaded.PermittedTools.Count);
        Assert.Contains("propose_tasks", loaded.PermittedTools);
    }

    [Fact]
    public async Task EfCoreTaskRepository_And_DependencyStore_WorkCorrectly()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace
        {
            Name = "Repo1",
            RepositoryPath = "/path/1",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(context);
        var depStore = new EfCoreTaskDependencyStore(context);

        var task1 = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "First ForgeTask",
            Description = "Base task",
            Status = TaskOverallStatus.Closed
        };
        await repo.CreateAsync(task1);

        var task2 = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Dependent ForgeTask",
            Description = "Depends on First ForgeTask",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task2, dependsOn: new[] { task1.Id });

        var prereqs = await depStore.GetPrerequisiteTaskIdsAsync(task2.Id);
        Assert.Single(prereqs);
        Assert.Equal(task1.Id, prereqs[0]);

        var dependents = await depStore.GetDependentTaskIdsAsync(task1.Id);
        Assert.Single(dependents);
        Assert.Equal(task2.Id, dependents[0]);

        var status1 = await depStore.GetTaskStatusAsync(task1.Id);
        Assert.Equal(TaskOverallStatus.Closed, status1);
    }

    [Fact]
    public async Task CascadeDelete_RemovesChildEntitiesWhenTaskDeleted()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace { Name = "CascadeWS", RepositoryPath = "/path", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Parent ForgeTask"
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        context.Questions.Add(new Question { TaskId = task.Id, StageId = "s1", Prompt = "Q1?" });
        context.ExecutionLogs.Add(new ExecutionLog { TaskId = task.Id, Source = "Test", Message = "Log 1" });
        context.StageCheckpoints.Add(new StageCheckpoint { TaskId = task.Id, StageId = "s1", SerializedMemory = "{}", GitCommitHash = "abc" });
        await context.SaveChangesAsync();

        // Delete task
        context.Tasks.Remove(task);
        await context.SaveChangesAsync();

        // Verify child records cascaded
        Assert.Empty(await context.Questions.Where(q => q.TaskId == task.Id).ToListAsync());
        Assert.Empty(await context.ExecutionLogs.Where(l => l.TaskId == task.Id).ToListAsync());
        Assert.Empty(await context.StageCheckpoints.Where(c => c.TaskId == task.Id).ToListAsync());
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_SeedsAll5StandardProfiles_WhenEmpty()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        var profiles = await context.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.Equal(5, profiles.Count);

        var ids = profiles.Select(p => p.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "code-review-fix", "ideation", "implementation", "task-review", "triage" }, ids);

        foreach (var p in profiles)
        {
            Assert.Null(p.WorkspaceId);
            Assert.Equal("Qwen3.8-27B:latest", p.ModelName);
        }

        // Verify standard profiles have null OptimizationFlags by default to inherit global optimizations
        var impl = profiles.First(p => p.Id == "implementation");
        Assert.Null(impl.OptimizationFlags);

        // Verify triage has restricted sandbox tier
        var triage = profiles.First(p => p.Id == "triage");
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, triage.SecurityTier);
        Assert.Equal(AgentMode.ReadOnly, triage.Mode);
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_IsIdempotent_AndPreservesCustomizations()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        // User customizes implementation temperature
        var impl = await context.AgentProfiles.FirstAsync(p => p.Id == "implementation");
        impl.Temperature = 0.77;
        impl.ModelName = "custom-coder:32b";
        await context.SaveChangesAsync();

        // Run seeder again (e.g. app restart)
        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        var profiles = await context.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.Equal(5, profiles.Count);

        var updatedImpl = await context.AgentProfiles.FirstAsync(p => p.Id == "implementation");
        Assert.Equal(0.77, updatedImpl.Temperature);
        Assert.Equal("custom-coder:32b", updatedImpl.ModelName);
    }

    [Fact]
    public async Task EfCoreWorkspaceRepository_GetEffectiveAgentProfileAsync_ResolvesDedicatedAndFallsBackToGlobal()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        var repo = new EfCoreWorkspaceRepository(context);
        var ws = await repo.CreateAsync(new Workspace { Name = "RepoTest", RepositoryPath = "/repo", DefaultBaseBranch = "main" });

        // 1. When no dedicated profile exists, falls back to global profile
        var globalImpl = await repo.GetEffectiveAgentProfileAsync(ws.Id, "implementation");
        Assert.NotNull(globalImpl);
        Assert.Null(globalImpl.WorkspaceId);
        Assert.Equal("Qwen3.8-27B:latest", globalImpl.ModelName);

        // 2. Add dedicated workspace override for implementation
        var dedicatedImpl = new AgentProfile
        {
            Id = $"ws-{ws.Id}:implementation",
            WorkspaceId = ws.Id,
            Name = "Dedicated Implementation Agent",
            HarnessId = "crucible",
            ModelName = "dedicated-model:70b",
            StageDirectives = new Dictionary<string, string> { ["implementation"] = "Custom directive" }
        };
        context.AgentProfiles.Add(dedicatedImpl);
        await context.SaveChangesAsync();

        // 3. Now resolves the dedicated workspace profile
        var resolved = await repo.GetEffectiveAgentProfileAsync(ws.Id, "implementation");
        Assert.NotNull(resolved);
        Assert.Equal(ws.Id, resolved.WorkspaceId);
        Assert.Equal("dedicated-model:70b", resolved.ModelName);

        // 4. Other stages still fall back to global
        var triage = await repo.GetEffectiveAgentProfileAsync(ws.Id, "triage");
        Assert.NotNull(triage);
        Assert.Null(triage.WorkspaceId);
        Assert.Equal("Qwen3.8-27B:latest", triage.ModelName);
    }

    [Fact]
    public async Task TaskRepository_UpdateAsync_WithDetachedGraphAndTrackedWorkspace_DoesNotConflict()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace { Name = "TrackingWs", RepositoryPath = "/path/test", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Original Title",
            Status = TaskOverallStatus.Open
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        // Clear change tracker to simulate detached state across requests / scopes
        context.ChangeTracker.Clear();

        var workspaceRepo = new EfCoreWorkspaceRepository(context);
        var taskRepo = new EfCoreTaskRepository(context);

        // 1. Track the workspace in the context
        var trackedWorkspaces = await workspaceRepo.GetAllAsync();
        Assert.Single(trackedWorkspaces);

        // 2. Fetch the task with AsNoTracking (which also includes detached Workspace instance)
        var detachedTasks = await taskRepo.GetByStatusAsync(TaskOverallStatus.Open);
        Assert.Single(detachedTasks);
        var detachedTask = detachedTasks[0];
        Assert.NotNull(detachedTask.Workspace);
        Assert.False(ReferenceEquals(trackedWorkspaces[0], detachedTask.Workspace));

        // 3. Mutate detached task and update
        detachedTask.Title = "Updated Title";
        detachedTask.PipelineDataJson = "{\"status\":\"updated\"}";

        var ex = await Record.ExceptionAsync(() => taskRepo.UpdateAsync(detachedTask));
        Assert.Null(ex);

        // Verify update persisted
        context.ChangeTracker.Clear();
        var reloaded = await taskRepo.GetByIdAsync(detachedTask.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Updated Title", reloaded.Title);
        Assert.Equal("{\"status\":\"updated\"}", reloaded.PipelineDataJson);
    }

    [Fact]
    public async Task WorkspaceRepository_UpdateAsync_WithDetachedWorkspaceAndTrackedInstance_DoesNotConflict()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace { Name = "OriginalWsName", RepositoryPath = "/path/ws", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var workspaceRepo = new EfCoreWorkspaceRepository(context);

        // Track original workspace
        var tracked = await workspaceRepo.GetByIdAsync(ws.Id);
        Assert.NotNull(tracked);

        // Create a detached workspace instance with same Id
        var detached = new Workspace
        {
            Id = ws.Id,
            Name = "RenamedWsName",
            RepositoryPath = "/path/ws-renamed",
            DefaultBaseBranch = "develop"
        };

        var ex = await Record.ExceptionAsync(() => workspaceRepo.UpdateAsync(detached));
        Assert.Null(ex);

        context.ChangeTracker.Clear();
        var reloaded = await workspaceRepo.GetByIdAsync(ws.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("RenamedWsName", reloaded.Name);
        Assert.Equal("develop", reloaded.DefaultBaseBranch);
    }

    [Fact]
    public async Task WorkflowDefinitions_And_StageDefinitions_Persist_WithCascadingDelete()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var workflow = new WorkflowDefinition
        {
            Name = "Custom 2-Stage",
            Code = "custom-2-stage",
            Icon = "⚡",
            Description = "Custom workflow pipeline",
            IsDefault = false
        };

        var stage1 = new StageDefinition
        {
            StageId = "task-review",
            DisplayName = "Task Review",
            Icon = "🔍",
            SequenceOrder = 1,
            Kind = WorkflowStageKind.Agentic,
            CsharpImplementationType = "agentic-review",
            ConfigurationJson = "{}"
        };

        var stage2 = new StageDefinition
        {
            StageId = "deterministic-build",
            DisplayName = "Deterministic Build",
            Icon = "⚙️",
            SequenceOrder = 2,
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = "deterministic-composite",
            ConfigurationJson = "{\"Action\":\"Build\"}"
        };

        workflow.Stages.Add(stage1);
        workflow.Stages.Add(stage2);

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        Assert.True(workflow.Id > 0);
        Assert.True(stage1.Id > 0);
        Assert.True(stage2.Id > 0);

        // Reload fresh context to verify eager/explicit loading
        using var context2 = new AppDbContext(_options);
        var loaded = await context2.WorkflowDefinitions
            .Include(w => w.Stages)
            .FirstOrDefaultAsync(w => w.Code == "custom-2-stage");

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Stages.Count);
        Assert.Contains(loaded.Stages, s => s.StageId == "task-review");
        Assert.Contains(loaded.Stages, s => s.StageId == "deterministic-build");

        // Verify cascading delete of stages when workflow is deleted
        context2.WorkflowDefinitions.Remove(loaded);
        await context2.SaveChangesAsync();

        var remainingStages = await context2.StageDefinitions.Where(s => s.WorkflowId == loaded.Id).ToListAsync();
        Assert.Empty(remainingStages);
    }

    [Fact]
    public async Task Conversation_Persists_WithNullableTaskId_AndIdeationType()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace
        {
            Name = "IdeationWS",
            RepositoryPath = "/repos/ideation",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var ideationConv = new Conversation
        {
            TaskId = null,
            WorkspaceId = ws.Id,
            ConversationType = ConversationType.Ideation,
            Title = "Feature Planning Session",
            StageId = "ideation",
            StageKind = WorkflowStageKind.Agentic,
            Status = StageExecutionStatus.Completed
        };

        var turn = new ConversationTurn
        {
            TurnIndex = 1,
            Role = StageTurnRole.User,
            Content = "Let's plan a task."
        };
        ideationConv.Turns.Add(turn);

        context.Conversations.Add(ideationConv);
        await context.SaveChangesAsync();

        Assert.True(ideationConv.Id > 0);
        Assert.Null(ideationConv.TaskId);
        Assert.Equal(ws.Id, ideationConv.WorkspaceId);

        using var context2 = new AppDbContext(_options);
        var loaded = await context2.Conversations
            .Include(c => c.Turns)
            .FirstOrDefaultAsync(c => c.Id == ideationConv.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded.TaskId);
        Assert.Equal(ConversationType.Ideation, loaded.ConversationType);
        Assert.Equal("Feature Planning Session", loaded.Title);
        Assert.Single(loaded.Turns);
        Assert.Equal("Let's plan a task.", loaded.Turns.First().Content);
    }

    [Fact]
    public async Task EnsureDefaultWorkflowsAsync_SeedsStandard3AgentWorkflowAndStages_WhenEmpty()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var workflow = await context.EnsureDefaultWorkflowsAsync();

        Assert.NotNull(workflow);
        Assert.Equal("Standard 3-Agent Pipeline", workflow.Name);
        Assert.Equal("standard-3-agent", workflow.Code);
        Assert.Equal("⚡", workflow.Icon);
        Assert.True(workflow.IsDefault);
        Assert.Null(workflow.WorkspaceId);
        Assert.Equal(6, workflow.Stages.Count);

        var stages = workflow.Stages.OrderBy(s => s.SequenceOrder).ToList();
        
        Assert.Equal("task-review", stages[0].StageId);
        Assert.Equal("ForgeTask Review", stages[0].DisplayName);
        Assert.Equal("🔍", stages[0].Icon);
        Assert.Equal(WorkflowStageKind.Agentic, stages[0].Kind);
        Assert.Equal("agentic", stages[0].CsharpImplementationType);
        Assert.Contains("\"RequiredMode\": 1", stages[0].ConfigurationJson);
        Assert.Contains("PromptTemplate", stages[0].ConfigurationJson);
        Assert.Contains("StageDirective", stages[0].ConfigurationJson);

        Assert.Equal("deterministic-pre-impl", stages[1].StageId);
        Assert.Equal("Pre-Implementation Baseline", stages[1].DisplayName);
        Assert.Equal("⚙️", stages[1].Icon);
        Assert.Equal(WorkflowStageKind.Deterministic, stages[1].Kind);
        Assert.Equal("deterministic-composite", stages[1].CsharpImplementationType);
        Assert.Contains("\"Action\": \"CompositeBaseline\"", stages[1].ConfigurationJson);

        Assert.Equal("implementation", stages[2].StageId);
        Assert.Equal("Implementation", stages[2].DisplayName);
        Assert.Equal("💻", stages[2].Icon);
        Assert.Equal(WorkflowStageKind.Agentic, stages[2].Kind);
        Assert.Equal("agentic", stages[2].CsharpImplementationType);
        Assert.Contains("\"RequiredMode\": 1", stages[2].ConfigurationJson);
        Assert.Contains("PromptTemplate", stages[2].ConfigurationJson);
        Assert.Contains("StageDirective", stages[2].ConfigurationJson);

        Assert.Equal("deterministic-post-impl", stages[3].StageId);
        Assert.Equal("Post-Implementation Gates", stages[3].DisplayName);
        Assert.Equal("🧪", stages[3].Icon);
        Assert.Equal(WorkflowStageKind.Deterministic, stages[3].Kind);
        Assert.Equal("deterministic-composite", stages[3].CsharpImplementationType);
        Assert.Contains("\"Action\": \"CompositeVerification\"", stages[3].ConfigurationJson);

        Assert.Equal("code-review-fix", stages[4].StageId);
        Assert.Equal("Code Review & Fix", stages[4].DisplayName);
        Assert.Equal("🧐", stages[4].Icon);
        Assert.Equal(WorkflowStageKind.Agentic, stages[4].Kind);
        Assert.Equal("agentic", stages[4].CsharpImplementationType);
        Assert.Contains("\"RequiredMode\": 2", stages[4].ConfigurationJson);
        Assert.Contains("PromptTemplate", stages[4].ConfigurationJson);
        Assert.Contains("StageDirective", stages[4].ConfigurationJson);

        Assert.Equal("deterministic-post-review", stages[5].StageId);
        Assert.Equal("Post-Review Verification", stages[5].DisplayName);
        Assert.Equal("🛡️", stages[5].Icon);
        Assert.Equal(WorkflowStageKind.Deterministic, stages[5].Kind);
        Assert.Equal("deterministic-composite", stages[5].CsharpImplementationType);
        Assert.Contains("\"Action\": \"CompositeVerification\"", stages[5].ConfigurationJson);
    }

    [Fact]
    public async Task EnsureDefaultWorkflowsAsync_IsIdempotent_AndPreservesExistingWorkflow()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var firstResult = await context.EnsureDefaultWorkflowsAsync();
        Assert.NotNull(firstResult);

        // Modify description and save
        firstResult.Description = "Custom description";
        await context.SaveChangesAsync();

        // Call again
        var secondResult = await context.EnsureDefaultWorkflowsAsync();
        Assert.NotNull(secondResult);
        Assert.Equal(firstResult.Id, secondResult.Id);
        Assert.Equal("Custom description", secondResult.Description);

        // Verify total count in database
        var totalWorkflows = await context.WorkflowDefinitions.CountAsync();
        var totalStages = await context.StageDefinitions.CountAsync();
        Assert.Equal(1, totalWorkflows);
        Assert.Equal(6, totalStages);
    }

    [Fact]
    public async Task EfCoreTaskRepository_IdeationConversationLifecycle_AndTurnsPersistence_WorksCorrectly()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace { Name = "Ideation Workspace", RepositoryPath = "/path/ideation", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var taskRepo = new EfCoreTaskRepository(context);

        // 1. Initially no ideation conversation
        var initialActive = await taskRepo.GetActiveIdeationConversationAsync(ws.Id);
        Assert.Null(initialActive);

        // 2. Start ideation conversation
        var conv = await taskRepo.StartIdeationConversationAsync(ws.Id, "Architecture Brainstorming", "deepseek-r1", "ideation");
        Assert.NotNull(conv);
        Assert.True(conv.Id > 0);
        Assert.Null(conv.TaskId);
        Assert.Equal(ws.Id, conv.WorkspaceId);
        Assert.Equal(ConversationType.Ideation, conv.ConversationType);
        Assert.Equal(StageExecutionStatus.Running, conv.Status);
        Assert.Equal("Architecture Brainstorming", conv.Title);

        // 3. Add turns (User, ToolResult, Assistant with thinking trace and proposal JSON)
        var turn0 = await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Can we decompose monolithic services?");
        Assert.Equal(0, turn0.TurnIndex);

        var turn1 = await taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            content: "Directory listing completed",
            toolName: "list_dir",
            toolArguments: "{\"path\":\"src\"}",
            toolResult: "src/Services, src/Shared",
            durationMs: 42,
            isError: false);
        Assert.Equal(1, turn1.TurnIndex);

        var proposalsJson = "[{\"Title\":\"Extract Billing Service\",\"Description\":\"Move billing to microservice\",\"Priority\":1,\"DependsOnTitles\":[]}]";
        var turn2 = await taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Assistant,
            content: "Here is the decomposition plan.",
            thinkingTrace: "Deep analysis of microservices boundaries...",
            metadataJson: proposalsJson);
        Assert.Equal(2, turn2.TurnIndex);

        // 4. Retrieve active ideation conversation
        var active = await taskRepo.GetActiveIdeationConversationAsync(ws.Id);
        Assert.NotNull(active);
        Assert.Equal(conv.Id, active.Id);
        Assert.Equal(3, active.Turns.Count);
        Assert.Equal(0, active.Turns.ElementAt(0).TurnIndex);
        Assert.Equal(1, active.Turns.ElementAt(1).TurnIndex);
        Assert.Equal(2, active.Turns.ElementAt(2).TurnIndex);
        Assert.Equal("Deep analysis of microservices boundaries...", active.Turns.ElementAt(2).ThinkingTrace);
        Assert.Equal(proposalsJson, active.Turns.ElementAt(2).MetadataJson);

        // 5. Complete conversation
        await taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed, summary: "Completed successfully.");

        // 6. Active conversation should now be null
        var completedActive = await taskRepo.GetActiveIdeationConversationAsync(ws.Id);
        Assert.Null(completedActive);

        // 7. GetIdeationConversationsAsync still returns it in history
        var allConversations = await taskRepo.GetIdeationConversationsAsync(ws.Id);
        Assert.Single(allConversations);
        Assert.Equal(StageExecutionStatus.Completed, allConversations[0].Status);
    }

    [Fact]
    public async Task GetByIdAsync_WithSplitQuery_LoadsCompleteTaskGraph()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        // 1. Setup Workspace and Workflow with Stages
        var ws = new Workspace
        {
            Name = "SplitQueryTestRepo",
            RepositoryPath = "/test/repo",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(ws);

        var workflow = new WorkflowDefinition
        {
            Code = "test-split-query-wf",
            Name = "Test Split Query Workflow"
        };
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var stage1 = new StageDefinition
        {
            WorkflowId = workflow.Id,
            StageId = "stage-1",
            DisplayName = "Stage 1",
            SequenceOrder = 1,
            Kind = WorkflowStageKind.Agentic
        };
        var stage2 = new StageDefinition
        {
            WorkflowId = workflow.Id,
            StageId = "stage-2",
            DisplayName = "Stage 2",
            SequenceOrder = 2,
            Kind = WorkflowStageKind.Deterministic
        };
        context.StageDefinitions.AddRange(stage1, stage2);

        // 2. Setup Task
        var task = new ForgeTask
        {
            WorkspaceId = ws.Id,
            WorkflowId = workflow.Id,
            Title = "Task with full graph",
            BaseBranch = "main"
        };
        context.Tasks.Add(task);

        var depTask = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Predecessor Task",
            BaseBranch = "main"
        };
        context.Tasks.Add(depTask);
        await context.SaveChangesAsync();

        // 3. Setup Collections: Dependencies, Questions, Checkpoints, Records, Logs, Conversations
        context.TaskDependencies.Add(new TaskDependency { TaskId = task.Id, DependsOnTaskId = depTask.Id });

        context.Questions.Add(new Question
        {
            TaskId = task.Id,
            StageId = "stage-1",
            Prompt = "Which framework?",
            QuestionType = QuestionType.MultipleChoice,
            Status = QuestionStatus.Pending
        });

        context.StageCheckpoints.Add(new StageCheckpoint
        {
            TaskId = task.Id,
            StageId = "stage-1",
            GitCommitHash = "abcdef123456"
        });

        context.StageExecutionRecords.Add(new StageExecutionRecord
        {
            TaskId = task.Id,
            StageId = "stage-1",
            StageKind = "Agentic",
            AgentProfileId = "architect",
            HarnessId = "crucible",
            ModelName = "gpt-4o",
            Status = StageExecutionStatus.Completed
        });

        context.ExecutionLogs.Add(new ExecutionLog
        {
            TaskId = task.Id,
            StageId = "stage-1",
            Level = Microsoft.Extensions.Logging.LogLevel.Information,
            Source = "TestRunner",
            Message = "Initialized test run"
        });

        context.Conversations.Add(new Conversation
        {
            TaskId = task.Id,
            WorkspaceId = ws.Id,
            StageId = "stage-1",
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed
        });

        await context.SaveChangesAsync();

        // 4. Query using EfCoreTaskRepository
        var repo = new EfCoreTaskRepository(context);
        var loaded = await repo.GetByIdAsync(task.Id);

        // 5. Verify entire graph is fully and correctly hydrated
        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded.Id);
        Assert.NotNull(loaded.Workspace);
        Assert.Equal(ws.Id, loaded.Workspace.Id);
        Assert.NotNull(loaded.Workflow);
        Assert.Equal(workflow.Id, loaded.Workflow.Id);
        Assert.Equal(2, loaded.Workflow.Stages.Count);
        Assert.Single(loaded.Dependencies);
        Assert.Equal(depTask.Id, loaded.Dependencies.First().DependsOnTaskId);
        Assert.Single(loaded.Questions);
        Assert.Equal("Which framework?", loaded.Questions.First().Prompt);
        Assert.Single(loaded.Checkpoints);
        Assert.Equal("abcdef123456", loaded.Checkpoints.First().GitCommitHash);
        Assert.Single(loaded.ExecutionRecords);
        Assert.Equal("architect", loaded.ExecutionRecords.First().AgentProfileId);
        Assert.Single(loaded.ExecutionLogs);
        Assert.Equal("Initialized test run", loaded.ExecutionLogs.First().Message);
        Assert.Single(loaded.Conversations);
        Assert.Equal(StageExecutionStatus.Completed, loaded.Conversations.First().Status);
    }
}
