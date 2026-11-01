using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Baseline Playwright E2E browser test suite for Human-in-the-Loop (HITL) and Observability pages:
/// 1. Human Question Inbox (/inbox)
/// 2. Review Room (/review)
/// 3. Task Execution Inspector (/inspector) and Transcripts Browser (/transcripts)
/// 4. Settings & Agent Profile Configuration (/settings)
/// </summary>
[Collection("PlaywrightUiE2E")]
[Trait("Category", "E2E")]
public class HitlAndObservabilityE2ETests : IAsyncLifetime
{
    private readonly PlaywrightWebHostFixture _fixture;
    private IBrowserContext _context = default!;
    private IPage _page = default!;

    public HitlAndObservabilityE2ETests(PlaywrightWebHostFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(30000);
    }

    public async Task DisposeAsync()
    {
        if (_page != null)
        {
            try
            {
                await _page.CloseAsync();
            }
            catch (Exception)
            {
                // Best-effort page close during test teardown
            }
        }

        if (_context != null)
        {
            try
            {
                await _context.CloseAsync();
            }
            catch (Exception)
            {
                // Best-effort browser context close during test teardown
            }
        }
    }

    [Fact]
    public async Task QuestionInbox_SelectOptionsAndSubmitBatch_UpdatesDatabaseAndClearsTaskWait()
    {
        // 1. Ensure background operations are paused so Hangfire doesn't immediately resume execution
        await _page.GotoAsync(_fixture.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".forge-layout");
        await _page.WaitForTimeoutAsync(1000);

        var pauseBtn = _page.Locator("#nav-btn-pause-operations");
        var resumeBtn = _page.Locator("#nav-btn-resume-operations");
        if (await pauseBtn.CountAsync() > 0 && await pauseBtn.First.IsVisibleAsync())
        {
            await PlaywrightWebHostFixture.ClickUntilVisibleAsync(pauseBtn.First, resumeBtn, TimeSpan.FromSeconds(15));
            await _page.WaitForSelectorAsync("#nav-btn-resume-operations", new PageWaitForSelectorOptions { Timeout = 10000 });
        }

        // 2. Seed a workspace, a task waiting for human input, and a batch of 2 questions
        var ws = new Workspace
        {
            Name = "Question Inbox E2E Workspace",
            RepositoryPath = _fixture.CreateTempWorkspaceDirectory("synthetic_inbox"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };

        var task = new ForgeTask
        {
            Title = "Cache Architecture Configuration",
            Description = "Select distributed cache topology and entry expiration TTL",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            IsWaitingForHuman = true,
            Priority = TaskPriority.High,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var batchId = $"batch-{Guid.NewGuid():N}"[..12];

        var q1 = new Question
        {
            Prompt = "Which cache provider topology do you prefer?",
            Rationale = "Topology determines replication latency and node failover policy.",
            OptionsJson = "[\"Redis Cluster (Sharded)\",\"Redis Sentinel (Primary-Replica)\",\"In-Memory MemoryCache\"]",
            Status = QuestionStatus.Pending,
            StageId = StandardStageIds.TaskReview,
            BatchId = batchId,
            AskedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        var q2 = new Question
        {
            Prompt = "What is the cache entry expiration timeout in seconds?",
            Rationale = "Determines default TTL across keys.",
            Status = QuestionStatus.Pending,
            StageId = StandardStageIds.TaskReview,
            BatchId = batchId,
            AskedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            task.WorkspaceId = ws.Id;
            db.Tasks.Add(task);
            await db.SaveChangesAsync();

            q1.TaskId = task.Id;
            q2.TaskId = task.Id;
            db.Questions.AddRange(q1, q2);
        });

        try
        {
            // 3. Navigate to /w/{workspaceId}/inbox
            await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/inbox", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await _page.WaitForSelectorAsync(".inbox-container");

            // Wait for the specific task/batch question card
            var card = _page.Locator($"#question-card-batch-{batchId}, [data-testid='question-card-{task.Id}']");
            await card.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

            // Verify both questions are rendered
            var cardText = await card.First.InnerTextAsync();
            Assert.Contains("Which cache provider topology do you prefer?", cardText);
            Assert.Contains("What is the cache entry expiration timeout in seconds?", cardText);

            // 4. Click option chip on Question 1 (Option 0: Redis Cluster)
            var optionChip = _page.Locator($"#btn-option-{q1.Id}-0");
            await optionChip.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await optionChip.ClickAsync();

            // 5. Fill custom answer in Question 2
            var answer2Input = _page.Locator($"#clarification-input-{q2.Id}, [data-testid='input-answer-{q2.Id}']");
            await answer2Input.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await answer2Input.First.FillAsync("300");

            // 6. Submit batch
            var submitBtn = _page.Locator($"#submit-all-btn-{batchId}, [data-testid='btn-submit-batch-{task.Id}']");
            await submitBtn.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await submitBtn.First.ClickAsync();

            // 7. Verify empty inbox state is displayed
            await _page.WaitForSelectorAsync("#inbox-empty-state, .empty-inbox", new PageWaitForSelectorOptions { Timeout = 15000 });

            // 8. Assert database updates: Questions are Answered and ForgeTask.IsWaitingForHuman is false
            var (savedQ1, savedQ2, savedTask) = await _fixture.QueryDatabaseAsync(async db =>
            {
                var sq1 = await db.Questions.FindAsync(q1.Id);
                var sq2 = await db.Questions.FindAsync(q2.Id);
                var st = await db.Tasks.FindAsync(task.Id);
                return (sq1, sq2, st);
            });

            Assert.NotNull(savedQ1);
            Assert.NotNull(savedQ2);
            Assert.NotNull(savedTask);

            Assert.Equal(QuestionStatus.Answered, savedQ1.Status);
            Assert.Contains("Redis Cluster", savedQ1.Answer);

            Assert.Equal(QuestionStatus.Answered, savedQ2.Status);
            Assert.Equal("300", savedQ2.Answer);

            Assert.False(savedTask.IsWaitingForHuman);
        }
        finally
        {
            // Close the seeded task before resuming operations so Hangfire never executes an LLM stage for it
            await _fixture.SeedDatabaseAsync(async db =>
            {
                var st = await db.Tasks.FindAsync(task.Id);
                if (st != null)
                {
                    st.Status = TaskOverallStatus.Closed;
                    st.IsWaitingForHuman = false;
                }
            });

            if (await resumeBtn.CountAsync() > 0 && await resumeBtn.First.IsVisibleAsync())
            {
                await PlaywrightWebHostFixture.ClickUntilVisibleAsync(resumeBtn.First, pauseBtn, TimeSpan.FromSeconds(15));
            }
        }
    }

    [Fact]
    public async Task ReviewRoom_RendersGitBranchDiff_And_HandlesRejectTaskAction()
    {
        // 1. Initialize synthetic repository with committed feature branch
        await using var repo = new SyntheticRepoFixture();
        await repo.InitializeAsync();

        const int taskId = 50;
        var featureBranchName = $"feature/task-{taskId}";

        // Create feature branch and commit changes to Calculator.cs
        await repo.RunGitAsync($"checkout -b {featureBranchName}");

        var calcPath = Path.Combine(repo.RepositoryPath, "src", "SyntheticLib", "Calculator.cs");
        var originalContent = await File.ReadAllTextAsync(calcPath);
        var modifiedContent = originalContent.Replace(
            "public int Subtract(int a, int b) => a - b;",
            "public int Subtract(int a, int b) => a - b;\n\n    /// <summary>\n    /// Computes the product of two integers.\n    /// </summary>\n    public int Multiply(int a, int b) => a * b;");
        await File.WriteAllTextAsync(calcPath, modifiedContent);

        await repo.RunGitAsync("add -A");
        await repo.RunGitAsync("commit -m \"Add Multiply method to Calculator\"");
        await repo.RunGitAsync("checkout main");

        // 2. Seed workspace and task in Ready status
        var ws = new Workspace
        {
            Name = repo.WorkspaceName,
            RepositoryPath = repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            var task = new ForgeTask
            {
                Id = taskId,
                WorkspaceId = ws.Id,
                Title = "Implement Calculator Multiplication",
                Description = "Add Multiply arithmetic method to Calculator class",
                Status = TaskOverallStatus.Ready,
                MergeMode = MergeMode.ManualReviewRequired,
                BaseBranch = "main",
                FeatureBranch = featureBranchName,
                HasExecutionRun = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-20),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
            };
            db.Tasks.Add(task);
        });

        // 3. Navigate to /w/{workspaceId}/review/50
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/review/{taskId}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".review-room-container");

