using System.Text.Json;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Core.Tests.Fixtures;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class Step917DiffStatAndBatchedClarificationTests
{
    [Fact]
    public void PromptConstruction_ContainsFencedStat_HintCommand_NoRawDiff_RenderedOnce()
    {
        var task = new ForgeTask
        {
            Id = 77,
            Title = "Implement Feature 77",
            Description = "Task Description",
            BaseBranch = "main",
            FeatureBranch = "feature/task-77"
        };
        var workspace = new Workspace { Id = 1, Name = "RepoWS" };
        var pipelineContext = new PipelineContext(task, workspace);

        var diffStat = " src/App.cs | 4 ++--\n 1 file changed, 2 insertions(+), 2 deletions(-)";
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);

        var stage = new GenericAgenticStage(
            StandardStageIds.CodeReviewAndFix,
            "Code Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Reviewing task #{{ task.id }}: {{ task.title }}"
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        // 1. Fenced diff stat
        Assert.Contains("[Git Diff Summary]:", prompt);
        Assert.Contains("```diff\n" + diffStat + "\n```", prompt);

        // 2. Hint command with exact base branch and feature branch
        Assert.Contains("git diff main...feature/task-77 -- <file-path>", prompt);

        // 3. No raw diff
        Assert.DoesNotContain("<review_diff>", prompt);

        // 4. Rendered only once
        int statCount = prompt.Split("[Git Diff Summary]:").Length - 1;
        Assert.Equal(1, statCount);
    }

    [Fact]
    public void PromptConstruction_StandardWorkflowReviewStage_RendersDiffStatAndHintOnce()
    {
        var task = new ForgeTask
        {
            Id = 88,
            Title = "Implement Feature 88",
            Description = "Task Description 88",
            BaseBranch = "develop",
            FeatureBranch = "feature/task-88"
        };
        var workspace = new Workspace { Id = 1, Name = "RepoWS" };
        var pipelineContext = new PipelineContext(task, workspace);

        var diffStat = " src/Service.cs | 10 +++++++++-\n 1 file changed, 9 insertions(+), 1 deletion(-)";
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, diffStat);

        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var reviewStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.CodeReviewAndFix);
        var config = JsonSerializer.Deserialize<AgenticStageConfig>(reviewStageDef.ConfigurationJson)!;

        var stage = new GenericAgenticStage(
            reviewStageDef.StageId,
            reviewStageDef.DisplayName,
            config,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        Assert.Contains("[Git Diff Summary]:", prompt);
        Assert.Contains("```diff\n" + diffStat + "\n```", prompt);
        Assert.Contains("To inspect the diff of a specific file against base branch 'develop', run:", prompt);
        Assert.Contains("git diff develop...feature/task-88 -- <file-path>", prompt);

        // Check not duplicated
        int count = prompt.Split("[Git Diff Summary]:").Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void CleanBreak_ExceptionsAndResults_RejectEmptyCollections_AndPreserveItems()
    {
        // HumanInterruptionException rejects empty
        Assert.Throws<ArgumentException>(() => new HumanInterruptionException(Array.Empty<QuestionItem>()));
        Assert.Throws<ArgumentException>(() => new HumanInterruptionException(Array.Empty<Question>()));

        // StageExecutionResult rejects empty
        Assert.Throws<ArgumentException>(() => StageExecutionResult.WaitingForHuman(Array.Empty<Question>()));

        // Preserves all items
        var questions = new List<Question>
        {
            new() { Prompt = "Q1" },
            new() { Prompt = "Q2" },
            new() { Prompt = "Q3" }
        };

        var stageResult = StageExecutionResult.WaitingForHuman(questions);
        Assert.Equal(3, stageResult.ClarificationQuestions?.Count);
    }

    [Fact]
    public async Task Orchestrator_BatchResumption_ValidatesStrictly()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace { Id = 1, Name = "BatchWS", RepositoryPath = "/repo" };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 300,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            Questions = new List<Question>
            {
                new() { Id = 1, TaskId = 300, Prompt = "Q1", Status = QuestionStatus.Pending, BatchId = "b1" },
                new() { Id = 2, TaskId = 300, Prompt = "Q2", Status = QuestionStatus.Answered, Answer = "Already done", BatchId = "b1" }
            }
        });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        // 1. Unknown Question ID
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await orchestrator.SubmitAnswersAsync(300, new Dictionary<int, string> { [999] = "Answer" });
        });

        // 2. Blank Answer
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await orchestrator.SubmitAnswersAsync(300, new Dictionary<int, string> { [1] = "   " });
        });

        // 3. Already Answered Question
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            await orchestrator.SubmitAnswersAsync(300, new Dictionary<int, string> { [2] = "New Answer" });
        });
    }

    [Fact]
    public async Task Orchestrator_BatchResumption_PartialKeepsWaiting_FullResumesOnce()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace { Id = 1, Name = "BatchWS", RepositoryPath = "/repo" };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            Questions = new List<Question>
            {
                new() { Id = 10, TaskId = 301, Prompt = "Q10", Status = QuestionStatus.Pending, BatchId = "batch-xyz" },
                new() { Id = 11, TaskId = 301, Prompt = "Q11", Status = QuestionStatus.Pending, BatchId = "batch-xyz" }
            }
        });
        depStore.AddTask(301, 1, TaskOverallStatus.Open);
        depStore.SetWaitingForHuman(301, true);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        // Step 1: Submit partial answer (Q10 only)
        await orchestrator.SubmitAnswersAsync(301, new Dictionary<int, string> { [10] = "Answer 10" });

        var midway = await taskRepo.GetByIdAsync(301);
        Assert.NotNull(midway);
        Assert.True(midway.IsWaitingForHuman); // Still waiting!
        Assert.Empty(jobQueue.TaskReviewQueue);   // Not yet enqueued!

        var q10 = await taskRepo.GetQuestionByIdAsync(10);
        Assert.Equal(QuestionStatus.Answered, q10?.Status);

        // Step 2: Submit remaining answer (Q11)
        await orchestrator.SubmitAnswersAsync(301, new Dictionary<int, string> { [11] = "Answer 11" });

        var completed = await taskRepo.GetByIdAsync(301);
        Assert.NotNull(completed);
        Assert.False(completed.IsWaitingForHuman); // Resumed!
        Assert.Single(jobQueue.TaskReviewQueue);      // Enqueued exactly once!
        Assert.Contains(301, jobQueue.TaskReviewQueue);
    }

    [Fact]
    public void PromptBuilder_ReAskInSameStage_InjectsOnlyLatestBatch()
    {
        var task = new ForgeTask
        {
            Id = 500,
            Title = "Task with Re-Ask",
            Description = "Task Desc",
            Questions = new List<Question>
            {
                // Earlier batch 1 (answered)
                new() { Id = 1, TaskId = 500, Prompt = "Batch 1 Question?", Answer = "Batch 1 Answer", Status = QuestionStatus.Answered, BatchId = "b1", AnsweredAt = DateTime.UtcNow.AddMinutes(-10) },
                // Later batch 2 (answered)
                new() { Id = 2, TaskId = 500, Prompt = "Batch 2 Question?", Answer = "Batch 2 Answer", Status = QuestionStatus.Answered, BatchId = "b2", AnsweredAt = DateTime.UtcNow }
            }
        };

        var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 });
        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "Task Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Task: {{ task.title }}\n{{- if context.answered_questions }}\n[Answers]:\n{{ for qa in context.answered_questions }}{{ qa.prompt }} -> {{ qa.answer }}\n{{ end }}{{- end }}"
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        // Only latest batch 2 is injected
        Assert.Contains("Batch 2 Question? -> Batch 2 Answer", prompt);
        Assert.DoesNotContain("Batch 1 Question?", prompt);
    }

    [Fact]
    public void ConversationHistory_CompletePendingAskToolCall_AppendsToolResultJson()
    {
        var rawHistory = """
        [
          {"Role":"user","Contents":[{"Type":"text","Text":"Start"}]},
          {"Role":"assistant","Contents":[{"Type":"call","CallId":"call_ask_123","Name":"ask","Arguments":{"questions":[]}}]}
        ]
        """;

        var completed = ConversationHistorySerializer.CompletePendingAskToolCall(rawHistory, "1. Question: Q1\n   Answer: A1");

        Assert.Contains("\"Role\":\"tool\"", completed);
        Assert.Contains("\"CallId\":\"call_ask_123\"", completed);
        Assert.Contains("Answer: A1", completed);
    }

    [Fact]
    public async Task Orchestrator_BatchResumption_ConcurrentSubmit_EnqueuesOnce()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace { Id = 1, Name = "ConcurrentWS", RepositoryPath = "/repo" };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 400,
            WorkspaceId = 1,
            Title = "Concurrent Submit Task",
            Description = "Task Desc",
            IsWaitingForHuman = true,
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 20, TaskId = 400, Prompt = "Q20", BatchId = "b20", Status = QuestionStatus.Pending },
                new() { Id = 21, TaskId = 400, Prompt = "Q21", BatchId = "b20", Status = QuestionStatus.Pending }
            }
        });

        depStore.AddTask(400, 1, TaskOverallStatus.Open);
        depStore.SetWaitingForHuman(400, true);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        // Submit Q20 first
        await orchestrator.SubmitAnswersAsync(400, new Dictionary<int, string> { [20] = "Answer 20" });

        // Concurrent submission for Q21: both try to submit simultaneously
        var t1 = Task.Run(async () =>
        {
            try { await orchestrator.SubmitAnswersAsync(400, new Dictionary<int, string> { [21] = "Answer 21 A" }); }
            catch (InvalidOperationException) { /* already answered */ }
        });

        var t2 = Task.Run(async () =>
        {
            try { await orchestrator.SubmitAnswersAsync(400, new Dictionary<int, string> { [21] = "Answer 21 B" }); }
            catch (InvalidOperationException) { /* already answered */ }
        });

        await Task.WhenAll(t1, t2);

        var finalTask = await taskRepo.GetByIdAsync(400);
        Assert.NotNull(finalTask);
        Assert.False(finalTask.IsWaitingForHuman);
        Assert.Single(jobQueue.TaskReviewQueue); // Enqueued exactly once!
    }

    [Fact]
    public async Task Orchestrator_BatchResumption_CrossBatchAnswers_ThrowsArgumentException()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace { Id = 1, Name = "CrossBatchWS", RepositoryPath = "/repo" };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 401,
            WorkspaceId = 1,
            Title = "Cross Batch Task",
            Description = "Task Desc",
            IsWaitingForHuman = true,
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 30, TaskId = 401, Prompt = "Q30", BatchId = "batch-1", Status = QuestionStatus.Pending },
                new() { Id = 31, TaskId = 401, Prompt = "Q31", BatchId = "batch-2", Status = QuestionStatus.Pending }
            }
        });
        depStore.AddTask(401, 1, TaskOverallStatus.Open);
        depStore.SetWaitingForHuman(401, true);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            orchestrator.SubmitAnswersAsync(401, new Dictionary<int, string>
            {
                [30] = "Answer 30",
                [31] = "Answer 31"
            }));

        Assert.Contains("span multiple clarification batches", ex.Message);
    }

    [Fact]
    public void PromptConstruction_ImplementationStage_DoesNotDuplicateTaskDescription_WhenApprovedPlanMatchesDescription()
    {
        var task = new ForgeTask
        {
            Id = 99,
            Title = "Implement Feature 99",
            Description = "Specification requirements for feature 99."
        };
        var workspace = new Workspace { Id = 1, Name = "RepoWS" };
        var pipelineContext = new PipelineContext(task, workspace);

        // Simulate identical text in ApprovedPlan as task.Description
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, "Specification requirements for feature 99.");

        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var implStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.Implementation);
        var config = JsonSerializer.Deserialize<AgenticStageConfig>(implStageDef.ConfigurationJson)!;

        var stage = new GenericAgenticStage(
            implStageDef.StageId,
            implStageDef.DisplayName,
            config,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        // Under Step 11.31 prompt deduplication, standard template omits task description,
        // and because ApprovedPlan matches Description, it is also suppressed:
        Assert.DoesNotContain("Specification requirements for feature 99.", prompt);
        Assert.DoesNotContain("[Approved Implementation Plan]:", prompt);
    }

    [Fact]
    public void PromptConstruction_ImplementationStage_IncludesApprovedPlan_WhenDistinctFromTaskDescription()
    {
        var task = new ForgeTask
        {
            Id = 100,
            Title = "Implement Feature 100",
            Description = "Specification requirements for feature 100."
        };
        var workspace = new Workspace { Id = 1, Name = "RepoWS" };
        var pipelineContext = new PipelineContext(task, workspace);

        // Distinct approved plan from previous stage summary
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, "Step 1: Create service\nStep 2: Add unit tests");

        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var implStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.Implementation);
        var config = JsonSerializer.Deserialize<AgenticStageConfig>(implStageDef.ConfigurationJson)!;

        var stage = new GenericAgenticStage(
            implStageDef.StageId,
            implStageDef.DisplayName,
            config,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        // Under Step 11.31 prompt deduplication, standard template omits raw task description,
        // while distinct approved plan is preserved:
        Assert.DoesNotContain("Specification requirements for feature 100.", prompt);
        Assert.Contains("[Approved Implementation Plan]:", prompt);
        Assert.Contains("Step 1: Create service\nStep 2: Add unit tests", prompt);
    }

    [Fact]
    public async Task Orchestrator_TransitionToReviewStage_ComputesDiffStat()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager
        {
            GeneratedDiffStat = "src/ReviewTarget.cs | 4 ++--\n 1 file changed"
        };
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace { Id = 1, Name = "ReviewWS", RepositoryPath = "/repo" };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 600,
            WorkspaceId = 1,
            Title = "Task Transition to Review",
            Description = "Task Desc",
            CurrentStageId = StandardStageIds.PostImplementationGates,
            Status = TaskOverallStatus.Open
        });
        depStore.AddTask(600, 1, TaskOverallStatus.Open);

        // Pipeline where stage 1 routes to CodeReviewAndFix
        var gateStage = new GenericAgenticStage(
            StandardStageIds.PostImplementationGates,
            "Gates",
            new AgenticStageConfig(),
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System)
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success(StandardStageIds.CodeReviewAndFix))
        };
        var reviewStage = new GenericAgenticStage(
            StandardStageIds.CodeReviewAndFix,
            "Review",
            new AgenticStageConfig(),
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { gateStage, reviewStage });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        await orchestrator.ProcessTaskAsync(600);

        var updatedTask = await taskRepo.GetByIdAsync(600);
        Assert.NotNull(updatedTask);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, updatedTask.CurrentStageId);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.Equal("src/ReviewTarget.cs | 4 ++--\n 1 file changed", bag.Get<string>(PipelineDataBagKeys.ReviewDiffStat));
        Assert.Equal("diff --git a/file.cs b/file.cs\n+Added line", bag.Get<string>(PipelineDataBagKeys.ReviewDiff));
    }

    [Fact]
    public void PromptConstruction_WithReviewDiff_RendersDirectDiffBlock()
    {
        var task = new ForgeTask
        {
            Id = 89,
            Title = "Implement Feature 89",
            Description = "Task Description 89",
            BaseBranch = "develop",
            FeatureBranch = "feature/task-89"
        };
        var workspace = new Workspace { Id = 1, Name = "RepoWS" };
        var pipelineContext = new PipelineContext(task, workspace);

        var diffContent = "--- a/Service.cs\n+++ b/Service.cs\n@@ -1,3 +1,4 @@\n+line added";
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiff, diffContent);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, " Service.cs | 1 +\n 1 file changed");

        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var reviewStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.CodeReviewAndFix);
        var config = JsonSerializer.Deserialize<AgenticStageConfig>(reviewStageDef.ConfigurationJson)!;

        var stage = new GenericAgenticStage(
            reviewStageDef.StageId,
            reviewStageDef.DisplayName,
            config,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        Assert.Contains("[Git Diff (develop...feature/task-89)]:", prompt);
        Assert.Contains("```diff\n" + diffContent + "\n```", prompt);
        Assert.DoesNotContain("[Git Diff Summary]:", prompt);
    }

    [Fact]
    public async Task Orchestrator_TransitionToReviewStage_WhenDiffExceedsLimit_OmitsReviewDiff()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager
        {
            GeneratedDiffStat = "src/Massive.cs | 1000 +++\n 1 file changed",
            GeneratedDiff = new string('x', WorkflowDefaults.MaxInlineReviewDiffChars + 50)
        };
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace { Id = 1, Name = "ReviewWS", RepositoryPath = "/repo" };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Task Massive Diff",
            Description = "Task Desc",
            CurrentStageId = StandardStageIds.PostImplementationGates,
            Status = TaskOverallStatus.Open
        });
        depStore.AddTask(601, 1, TaskOverallStatus.Open);

        var gateStage = new GenericAgenticStage(
            StandardStageIds.PostImplementationGates,
            "Gates",
            new AgenticStageConfig(),
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System)
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success(StandardStageIds.CodeReviewAndFix))
        };
        var reviewStage = new GenericAgenticStage(
            StandardStageIds.CodeReviewAndFix,
            "Review",
            new AgenticStageConfig(),
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { gateStage, reviewStage });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        await orchestrator.ProcessTaskAsync(601);

        var updatedTask = await taskRepo.GetByIdAsync(601);
        Assert.NotNull(updatedTask);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, updatedTask.CurrentStageId);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.Equal("src/Massive.cs | 1000 +++\n 1 file changed", bag.Get<string>(PipelineDataBagKeys.ReviewDiffStat));
        Assert.Null(bag.Get<string>(PipelineDataBagKeys.ReviewDiff));
    }
}

