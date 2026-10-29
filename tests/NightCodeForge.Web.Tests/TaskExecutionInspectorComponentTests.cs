using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskExecutionInspectorComponentTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly FakeDevQueueService _devQueueService;
    private readonly FakeTaskReviewService _taskReviewService;

    public TaskExecutionInspectorComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _taskRepository = new InMemoryTaskRepository();
        _workflowRepository = new InMemoryWorkflowRepository(new[] { TestWorkflowFixtures.CreateStandardWorkflow() });
        _hubConnection = TestHubConnectionHelper.Create();
        _devQueueService = new FakeDevQueueService();
        _taskReviewService = new FakeTaskReviewService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        Services.AddSingleton<IWorkflowRepository>(_workflowRepository);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton<IDevQueueService>(_devQueueService);
        Services.AddSingleton<ITaskReviewService>(_taskReviewService);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(_taskReviewService));
        Services.AddSingleton<ReviewCommentStateService>();
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
    }

    [Fact]
    public void OptimizationMarkerCard_RendersActionTypeAndSavedChars()
    {
        var cut = Render<OptimizationMarkerCard>(p => p
            .Add(x => x.ActionType, "ToolResultCompaction")
            .Add(x => x.TokensOrBytesSaved, 1420)
            .Add(x => x.Description, "Compacted 3 older read_file tool outputs")
            .Add(x => x.Details, "{\"CompactedCount\": 3}"));

        Assert.Contains("ToolResultCompaction", cut.Markup);
        Assert.Contains("Saved ~1,420 chars", cut.Markup);
        Assert.Contains("Compacted 3 older read_file tool outputs", cut.Markup);
    }

    [Fact]
    public void StageRunSelector_RendersGroupedRunsAndSelects()
    {
        var conv1 = new Conversation
        {
            Id = 1,
            TaskId = 10,
            StageId = "task-review",
            StageKind = WorkflowStageKind.Deterministic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        var conv2 = new Conversation
        {
            Id = 2,
            TaskId = 10,
            StageId = "implementation",
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-8)
        };

        var conv3 = new Conversation
        {
            Id = 3,
            TaskId = 10,
            StageId = "implementation",
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 2,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var workflow = TestWorkflowFixtures.CreateStandardWorkflow();
        Conversation? selected = null;
        var cut = Render<StageRunSelector>(p => p
            .Add(x => x.Conversations, new List<Conversation> { conv1, conv2, conv3 })
            .Add(x => x.SelectedConversation, conv1)
            .Add(x => x.WorkflowStages, (IReadOnlyList<StageDefinition>)workflow.Stages!)
            .Add(x => x.OnConversationSelected, conv => selected = conv));

        Assert.Contains("1. ForgeTask Review", cut.Markup);
        Assert.Contains("3. Implementation", cut.Markup);
        Assert.Contains("Run 1", cut.Markup);
        Assert.Contains("Run 2", cut.Markup);

        var buttons = cut.FindAll("button");
        var run2Button = buttons.First(b => b.TextContent.Contains("Run 2"));
        run2Button.Click();

        Assert.NotNull(selected);
        Assert.Equal(3, selected.Id);
        Assert.Equal(2, selected.AttemptIndex);
    }

    [Fact]
    public void StageConversationViewer_RendersUserThinkingToolAndAssistantTurns()
    {
        var conv = new Conversation
        {
            Id = 1,
            TaskId = 20,
            StageId = "implementation",
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            ModelName = "deepseek-r1:32b",
            HarnessId = "Crucible",
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 101,
                    ConversationId = 1,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Please implement JWT authentication."
                },
                new()
                {
                    Id = 102,
                    ConversationId = 1,
                    TurnIndex = 1,
                    Role = StageTurnRole.Thinking,
                    Content = "Analyzing the security middleware..."
                },
                new()
                {
                    Id = 103,
                    ConversationId = 1,
                    TurnIndex = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = "write_file",
                    ToolArguments = "{\"path\": \"JwtAuth.cs\"}",
                    ToolResult = "File written successfully.",
                    DurationMs = 120
                },
                new()
                {
                    Id = 104,
                    ConversationId = 1,
                    TurnIndex = 3,
                    Role = StageTurnRole.Assistant,
                    Content = "JWT authentication has been implemented."
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, false));

        Assert.Contains("deepseek-r1:32b", cut.Markup);
        Assert.Contains("Please implement JWT authentication.", cut.Markup);
        Assert.Contains("Analyzing the security middleware...", cut.Markup);
        Assert.Contains("write_file", cut.Markup);
        Assert.Contains("JWT authentication has been implemented.", cut.Markup);
    }

    [Fact]
    public async Task TaskExecutionInspector_RendersOverviewTabAndSquashSha()
    {
        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Refactor Auth Middleware",
            Description = "Full architectural overhaul of JWT validation.",
            Status = TaskOverallStatus.Closed,
            SquashCommitSha = "e89c3140ab89ef451234567890abcdef12345678",
            FeatureBranch = "feature/task-55",
            BaseBranch = "main",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 1,
                    TaskId = 55,
                    Prompt = "Should we enforce refresh tokens?",
                    Answer = "Yes, with 7-day sliding expiration."
                }
            }
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 55));

        // Header displays squash commit SHA
        Assert.Contains("e89c3140", cut.Markup);

        // Switch to Overview tab
        var overviewBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Task Overview"));
        overviewBtn.Click();

        Assert.Contains("Full architectural overhaul of JWT validation.", cut.Markup);
        Assert.Contains("Should we enforce refresh tokens?", cut.Markup);
        Assert.Contains("Yes, with 7-day sliding expiration.", cut.Markup);
    }

    [Fact]
    public async Task TaskExecutionInspector_RetryAndRestartButtons_TriggerDevQueueService()
    {
        var task = new ForgeTask
        {
            Id = 60,
            WorkspaceId = 1,
            Title = "Bugfix in Token Parser",
            Status = TaskOverallStatus.Failed
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 60));

        var retryBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Retry Stage"));
        retryBtn.Click();

        Assert.Equal(60, _devQueueService.LastRescheduledTaskId);
        Assert.False(_devQueueService.LastRescheduledRestartPipeline);

        var restartBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Restart Pipeline"));
        restartBtn.Click();

        var confirmBtn = cut.Find("#confirm-restart-btn");
        confirmBtn.Click();

        Assert.Equal(60, _devQueueService.LastRescheduledTaskId);
        Assert.True(_devQueueService.LastRescheduledRestartPipeline);
    }

    [Fact]
    public async Task TaskExecutionInspector_ConversationsTab_StageRunSelectorContainerIsSticky()
    {
        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Sticky Test Task",
            Status = TaskOverallStatus.Open
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 88));

        var selector = cut.FindComponent<StageRunSelector>();
        Assert.NotNull(selector);
        Assert.Contains("position: sticky", cut.Markup);
        Assert.Contains("Pipeline Stages &amp; Runs", cut.Markup);
    }

    [Fact]
    public void OptimizationMarkerCard_WithTokensUnit_RendersTokensLabel()
    {
        var cut = Render<OptimizationMarkerCard>(p => p
            .Add(x => x.ActionType, "ThinkingCompaction")
            .Add(x => x.TokensOrBytesSaved, 840)
            .Add(x => x.Unit, "tokens")
            .Add(x => x.Description, "Compacted thinking trace across 4 turns")
            .Add(x => x.Details, "{\"TokensBefore\": 1200, \"TokensAfter\": 360}"));

        Assert.Contains("ThinkingCompaction", cut.Markup);
        Assert.Contains("Saved ~840 tokens", cut.Markup);
        Assert.DoesNotContain("chars", cut.Markup);
    }

    [Fact]
    public void StageRunSelector_FormatsDeterministicAndCodeReviewStageAliases()
    {
        var c1 = new Conversation
        {
            Id = 1,
            TaskId = 10,
            StageId = "deterministic-pre-impl",
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var c2 = new Conversation
        {
            Id = 2,
            TaskId = 10,
            StageId = "deterministic-post-impl",
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var c3 = new Conversation
        {
            Id = 3,
            TaskId = 10,
            StageId = "code-review-fix",
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-2)
        };

        var workflow = TestWorkflowFixtures.CreateStandardWorkflow();
        var cut = Render<StageRunSelector>(p => p
            .Add(x => x.Conversations, new List<Conversation> { c1, c2, c3 })
            .Add(x => x.WorkflowStages, (IReadOnlyList<StageDefinition>)workflow.Stages!));

        Assert.Contains("2. Pre-Implementation Baseline", cut.Markup);
        Assert.Contains("4. Post-Implementation Gates", cut.Markup);
        Assert.Contains("5. Code Review &amp; Fix", cut.Markup);

        var cutFallback = Render<StageRunSelector>(p => p
            .Add(x => x.Conversations, new List<Conversation> { c1, c2, c3 }));

        Assert.Contains("Pre-Implementation Baseline", cutFallback.Markup);
        Assert.Contains("Post-Implementation Gates", cutFallback.Markup);
        Assert.Contains("Code Review &amp; Fix", cutFallback.Markup);

        var cUnknown = new Conversation
        {
            Id = 4,
            TaskId = 10,
            StageId = "custom-security-scan",
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow
        };
        var cutUnknown = Render<StageRunSelector>(p => p
            .Add(x => x.Conversations, new List<Conversation> { cUnknown }));
        Assert.Contains("custom-security-scan", cutUnknown.Markup);
    }

    [Fact]
    public void StageConversationViewer_FormatsStageAliasesAndDurationFallback()
    {
        var conv = new Conversation
        {
            Id = 1,
            TaskId = 20,
            StageId = "code-review-fix",
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 201,
                    ConversationId = 1,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Please fix Sonar warnings."
                },
                new()
                {
                    Id = 202,
                    ConversationId = 1,
                    TurnIndex = 1,
                    Role = StageTurnRole.Optimization,
                    Content = "Compacted thinking token log",
                    MetadataJson = "{\"ActionType\": \"ThinkingCompaction\", \"TokensSaved\": 420}"
                },
                new()
                {
                    Id = 203,
                    ConversationId = 1,
                    TurnIndex = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = "run_tests",
                    DurationMs = 350,
                    ToolResult = "All tests green."
                }
            }
        };

        var workflow = TestWorkflowFixtures.CreateStandardWorkflow();
        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.WorkflowStages, (IReadOnlyList<StageDefinition>)workflow.Stages!)
            .Add(x => x.IsActiveStage, false));

        Assert.Contains("5. Code Review", cut.Markup);
        Assert.Contains("Compacted thinking token log", cut.Markup);
        Assert.Contains("tokens", cut.Markup);
        Assert.Contains("350ms", cut.Markup);
    }

    [Fact]
    public async Task TaskExecutionInspector_WhenTaskClosed_RetryStageButtonIsDisabled()
    {
        var task = new ForgeTask
        {
            Id = 70,
            WorkspaceId = 1,
            Title = "Closed Finished Task",
            Status = TaskOverallStatus.Closed
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 70));

        var retryBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Retry Stage"));
        Assert.True(retryBtn.HasAttribute("disabled"));
    }

    [Fact]
    public async Task TaskExecutionInspector_TerminalConsole_KeepsMountedAcrossTabSwitches()
    {
        var task = new ForgeTask
        {
            Id = 75,
            WorkspaceId = 1,
            Title = "Task with Terminal Logs",
            Status = TaskOverallStatus.Open
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 75));

        // Terminal container exists in DOM even while conversations tab is active
        var terminalContainer = cut.Find("#terminal-console-container");
        Assert.NotNull(terminalContainer);

        // Switch to Terminal tab
        var terminalBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Terminal & Logs"));
        terminalBtn.Click();

        // Still mounted
        terminalContainer = cut.Find("#terminal-console-container");
        Assert.NotNull(terminalContainer);
    }

    [Fact]
    public async Task TaskExecutionInspector_DiffTab_LoadsAndDisplaysChangedFiles()
    {
        var task = new ForgeTask
        {
            Id = 80,
            WorkspaceId = 1,
            Title = "Task with Diffs",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        _taskReviewService.DiffFilesToReturn = new List<FileDiffSummary>
        {
            new("src/ImportantLogic.cs", FileChangeType.Modified, 15, 3, "old line", "new line")
        };

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 80));

        var diffBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Git Diff & Changes"));
        diffBtn.Click();

        cut.WaitForState(() => cut.Markup.Contains("src/ImportantLogic.cs"));
        Assert.Contains("src/ImportantLogic.cs", cut.Markup);
        Assert.Contains("+15", cut.Markup);
        Assert.Contains("-3", cut.Markup);
        Assert.NotNull(cut.Find("#diff-workspace-grid"));
        Assert.NotNull(cut.Find("#file-list"));
        Assert.NotNull(cut.Find("#diff-viewer-panel"));
        Assert.NotNull(cut.Find(".review-dir-node"));
        Assert.NotNull(cut.Find(".review-file-node"));
    }

    [Fact]
    public async Task GitDiffTab_RendersLiveWorktreeBadge()
    {
        var task = new ForgeTask
        {
            Id = 81,
            WorkspaceId = 1,
            Title = "Task with Live Worktree Diff",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        _taskReviewService.DiffFilesToReturn = new List<FileDiffSummary>
        {
            new("src/LiveService.cs", FileChangeType.Modified, 8, 1, "old", "new")
        };

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 81));

        var diffBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Git Diff & Changes"));
        diffBtn.Click();

        cut.WaitForState(() => cut.Markup.Contains("Live Worktree"));
        Assert.Contains("Live Worktree (Includes Uncommitted Changes)", cut.Markup);
        Assert.Contains("+8", cut.Markup);
        Assert.Contains("-1", cut.Markup);
    }

    [Fact]
    public async Task GitDiffTab_WhenDiffCalculationFails_RendersErrorMessage()
    {
        var task = new ForgeTask
        {
            Id = 82,
            WorkspaceId = 1,
            Title = "Task with Failing Diff",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        _taskReviewService.DiffExceptionToThrow = new InvalidOperationException("Git lock collision in worktree");

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 82));

        var diffBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Git Diff & Changes"));
        diffBtn.Click();

        cut.WaitForState(() => cut.Find("#inspector-diff-error") != null);
        Assert.Contains("Git lock collision in worktree", cut.Find("#inspector-diff-error").TextContent);
    }

    [Fact]
    public async Task TaskExecutionInspector_Renders1To7PhasesPipelineStepper_AndStageClickSelectsConversation()
    {
        var task = new ForgeTask
        {
            Id = 85,
            WorkspaceId = 1,
            Title = "Task with Pipeline Stepper Interaction",
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var conv1 = await _taskRepository.StartConversationAsync(85, "task-review", WorkflowStageKind.Deterministic, 1);
        await _taskRepository.CompleteConversationAsync(conv1.Id, StageExecutionStatus.Completed, "Review passed");

        var conv2 = await _taskRepository.StartConversationAsync(85, "implementation", WorkflowStageKind.Agentic, 1);
        await _taskRepository.AddConversationTurnAsync(conv2.Id, StageTurnRole.User, "Implement feature X");
        await _taskRepository.CompleteConversationAsync(conv2.Id, StageExecutionStatus.Completed, "Feature X completed");

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 85));

        // PipelineStepper is rendered
        Assert.NotNull(cut.Find(".pipeline-stepper-container"));

        // Stage 1 (Review) and Stage 3 (Implementation) are completed in stepper
        var step1 = cut.Find("#stage-step-1");
        var step3 = cut.Find("#stage-step-3");
        Assert.Contains("step-completed", step1.ClassName);
        Assert.Contains("step-completed", step3.ClassName);

        // Click on stage 3 step to select its conversation
        step3.Click();

        cut.WaitForState(() => cut.Markup.Contains("Implement feature X"));
        Assert.Contains("Implement feature X", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_RendersUserAndAgentTurnsAsFormattedMarkdown()
    {
        var conv = new Conversation
        {
            Id = 1,
            TaskId = 90,
            StageId = "implementation",
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            Summary = "Stage completed with **zero errors** in `DiscountService.cs`.",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
            Turns = new List<ConversationTurn>
            {
                new()
                {
                    Id = 201,
                    ConversationId = 1,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Please implement **JWT Authentication** with `TokenService`."
                },
                new()
                {
                    Id = 202,
                    ConversationId = 1,
                    TurnIndex = 1,
                    Role = StageTurnRole.Assistant,
                    Content = "### Completed Implementation\n\n- Created `JwtTokenGenerator`\n- Added unit tests in `AuthTests.cs`\n\nSee [RFC 7519](https://tools.ietf.org/html/rfc7519) for specs."
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, false));

        // Stage summary markdown elements
        Assert.Contains("<strong>zero errors</strong>", cut.Markup);
        Assert.Contains("<code>DiscountService.cs</code>", cut.Markup);

        // User turn markdown elements
        Assert.Contains("<strong>JWT Authentication</strong>", cut.Markup);
        Assert.Contains("<code>TokenService</code>", cut.Markup);

        // Assistant turn markdown elements
        Assert.Contains("Completed Implementation", cut.Find(".turn-assistant h3").TextContent);
        Assert.Contains("<code>JwtTokenGenerator</code>", cut.Markup);
        Assert.Contains("RFC 7519", cut.Markup);
        Assert.Contains("href=\"https://tools.ietf.org/html/rfc7519\"", cut.Markup);
    }

    [Fact]
    public async Task TaskExecutionInspector_RendersOverviewTab_FormatsMarkdownDescriptionAndQuestions()
    {
        var task = new ForgeTask
        {
            Id = 91,
            WorkspaceId = 1,
            Title = "Task with Rich Markdown Overview",
            Description = "## Acceptance Criteria\n\n- Configure `IAuthenticationService`\n- Verify **security ratchet** passes\n\n> Note: Must support RS256 algorithm.",
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 1,
                    TaskId = 91,
                    Prompt = "Which key size?",
                    Answer = "Use **2048-bit** or higher `RSA` keys."
                }
            }
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 91));

        // Switch to Overview tab
        var overviewBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Task Overview"));
        overviewBtn.Click();

        // Formatted description elements
        Assert.Contains("Acceptance Criteria", cut.Find("h2").TextContent);
        Assert.Contains("<code>IAuthenticationService</code>", cut.Markup);
        Assert.Contains("<strong>security ratchet</strong>", cut.Markup);
        Assert.Contains("<blockquote>", cut.Markup);

        // Formatted clarification answer
        Assert.Contains("<strong>2048-bit</strong>", cut.Markup);
        Assert.Contains("<code>RSA</code>", cut.Markup);
    }

    [Fact]
    public async Task TaskExecutionInspector_OnInitialized_JoinsTaskGroup()
    {
        var task = new ForgeTask { Id = 101, Title = "SignalR Join Group Task", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 101));

        Assert.Contains(101, _hubConnection.ActiveTaskGroups);
    }

    [Fact]
    public async Task TaskExecutionInspector_OnParameterChange_LeavesOldAndJoinsNewTaskGroup()
    {
        var task1 = new ForgeTask { Id = 102, Title = "Task 1", Status = TaskOverallStatus.Open };
        var task2 = new ForgeTask { Id = 103, Title = "Task 2", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task1);
        await _taskRepository.CreateAsync(task2);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 102));
        Assert.Contains(102, _hubConnection.ActiveTaskGroups);
        Assert.DoesNotContain(103, _hubConnection.ActiveTaskGroups);

        cut.Render(p => p.Add(x => x.TaskId, 103));

        Assert.DoesNotContain(102, _hubConnection.ActiveTaskGroups);
        Assert.Contains(103, _hubConnection.ActiveTaskGroups);
    }

    [Fact]
    public async Task TaskExecutionInspector_OnDispose_LeavesTaskGroup()
    {
        var task = new ForgeTask { Id = 104, Title = "Task to Dispose", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 104));
        Assert.Contains(104, _hubConnection.ActiveTaskGroups);

        cut.Instance.Dispose();

        Assert.DoesNotContain(104, _hubConnection.ActiveTaskGroups);
    }

    [Fact]
    public async Task TaskExecutionInspector_SwitchingToHistoryTab_RendersTaskHistoryTimeline()
    {
        var task = new ForgeTask
        {
            Id = 105,
            WorkspaceId = 1,
            Title = "Task with history tab in inspector",
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created baseline");

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 105));

        // Switch to Revisions & History tab
        var historyTabBtn = cut.FindAll("button").First(b => b.TextContent.Contains("History") || b.TextContent.Contains("Revisions"));
        historyTabBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Revision History", cut.Markup);
            Assert.Contains("Version 1", cut.Markup);
            Assert.Contains("Created baseline", cut.Markup);
        });
    }

    [Fact]
    public async Task TaskExecutionInspector_WhenActiveConversationRunning_RendersRunningStageStatusInStepper()
    {
        var task = new ForgeTask
        {
            Id = 106,
            WorkspaceId = 1,
            Title = "Active Stage Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            Conversations = new List<Conversation>
            {
                new()
                {
                    TaskId = 106,
                    StageId = StandardStageIds.Implementation,
                    Status = StageExecutionStatus.Running,
                    StartedAt = DateTime.UtcNow
                }
            }
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 106));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".spinner-pulse"));
        });
    }

    [Fact]
    public async Task ClickingRestartButton_OpensRestartPipelineModal()
    {
        var task = new ForgeTask
        {
            Id = 107,
            WorkspaceId = 1,
            Title = "Task to test restart modal",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 107));

        var restartBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Restart Pipeline"));
        restartBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Restart Workflow Pipeline - Task #107", cut.Markup);
            Assert.NotNull(cut.Find("#confirm-restart-btn"));
            Assert.NotNull(cut.Find("#cancel-restart-btn"));
        });
    }

    [Fact]
    public async Task ConfirmingHardReset_InvokesDevQueueServiceWithHardResetMode()
    {
        var task = new ForgeTask
        {
            Id = 108,
            WorkspaceId = 1,
            Title = "Task to hard reset",
            Status = TaskOverallStatus.Failed,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 108));

        var restartBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Restart Pipeline"));
        restartBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("#radio-hard-reset"));
        });

        var hardResetRadio = cut.Find("#radio-hard-reset");
        hardResetRadio.Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("permanently discarded", cut.Markup);
        });

        var confirmBtn = cut.Find("#confirm-restart-btn");
        confirmBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(_devQueueService.LastRestartPipelineCall);
            Assert.Equal(108, _devQueueService.LastRestartPipelineCall.Value.TaskId);
            Assert.Equal(TaskRestartGitMode.HardResetToBaseBranch, _devQueueService.LastRestartPipelineCall.Value.GitMode);
        });
    }

    [Fact]
    public async Task ConfirmingSoftRestart_InvokesDevQueueServiceWithPreserveWorkMode()
    {
        var task = new ForgeTask
        {
            Id = 109,
            WorkspaceId = 1,
            Title = "Task to soft restart",
            Status = TaskOverallStatus.Failed,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 109));

        var restartBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Restart Pipeline"));
        restartBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("#confirm-restart-btn"));
        });

        var confirmBtn = cut.Find("#confirm-restart-btn");
        confirmBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(_devQueueService.LastRestartPipelineCall);
            Assert.Equal(109, _devQueueService.LastRestartPipelineCall.Value.TaskId);
            Assert.Equal(TaskRestartGitMode.PreserveWork, _devQueueService.LastRestartPipelineCall.Value.GitMode);
        });
    }

    [Fact]
    public async Task RestartPipelineButton_WhenTaskHasSquashCommitSha_IsDisabled()
    {
        var task = new ForgeTask
        {
            Id = 110,
            WorkspaceId = 1,
            Title = "Merged Task with SquashCommitSha",
            Status = TaskOverallStatus.Ready,
            SquashCommitSha = "c0ffee1234567890abcdef1234567890abcdef",
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 110));

        var restartBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Restart Pipeline"));
        Assert.True(restartBtn.HasAttribute("disabled"));
        Assert.Contains("Cannot restart a task that has already been merged", restartBtn.GetAttribute("title"));
    }

    [Fact]
    public async Task ConfirmingRestart_WhenServiceThrowsException_ClosesModalAndDisplaysErrorMessage()
    {
        var task = new ForgeTask
        {
            Id = 111,
            WorkspaceId = 1,
            Title = "Task triggering restart exception",
            Status = TaskOverallStatus.Failed,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        _devQueueService.RestartExceptionToThrow = new InvalidOperationException("Simulated reset failure");

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, 111));

        var restartBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Restart Pipeline"));
        restartBtn.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("#confirm-restart-btn"));
        });

        var confirmBtn = cut.Find("#confirm-restart-btn");
        confirmBtn.Click();

        cut.WaitForAssertion(() =>
        {
            // Modal must be dismissed
            Assert.Empty(cut.FindAll("#confirm-restart-btn"));
            // Error message must be visible on the inspector
            Assert.Contains("Error restarting pipeline: Simulated reset failure", cut.Markup);
        });
    }

    [Fact]
    public void StageConversationViewer_WhenConversationNull_AndLiveStreamingThinkingPresent_RendersLiveStreamingAccordion()
    {
        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, null)
            .Add(x => x.IsActiveStage, true)
            .Add(x => x.LiveStreamingThinking, "Deliberating on the architectural boundaries...")
            .Add(x => x.LiveStreamingMessage, "Drafting initial controller"));

        Assert.Contains("Active Stage Execution", cut.Markup);
        Assert.Contains("LIVE STREAMING", cut.Markup);
        Assert.Contains("Deliberating on the architectural boundaries...", cut.Markup);
        Assert.Contains("Reasoning Deliberation", cut.Markup);
        Assert.Contains("Drafting initial controller", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_WhenConversationNull_AndNoStreaming_RendersNoConversationSelected()
    {
        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, null)
            .Add(x => x.IsActiveStage, false)
            .Add(x => x.LiveStreamingThinking, null)
            .Add(x => x.LiveStreamingMessage, null));

        Assert.Contains("No stage conversation selected", cut.Markup);
        Assert.DoesNotContain("LIVE STREAMING", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_WhenConversationCompleted_DoesNotRenderLiveStreamingBadge()
    {
        var conv = new Conversation
        {
            Id = 1,
            TaskId = 10,
            StageId = "implementation",
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, true)
            .Add(x => x.LiveStreamingThinking, null)
            .Add(x => x.LiveStreamingMessage, null));

        Assert.Contains("Completed", cut.Markup);
        Assert.DoesNotContain("LIVE STREAMING", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_WhenDeterministicStageRunning_RendersTealLiveStreamingBadgeAndRunningTool()
    {
        var conv = new Conversation
        {
            Id = 1,
            TaskId = 10,
            StageId = "deterministic-pre-impl",
            StageKind = WorkflowStageKind.Deterministic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-10)
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, true)
            .Add(x => x.LiveRunningToolName, "deterministic-pre-impl-build")
            .Add(x => x.LiveRunningToolArguments, "dotnet build")
            .Add(x => x.LiveRunningToolOutput, "Compiling project..."));

        Assert.Contains("Deterministic CPU", cut.Markup);
        Assert.Contains("LIVE STREAMING", cut.Markup);
        Assert.Contains("badge-teal", cut.Markup);
        Assert.Contains("Running", cut.Markup);
        Assert.Contains("deterministic-pre-impl-build", cut.Markup);
        Assert.Contains("dotnet build", cut.Markup);
        Assert.Contains("Compiling project...", cut.Markup);
    }

    [Fact]
    public void StageConversationViewer_WhenToolTurnInProgress_RendersRunningBadgeOnToolCallCard_AndLiveOutput()
    {
        var conv = new Conversation
        {
            Id = 2,
            TaskId = 10,
            StageId = "deterministic-pre-impl",
            StageKind = WorkflowStageKind.Deterministic,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-20),
            Turns = new List<ConversationTurn>
            {
                new ConversationTurn
                {
                    Id = 101,
                    ConversationId = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = "deterministic-pre-impl-test",
                    ToolArguments = "dotnet test",
                    Timestamp = DateTime.UtcNow.AddSeconds(-5)
                }
            }
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Conversation, conv)
            .Add(x => x.IsActiveStage, true)
            .Add(x => x.LiveRunningToolName, "deterministic-pre-impl-test")
            .Add(x => x.LiveRunningToolArguments, "dotnet test")
            .Add(x => x.LiveRunningToolOutput, "Test run started: 4 tests executed so far..."));

        Assert.Contains("Running", cut.Markup);
        Assert.Contains("deterministic-pre-impl-test", cut.Markup);
        Assert.Contains("Test run started: 4 tests executed so far...", cut.Markup);
    }

    #region Fakes

    private class FakeDevQueueService : IDevQueueService
    {
        public int? LastRescheduledTaskId { get; private set; }
        public bool? LastRescheduledRestartPipeline { get; private set; }
        public (int TaskId, TaskRestartGitMode GitMode)? LastRestartPipelineCall { get; private set; }
        public int? LastRetriedTaskId { get; private set; }
        public Exception? RestartExceptionToThrow { get; set; }

        public Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DevQueueSnapshot(
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<ActiveLockSnapshot>()));

        public Task<bool> PromoteToUrgentAsync(string jobId, int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> PromoteToUrgentAsync(int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RequeueJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> EmergencyReleaseLockAsync(int workspaceId, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RetryCurrentStageAsync(int taskId, CancellationToken cancellationToken = default)
        {
            LastRetriedTaskId = taskId;
            LastRescheduledTaskId = taskId;
            LastRescheduledRestartPipeline = false;
            return Task.FromResult(true);
        }

        public Task<bool> RestartPipelineAsync(int taskId, TaskRestartGitMode gitMode = TaskRestartGitMode.PreserveWork, CancellationToken cancellationToken = default)
        {
            if (RestartExceptionToThrow != null)
            {
                throw RestartExceptionToThrow;
            }
            LastRestartPipelineCall = (taskId, gitMode);
            LastRescheduledTaskId = taskId;
            LastRescheduledRestartPipeline = true;
            return Task.FromResult(true);
        }

        public Task<int> ReorderInferenceQueueByPriorityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private class FakeTaskReviewService : ITaskReviewService
    {
        public List<FileDiffSummary> DiffFilesToReturn { get; set; } = new();
        public Exception? DiffExceptionToThrow { get; set; }

        public Task<TaskReviewSnapshot?> GetSnapshotAsync(int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult<TaskReviewSnapshot?>(null);

        public Task<IReadOnlyList<TaskReviewSnapshot>> GetPendingReviewsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaskReviewSnapshot>>(Array.Empty<TaskReviewSnapshot>());

        public Task<TaskDiffResult> GetTaskDiffAsync(int taskId, CancellationToken cancellationToken = default)
        {
            if (DiffExceptionToThrow != null)
            {
                throw DiffExceptionToThrow;
            }
            return Task.FromResult(new TaskDiffResult(DiffFilesToReturn, DiffProvenance.LiveWorktree, "main"));
        }

        public bool IsPendingReview(TaskReviewSnapshot snapshot)
            => snapshot.HasExecutionRun && snapshot.Status == TaskOverallStatus.Ready;

        public bool ShouldDisplayConflictResolvedBanner(TaskReviewSnapshot snapshot)
            => snapshot.HasResolvedConflict && snapshot.Status == TaskOverallStatus.Ready;
    }

    #endregion
}