        // 4. Verify Task #50 is loaded and review header is rendered
        var headerSection = _page.Locator("#review-header-section, .review-header");
        await headerSection.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        var headerText = await headerSection.InnerTextAsync();
        Assert.Contains($"ForgeTask #{taskId}", headerText);
        Assert.Contains(featureBranchName, headerText);

        // 5. Verify changed files list contains Calculator.cs
        var fileList = _page.Locator("#file-list");
        await fileList.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        var fileListText = await fileList.InnerTextAsync();
        Assert.Contains("Calculator.cs", fileListText);

        // 6. Verify DiffWorkspace renders the added Multiply line
        var diffViewer = _page.Locator("#diff-viewer-panel");
        await diffViewer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        var diffText = await diffViewer.InnerTextAsync();
        Assert.Contains("Multiply", diffText);

        // 7. Click Task Details button in review header to navigate to Task Details page
        var detailsLink = _page.Locator("#btn-header-task-details");
        await detailsLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await detailsLink.ClickAsync();

        await _page.WaitForURLAsync(new Regex($@"tasks/{taskId}"), new PageWaitForURLOptions { Timeout = 15000 });
        await _page.WaitForSelectorAsync(".task-detail-page");

        // 8. Reject the task via #btn-reject-task and TaskRejectionModal
        var rejectBtn = _page.Locator("#btn-reject-task");
        var rejectModal = _page.Locator(".task-rejection-modal");
        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(rejectBtn, rejectModal, TimeSpan.FromSeconds(15));

        // Fill rejection reason
        var reasonInput = _page.Locator("#rejection-reason-input, [data-testid='reject-reason-input']");
        await reasonInput.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await reasonInput.First.FillAsync("Business logic requires floating point calculations.");

        // Confirm rejection
        var confirmRejectBtn = _page.Locator("#btn-confirm-reject");
        await confirmRejectBtn.ClickAsync();

        // 9. Assert task status transitions to Rejected in SQLite
        var updatedTask = await PlaywrightWebHostFixture.WaitForConditionAsync(async () =>
        {
            var t = await _fixture.QueryDatabaseAsync(async db => await db.Tasks.FindAsync(taskId));
            return t != null && t.Status == TaskOverallStatus.Rejected ? t : null;
        }, TimeSpan.FromSeconds(10));

        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Rejected, updatedTask.Status);
    }

    [Fact]
    public async Task HomePage_ListsPendingHumanCodeReviewTasks_AndNavigatesToReviewRoom()
    {
        await using var repo = new SyntheticRepoFixture();
        await repo.InitializeAsync();

        var ws = new Workspace
        {
            Name = repo.WorkspaceName,
            RepositoryPath = repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };

        ForgeTask Make(string title, TaskOverallStatus status, bool hasRun) => new()
        {
            Title = title,
            Status = status,
            MergeMode = MergeMode.ManualReviewRequired,
            BaseBranch = "main",
            HasExecutionRun = hasRun,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var readyRunTask = Make("Home Pending Review Task", TaskOverallStatus.Ready, true);
        var readyNoRunTask = Make("Home Ready Without Run", TaskOverallStatus.Ready, false);
        var openTask = Make("Home Open Task", TaskOverallStatus.Open, false);
        openTask.IsWaitingForHuman = true; // Prevent background worker pickup during assertion
        var closedTask = Make("Home Closed Task", TaskOverallStatus.Closed, true);

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            foreach (var t in new[] { readyRunTask, readyNoRunTask, openTask, closedTask })
            {
                t.WorkspaceId = ws.Id;
            }

            db.Tasks.AddRange(readyRunTask, readyNoRunTask, openTask, closedTask);
            await db.SaveChangesAsync();

            foreach (var t in new[] { readyRunTask, readyNoRunTask, openTask, closedTask })
            {
                t.FeatureBranch = $"feature/task-{t.Id}";
            }
        });

        var featureBranchName = $"feature/task-{readyRunTask.Id}";
        await repo.RunGitAsync($"checkout -b {featureBranchName}");
        var calcPath = Path.Combine(repo.RepositoryPath, "src", "SyntheticLib", "Calculator.cs");
        await File.WriteAllTextAsync(calcPath, (await File.ReadAllTextAsync(calcPath)) + "\n// pending review change\n");
        await repo.RunGitAsync("add -A");
        await repo.RunGitAsync("commit -m \"Pending review change\"");
        await repo.RunGitAsync("checkout main");

        try
        {
            await _page.GotoAsync(_fixture.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await _page.WaitForSelectorAsync("#home-pending-reviews-section");

            var wsBadge = _page.Locator($"#home-ws-review-{ws.Id}");
            await wsBadge.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            Assert.Contains("Review: 1", await wsBadge.InnerTextAsync());
            Assert.True(int.Parse((await _page.Locator("#home-review-count-badge").InnerTextAsync()).Trim()) >= 1);

            Assert.Equal(1, await _page.Locator($"#home-pending-review-{readyRunTask.Id}").CountAsync());
            Assert.Equal(0, await _page.Locator($"#home-pending-review-{readyNoRunTask.Id}").CountAsync());
            Assert.Equal(0, await _page.Locator($"#home-pending-review-{openTask.Id}").CountAsync());
            Assert.Equal(0, await _page.Locator($"#home-pending-review-{closedTask.Id}").CountAsync());

            await _page.Locator($"#home-review-task-btn-{readyRunTask.Id}").ClickAsync();
            await _page.WaitForURLAsync(new Regex($@"/w/{ws.Id}/review/{readyRunTask.Id}"), new PageWaitForURLOptions { Timeout = 15000 });
            await _page.WaitForSelectorAsync(".review-room-container");
        }
        finally
        {
            await _fixture.SeedDatabaseAsync(async db =>
            {
                var tasks = db.Tasks.Where(t => t.WorkspaceId == ws.Id);
                db.Tasks.RemoveRange(tasks);
                var workspace = await db.Workspaces.FindAsync(ws.Id);
                if (workspace != null)
                {
                    db.Workspaces.Remove(workspace);
                }
            });
        }
    }

    [Fact]
    public async Task TaskInspector_And_Transcripts_RenderSeededStagesAndConversationTurns()
    {
        // 1. Initialize synthetic repository for transcript storage
        await using var repo = new SyntheticRepoFixture();
        await repo.InitializeAsync();

        // Create docs/transcripts directory
        var transcriptsDir = Path.Combine(repo.RepositoryPath, "docs", "transcripts");
        Directory.CreateDirectory(transcriptsDir);

        var ws = new Workspace
        {
            Name = repo.WorkspaceName,
            RepositoryPath = repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };

        var task = new ForgeTask
        {
            Title = "Inspector & Observability Validation Task",
            Description = "Comprehensive verification of PipelineStepper, turns, and execution logs",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            HasExecutionRun = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        };

        var convReview = new Conversation
        {
            StageId = StandardStageIds.TaskReview,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-20),
            CompletedAt = DateTime.UtcNow.AddMinutes(-15)
        };

        var convImpl = new Conversation
        {
            StageId = StandardStageIds.Implementation,
            Status = StageExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            task.WorkspaceId = ws.Id;
            db.Tasks.Add(task);
            await db.SaveChangesAsync();

            convReview.TaskId = task.Id;
            convImpl.TaskId = task.Id;
            db.Conversations.AddRange(convReview, convImpl);
            await db.SaveChangesAsync();

            // Seed turns for implementation conversation
            var turn1 = new ConversationTurn
            {
                ConversationId = convImpl.Id,
                TurnIndex = 1,
                Role = StageTurnRole.User,
                Content = "Please implement the Calculator multiplication feature.",
                Timestamp = DateTime.UtcNow.AddMinutes(-9)
            };

            var turn2 = new ConversationTurn
            {
                ConversationId = convImpl.Id,
                TurnIndex = 2,
                Role = StageTurnRole.Assistant,
                ThinkingTrace = "Checking existing arithmetic methods in Calculator.cs.",
                Content = "I will inspect the source code and add the Multiply method.",
                Timestamp = DateTime.UtcNow.AddMinutes(-8)
            };

            var turn3 = new ConversationTurn
            {
                ConversationId = convImpl.Id,
                TurnIndex = 3,
                Role = StageTurnRole.Tool,
                ToolName = "read_file",
                ToolArguments = "{\"path\": \"src/SyntheticLib/Calculator.cs\"}",
                Timestamp = DateTime.UtcNow.AddMinutes(-7)
            };

            var turn4 = new ConversationTurn
            {
                ConversationId = convImpl.Id,
                TurnIndex = 4,
                Role = StageTurnRole.ToolResult,
                ToolName = "read_file",
                ToolResult = "public class Calculator { public int Add(int a, int b) => a + b; }",
                Timestamp = DateTime.UtcNow.AddMinutes(-6)
            };

            var turn5 = new ConversationTurn
            {
                ConversationId = convImpl.Id,
                TurnIndex = 5,
                Role = StageTurnRole.Assistant,
                Content = "Implementation in progress: added Multiply method to Calculator.cs.",
                Timestamp = DateTime.UtcNow.AddMinutes(-5)
            };

            db.ConversationTurns.AddRange(turn1, turn2, turn3, turn4, turn5);

            // Seed execution logs
            var log1 = new ExecutionLog
            {
                TaskId = task.Id,
                StageId = StandardStageIds.Implementation,
                Level = LogLevel.Information,
                Message = "Starting compilation check for Calculator.cs",
                Timestamp = DateTime.UtcNow.AddMinutes(-4)
            };

            var log2 = new ExecutionLog
            {
                TaskId = task.Id,
                StageId = StandardStageIds.Implementation,
                Level = LogLevel.Information,
                Message = "Compilation check passed with zero errors",
                Timestamp = DateTime.UtcNow.AddMinutes(-3)
            };

            db.ExecutionLogs.AddRange(log1, log2);
        });

        // 2. Write a markdown transcript file into {workspace.RepositoryPath}/docs/transcripts/
        var transcriptFileName = $"task-{task.Id}-implementation-completed.md";
        var transcriptFilePath = Path.Combine(transcriptsDir, transcriptFileName);
        var transcriptContent = $@"---
task_id: {task.Id}
stage_id: implementation
agent_profile: FullStackCoder
model: qwen2.5-coder:32b
duration_ms: 4500
tokens:
  prompt: 1200
  completion: 300
  total: 1500
status: Completed
---

### [00:01] 👤 User
Implement the Calculator multiplication feature.

### [00:05] 🤖 Assistant
Added Multiply method to Calculator.cs successfully.
";
        await File.WriteAllTextAsync(transcriptFilePath, transcriptContent);

        // 3. Navigate to /w/{workspaceId}/inspector/{taskId}
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/inspector/{task.Id}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync("#task-execution-inspector, .task-execution-inspector");

        // 4. Verify PipelineStepper displays stages
        var stepper = _page.Locator(".pipeline-stepper, .stepper-track");
        await stepper.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        var stepperText = await stepper.InnerTextAsync();
        Assert.Contains("Review", stepperText);
        Assert.Contains("Implementation", stepperText);

        // 5. Verify conversation turns are rendered in StageConversationViewer
        var turnBubble = _page.Locator(".conversation-turns-list .chat-bubble");
        await turnBubble.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

        var turnsText = await PlaywrightWebHostFixture.WaitForConditionAsync(async () =>
        {
            var text = await _page.Locator(".conversation-turns-list").InnerTextAsync();
            return text.Contains("Please implement the Calculator multiplication feature") ? text : null;
        }, TimeSpan.FromSeconds(10));

        Assert.NotNull(turnsText);
        Assert.Contains("Please implement the Calculator multiplication feature", turnsText);
        Assert.Contains("I will inspect the source code and add the Multiply method", turnsText);
        Assert.Contains("read_file", turnsText);

        // 6. Switch to Terminal & Logs tab and verify seeded logs
        var terminalTabBtn = _page.Locator("button:has-text('Terminal & Logs')");
        await terminalTabBtn.ClickAsync();

        var terminalBody = _page.Locator("#terminal-console-body, .terminal-body");
        await terminalBody.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var terminalText = await terminalBody.InnerTextAsync();
        Assert.Contains("Starting compilation check for Calculator.cs", terminalText);
        Assert.Contains("Compilation check passed with zero errors", terminalText);

        // 7. Navigate to /w/{workspaceId}/transcripts
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/transcripts", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".transcripts-container");

        // 8. Verify transcript file appears in the sidebar list and click it
        var transcriptItem = _page.Locator($".transcript-file-list li.file-item:has-text('Task #{task.Id}')");
        await transcriptItem.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await transcriptItem.ClickAsync();

        // 9. Verify transcript details rendered
        var mainCanvas = _page.Locator(".transcript-main");
        await mainCanvas.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        var mainText = await mainCanvas.InnerTextAsync();
        Assert.Contains("Implement the Calculator multiplication feature", mainText);
        Assert.Contains("Added Multiply method to Calculator.cs successfully", mainText);
    }

    [Fact]
    public async Task SettingsPage_TabNavigation_And_WorkspaceUpdate_PersistsToDatabase()
    {
        // 1. Seed a workspace
        var ws = new Workspace
        {
            Name = "Settings E2E Workspace",
            RepositoryPath = _fixture.CreateTempWorkspaceDirectory("synthetic_settings"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            BuildTimeoutSeconds = 45,
            TestTimeoutSeconds = 90,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit,
            CreatedAt = DateTime.UtcNow
        };

        await _fixture.SeedDatabaseAsync(db =>
        {
            db.Workspaces.Add(ws);
            return Task.CompletedTask;
        });

        // 2. Navigate to /w/{workspaceId}/settings
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/settings", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".page-header");
        await _page.WaitForTimeoutAsync(1500);

        // 3. Test Global Scope navigation tabs
        var workflowsTabBtn = _page.Locator(".settings-tabs button:has-text('Workflows & Stages')");
        var workflowsHeader = _page.Locator("h3:has-text('Workflows & Execution Stages')");
        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(workflowsTabBtn, workflowsHeader, TimeSpan.FromSeconds(15));
        var workflowsText = await _page.Locator(".settings-tab-content").InnerTextAsync();
        Assert.Contains("Workflows", workflowsText);

        var profilesTabBtn = _page.Locator(".settings-tabs button:has-text('Global Agent Profiles')");
        var profilesHeader = _page.Locator("h3:has-text('Global Agent Reasoning Profiles')");
        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(profilesTabBtn, profilesHeader, TimeSpan.FromSeconds(15));
        var profilesText = await _page.Locator(".settings-tab-content").InnerTextAsync();
        Assert.Contains("Agent Reasoning Profiles", profilesText);

        // 4. Switch to Workspace Settings scope
        var workspaceScopeBtn = _page.Locator(".scope-switcher button:has-text('Workspace Settings')");
        var confinementHeader = _page.Locator("h3:has-text('Repository & Execution Confinement')");
        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(workspaceScopeBtn, confinementHeader, TimeSpan.FromSeconds(15));

        // 5. Update workspace settings: Build Timeout to 180 and Global Skills Injection Mode to ForceEnabled
        var buildTimeoutInput = _page.Locator("label:has-text('Build Timeout') ~ input");
        await buildTimeoutInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await buildTimeoutInput.FillAsync("180");

        var injectionModeSelect = _page.Locator("#global-skills-injection-mode");
        await injectionModeSelect.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await injectionModeSelect.SelectOptionAsync(GlobalSkillsInjectionMode.ForceEnabled.ToString());

        // 6. Click Save Workspace Settings
        var saveBtn = _page.Locator("button:has-text('Save Workspace Settings')");
        await saveBtn.ClickAsync();

        // 7. Verify persistence in SQLite
        var updatedWs = await PlaywrightWebHostFixture.WaitForConditionAsync(async () =>
        {
            var result = await _fixture.QueryDatabaseAsync(async db => await db.Workspaces.FindAsync(ws.Id));
            return result != null && result.BuildTimeoutSeconds == 180 && result.GlobalSkillsInjectionMode == GlobalSkillsInjectionMode.ForceEnabled
                ? result
                : null;
        }, TimeSpan.FromSeconds(10));

        Assert.NotNull(updatedWs);
        Assert.Equal(180, updatedWs.BuildTimeoutSeconds);
        Assert.Equal(GlobalSkillsInjectionMode.ForceEnabled, updatedWs.GlobalSkillsInjectionMode);

        // 8. Switch across Workspace tabs: Workspace Agent Profiles and Workflows & Stages
        var wsProfilesTab = _page.Locator(".settings-tabs button:has-text('Workspace Agent Profiles')");
        await wsProfilesTab.ClickAsync();
        await _page.WaitForSelectorAsync(".settings-tab-content", new PageWaitForSelectorOptions { Timeout = 10000 });

        var wsWorkflowsTab = _page.Locator(".settings-tabs button:has-text('Workflows & Stages')");
        await wsWorkflowsTab.ClickAsync();
        await _page.WaitForSelectorAsync(".settings-tab-content", new PageWaitForSelectorOptions { Timeout = 10000 });
    }
}
