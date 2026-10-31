using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Core baseline Playwright E2E browser test suite for NightCodeForge.
/// Exercises critical UI paths against live Kestrel, SignalR circuit, and SQLite
/// with zero LLM inference calls:
/// 1. Workspace Provisioning & Switching (TopNavBar & Wizard)
/// 2. Home Dashboard KPIs, Active Tasks, and Global Pause Toggle
/// 3. Manual Task Creation, Backlog Kanban card & TaskDetail
/// 4. Backlog Dependency Badges, Filtering & TaskDetail Prerequisite Links
/// </summary>
[Collection("PlaywrightUiE2E")]
[Trait("Category", "E2E")]
public class CoreBacklogAndNavigationE2ETests : IAsyncLifetime
{
    private readonly PlaywrightWebHostFixture _fixture;
    private IBrowserContext _context = default!;
    private IPage _page = default!;

    public CoreBacklogAndNavigationE2ETests(PlaywrightWebHostFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(30000); // 30s timeout per action
    }

    public async Task DisposeAsync()
    {
        if (_page != null)
        {
            try { await _page.CloseAsync(); } catch { }
        }

        if (_context != null)
        {
            try { await _context.CloseAsync(); } catch { }
        }
    }

    [Fact]
    public async Task WorkspaceWizard_And_TopNavBar_WorkspaceSwitching_WorksEndToEnd()
    {
        // 1. Seed 1 existing workspace (Workspace A)
        var wsA = new Workspace
        {
            Name = "Alpha Repository",
            RepositoryPath = Path.Combine(Path.GetTempPath(), $"synthetic_alpha_{Guid.NewGuid():N}"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };
        Directory.CreateDirectory(wsA.RepositoryPath);

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(wsA);
        });

        // 2. Initialize synthetic repository fixture for Workspace B
        await using var repoB = new SyntheticRepoFixture();
        await repoB.InitializeAsync();

        // 3. Navigate to BaseUrl
        await _page.GotoAsync(_fixture.BaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".forge-layout");

        // Wait briefly for Blazor SignalR circuit to establish
        await _page.WaitForTimeoutAsync(1500);

        // 4. Open the Workspace Creation Wizard from TopNavBar
        var openWizardBtn = _page.Locator("button.forge-btn-icon-addon[title*='Create'], #home-setup-workspace-btn");
        var wizardModal = _page.Locator(".forge-modal-card.wizard-card");
        await ClickUntilVisibleAsync(openWizardBtn.First, wizardModal, TimeSpan.FromSeconds(20));

        // Step 1: Probe synthetic directory
        await _page.FillAsync("#repo-path-input", repoB.RepositoryPath);
        await _page.ClickAsync("button:has-text('Probe Directory')");
        await _page.WaitForSelectorAsync(".probe-feedback-card.probe-success", new PageWaitForSelectorOptions { Timeout = 15000 });

        // Step 1.2: Set workspace name and proceed
        await _page.FillAsync("#workspace-name-input", repoB.WorkspaceName);
        await _page.ClickAsync("button:has-text('Continue →')");

        // Step 2: Stack & Branch verification
        await _page.WaitForSelectorAsync("#base-branch-select", new PageWaitForSelectorOptions { Timeout = 10000 });
        await _page.ClickAsync("button:has-text('Continue →')");

        // Step 3: Execution Environment - select Bare Host (no container / WSL dependency)
        await _page.WaitForSelectorAsync(".execution-modes-grid", new PageWaitForSelectorOptions { Timeout = 10000 });
        await _page.ClickAsync(".mode-card:has-text('Bare Host')");
        await _page.ClickAsync("button:has-text('Provision Workspace')");

        // Step 4: Finish provisioning & activate workspace
        var finishBtn = _page.Locator("button:has-text('Open Workspace in Forge ➔')");
        await finishBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
        await finishBtn.ClickAsync();

        // 5. Verify the browser navigates to /w/{workspaceBId}
        await _page.WaitForURLAsync(new Regex(@"/w/\d+"), new PageWaitForURLOptions { Timeout = 15000 });
        var match = Regex.Match(_page.Url, @"/w/(\d+)");
        Assert.True(match.Success, $"Expected URL to contain /w/{{id}}, but was: {_page.Url}");
        var workspaceBId = int.Parse(match.Groups[1].Value);
        Assert.NotEqual(wsA.Id, workspaceBId);

        // 6. Use select.workspace-dropdown in TopNavBar to switch to Workspace A
        var wsDropdown = _page.Locator("select.workspace-dropdown");
        await wsDropdown.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await wsDropdown.SelectOptionAsync(wsA.Id.ToString());

        // Assert URL updates to /w/{wsA.Id} and breadcrumb reflects Workspace A
        await _page.WaitForURLAsync(new Regex($@"/w/{wsA.Id}"), new PageWaitForURLOptions { Timeout = 10000 });
        var breadcrumb = _page.Locator(".forge-breadcrumbs .breadcrumb-item.font-mono");
        await breadcrumb.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var breadcrumbTextA = await breadcrumb.InnerTextAsync();
        Assert.Contains(wsA.Name, breadcrumbTextA);

        // Switch back to Workspace B
        await wsDropdown.SelectOptionAsync(workspaceBId.ToString());
        await _page.WaitForURLAsync(new Regex($@"/w/{workspaceBId}"), new PageWaitForURLOptions { Timeout = 10000 });
        var breadcrumbTextB = await breadcrumb.InnerTextAsync();
        Assert.Contains(repoB.WorkspaceName, breadcrumbTextB);
    }

    [Fact]
    public async Task HomeDashboard_RendersSeededKpis_ActiveTasks_And_GlobalPauseToggle()
    {
        // 1. Seed a workspace with tasks in Open, Suggested, and InProgress states, plus a pending Question
        var ws = new Workspace
        {
            Name = "KPI Command Center Workspace",
            RepositoryPath = Path.Combine(Path.GetTempPath(), $"synthetic_kpi_{Guid.NewGuid():N}"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };
        Directory.CreateDirectory(ws.RepositoryPath);

        var openTask = new ForgeTask
        {
            Title = "Implement Distributed Cache Provider",
            Description = "Redis sentinel cluster provider",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        var suggestedTask = new ForgeTask
        {
            Title = "Refactor Database Context Pool",
            Description = "Investigate DbContext pool sizing",
            Status = TaskOverallStatus.Suggested,
            Priority = TaskPriority.Normal,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-15)
        };

        var inProgressTask = new ForgeTask
        {
            Title = "Background Agent Execution Engine",
            Description = "Active task running execution pipeline",
            Status = TaskOverallStatus.Reviewed, // IsRunnable = true
            CurrentStageId = "Plan",
            Priority = TaskPriority.Critical,
            CreatedAt = DateTime.UtcNow.AddMinutes(-40),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            openTask.WorkspaceId = ws.Id;
            suggestedTask.WorkspaceId = ws.Id;
            inProgressTask.WorkspaceId = ws.Id;

            db.Tasks.AddRange(openTask, suggestedTask, inProgressTask);
            await db.SaveChangesAsync();

            var question = new Question
            {
                TaskId = openTask.Id,
                StageId = "Plan",
                Status = QuestionStatus.Pending,
                Prompt = "Do we need multi-region replication for the distributed cache cluster?",
                AskedAt = DateTime.UtcNow
            };
            db.Questions.Add(question);
        });

        // 2. Navigate to /w/{workspaceId}
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".home-dashboard");

        // 3. Verify pending questions alert banner and inbox badge
        var questionBanner = _page.Locator("#home-pending-questions-banner");
        await questionBanner.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var inboxBadge = _page.Locator("#home-inbox-count-badge");
        await inboxBadge.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var badgeText = await inboxBadge.InnerTextAsync();
        Assert.Contains("1", badgeText);

        // 4. Verify workspace summary card with Open, Suggested, and Question counts
        var wsCard = _page.Locator($"#home-workspace-card-{ws.Id}");
        await wsCard.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var cardText = await wsCard.InnerTextAsync();
        Assert.Contains("Open: 2", cardText); // openTask (Open) + inProgressTask (Reviewed)
        Assert.Contains("Suggested: 1", cardText);

        var wsInboxPill = _page.Locator($"#home-ws-inbox-{ws.Id}");
        await wsInboxPill.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Contains("Questions: 1", await wsInboxPill.InnerTextAsync());

        // 5. Verify Running & Next Scheduled Tasks section
        var scheduledSection = _page.Locator("#home-scheduled-tasks-section");
        await scheduledSection.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        var scheduledTaskRow = _page.Locator($"#home-scheduled-task-{inProgressTask.Id}");
        await scheduledTaskRow.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Contains("Background Agent Execution Engine", await scheduledTaskRow.InnerTextAsync());

        // 6. Verify Live System Telemetry section
        var telemetrySection = _page.Locator("#home-telemetry-section");
        await telemetrySection.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // 7. Click #nav-btn-pause-operations ("Pause Operations") in TopNavBar
        var pauseBtn = _page.Locator("#nav-btn-pause-operations");
        var resumeBtn = _page.Locator("#nav-btn-resume-operations");
        await ClickUntilVisibleAsync(pauseBtn, resumeBtn, TimeSpan.FromSeconds(15));
        await _page.WaitForSelectorAsync("#nav-btn-resume-operations:has-text('Resume Operations')", new PageWaitForSelectorOptions { Timeout = 10000 });

        // Click again to resume operations
        await ClickUntilVisibleAsync(resumeBtn, pauseBtn, TimeSpan.FromSeconds(15));
        await _page.WaitForSelectorAsync("#nav-btn-pause-operations:has-text('Pause Operations')", new PageWaitForSelectorOptions { Timeout = 10000 });
    }

    [Fact]
    public async Task ManualTaskCreation_FlowsToBacklog_And_TaskDetailPage()
    {
        // 1. Seed an active workspace
        var ws = new Workspace
        {
            Name = "Manual Task Flow Workspace",
            RepositoryPath = Path.Combine(Path.GetTempPath(), $"synthetic_manual_{Guid.NewGuid():N}"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };
        Directory.CreateDirectory(ws.RepositoryPath);

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
        });

        // 2. Navigate to /w/{ws.Id}
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".forge-layout");

        // Wait for Blazor interactive circuit to establish and workspace to be hydrated in breadcrumb
        await _page.WaitForSelectorAsync($".forge-breadcrumbs .breadcrumb-item.font-mono:has-text('{ws.Name}')", new PageWaitForSelectorOptions { Timeout = 15000 });

        // 3. Navigate to /w/{ws.Id}/tasks/new via TopNavBar "+ New Task" button
        var newTaskNavBtn = _page.Locator("#nav-btn-new-task");
        var titleInput = _page.Locator("#task-title-input, #task-title");
        await ClickUntilVisibleAsync(newTaskNavBtn, titleInput, TimeSpan.FromSeconds(15));

        // 4. Fill out title, description, priority High, merge mode ManualReviewRequired
        var taskTitle = "Implement OAuth2 token refresh and rotation endpoint";
        var taskDescription = "## Requirements\n- Rotate refresh tokens upon consumption.\n- Invalidate compromised tokens.";

        await titleInput.FillAsync(taskTitle);

        var descInput = _page.Locator("#task-desc-input, #task-description");
        await descInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await descInput.FillAsync(taskDescription);

        // Select Priority 2 (High)
        var highPriorityChip = _page.Locator(".priority-select-chip:has-text('High'), .priority-select-chip:has-text('P2')");
        await highPriorityChip.First.ClickAsync();

        // Ensure Merge Mode is ManualReviewRequired (default or explicit click)
        var manualReviewCard = _page.Locator(".mergemode-card:has-text('Manual Review Required')");
        await manualReviewCard.First.ClickAsync();

        // 5. Click #btn-save-as-open ("Save as Open (Backlog Queue)")
        var saveOpenBtn = _page.Locator("#btn-save-as-open");
        var backlogSurface = _page.Locator(".backlog-page-container");
        await ClickUntilVisibleAsync(saveOpenBtn, backlogSurface, TimeSpan.FromSeconds(20));

        // 6. Verify navigation to backlog URL
        await _page.WaitForURLAsync(new Regex(@"backlog"), new PageWaitForURLOptions { Timeout = 10000 });

        // 7. Verify persisted task in SQLite
        var createdTask = await _fixture.QueryDatabaseAsync(async db =>
            await db.Tasks.OrderByDescending(t => t.Id).FirstOrDefaultAsync(t => t.WorkspaceId == ws.Id));

        Assert.NotNull(createdTask);
        Assert.Equal(taskTitle, createdTask.Title);
        Assert.Equal(taskDescription, createdTask.Description);
        Assert.Equal(TaskOverallStatus.Open, createdTask.Status);
        Assert.Equal(TaskPriority.High, createdTask.Priority);
        Assert.Equal(MergeMode.ManualReviewRequired, createdTask.MergeMode);
        Assert.Null(createdTask.LastHangfireJobId); // Zero inference jobs scheduled

        // 8. Verify the newly created task card (#kanban-card-{taskId}) is visible on Kanban board
        var kanbanCard = _page.Locator($"#kanban-card-{createdTask.Id}");
        await kanbanCard.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

        // Verify card displays High priority badge
        var cardText = await kanbanCard.InnerTextAsync();
        Assert.Contains("P2", cardText);

        // 9. Click the task card to navigate to TaskDetailPage
        var taskDetailPage = _page.Locator(".task-detail-page");
        await ClickUntilVisibleAsync(kanbanCard, taskDetailPage, TimeSpan.FromSeconds(15));
        await _page.WaitForURLAsync(new Regex($@"tasks/{createdTask.Id}"), new PageWaitForURLOptions { Timeout = 10000 });

        // 10. Verify task title, markdown description, status badge, priority, and history timeline
        var headerTitle = _page.Locator("h1");
        await headerTitle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Contains(taskTitle, await headerTitle.InnerTextAsync());

        var descBody = _page.Locator(".task-spec-markdown-body");
        await descBody.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var descText = await descBody.InnerTextAsync();
        Assert.Contains("Rotate refresh tokens", descText);

        // Status badge contains Open
        var badges = _page.Locator(".task-detail-header .forge-badge");
        var badgesText = await badges.AllInnerTextsAsync();
        Assert.Contains(badgesText, b => b.Contains("Open"));
        Assert.Contains(badgesText, b => b.Contains("P2") || b.Contains("High"));

        // Switch to history tab and assert timeline renders
        var historyTabBtn = _page.Locator("#tab-btn-history");
        var historyTimeline = _page.Locator(".task-history-timeline-container, [id^='task-history-timeline-']");
        await ClickUntilVisibleAsync(historyTabBtn, historyTimeline.First, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Backlog_DependencyBadges_And_Filtering_RenderCorrectly()
    {
        // 1. Seed two tasks (Task #1 Prerequisite in Open status, Task #2 Dependent in Suggested status with TaskDependency to Task #1)
        var ws = new Workspace
        {
            Name = "DAG & Filtering Workspace",
            RepositoryPath = Path.Combine(Path.GetTempPath(), $"synthetic_dag_{Guid.NewGuid():N}"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };
        Directory.CreateDirectory(ws.RepositoryPath);

        var task1 = new ForgeTask
        {
            Title = "Database Migration Engine",
            Description = "Base migration scripts and entity schemas",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var task2 = new ForgeTask
        {
            Title = "User Identity Store Service",
            Description = "Identity management dependent on migration engine",
            Status = TaskOverallStatus.Suggested,
            Priority = TaskPriority.Normal,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-15)
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            task1.WorkspaceId = ws.Id;
            task2.WorkspaceId = ws.Id;
            db.Tasks.AddRange(task1, task2);
            await db.SaveChangesAsync();

            var dep = new TaskDependency
            {
                TaskId = task2.Id,
                DependsOnTaskId = task1.Id
            };
            db.TaskDependencies.Add(dep);
        });

        // 2. Navigate to /w/{workspaceId}/backlog
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/backlog", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".backlog-page-container");

        // Wait for Blazor interactive circuit to establish and workspace to be hydrated in breadcrumb
        await _page.WaitForSelectorAsync($".forge-breadcrumbs .breadcrumb-item.font-mono:has-text('{ws.Name}')", new PageWaitForSelectorOptions { Timeout = 15000 });

        // 3. Verify both cards are visible initially
        var card1 = _page.Locator($"#kanban-card-{task1.Id}");
        var card2 = _page.Locator($"#kanban-card-{task2.Id}");
        await card1.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await card2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

        // Assert Task #2 card displays dependency count badge (🔗 1 Prereqs)
        var card2Text = await card2.InnerTextAsync();
        Assert.Contains("1 Prereqs", card2Text);

        // 4. Test filtering via status chips
        // Click Suggested filter chip
        var suggestedFilter = _page.Locator(".stat-chip:has-text('Suggested')");
        await suggestedFilter.ClickAsync();
        await _page.WaitForSelectorAsync(".stat-chip.active:has-text('Suggested')", new PageWaitForSelectorOptions { Timeout = 10000 });
        await card2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await card1.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5000 });

        // Click Open filter chip
        var openFilter = _page.Locator(".stat-chip:has-text('Open')");
        await openFilter.ClickAsync();
        await _page.WaitForSelectorAsync(".stat-chip.active:has-text('Open')", new PageWaitForSelectorOptions { Timeout = 10000 });
        await card1.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await card2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5000 });

        // Reset filter by clicking Total
        var totalFilter = _page.Locator(".stat-chip:has-text('Total')");
        await totalFilter.ClickAsync();
        await _page.WaitForSelectorAsync(".stat-chip.active:has-text('Total')", new PageWaitForSelectorOptions { Timeout = 10000 });
        await card1.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        await card2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        // 5. Navigate to /w/{workspaceId}/tasks/{task2Id}
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/tasks/{task2.Id}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".task-detail-page");

        // 6. Verify prerequisite dependency link is displayed and clickable
        var prereqLink = _page.Locator($".dependency-item[href*='tasks/{task1.Id}']");
        await prereqLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        Assert.Contains("Database Migration Engine", await prereqLink.InnerTextAsync());

        // Click the prerequisite link to navigate to Task #1
        var task1Title = _page.Locator("h1:has-text('Database Migration Engine')");
        await ClickUntilVisibleAsync(prereqLink, task1Title, TimeSpan.FromSeconds(15));
        await _page.WaitForURLAsync(new Regex($@"tasks/{task1.Id}"), new PageWaitForURLOptions { Timeout = 10000 });

        // Verify on Task #1 detail page
        Assert.Contains("Database Migration Engine", await task1Title.InnerTextAsync());
    }

    private static async Task ClickUntilVisibleAsync(ILocator button, ILocator target, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await target.CountAsync() > 0 && await target.First.IsVisibleAsync())
            {
                return;
            }

            try
            {
                if (await button.CountAsync() > 0 && await button.First.IsVisibleAsync())
                {
                    await button.First.ClickAsync();
                }
            }
            catch
            {
                // Transient Blazor circuit re-render
            }

            await Task.Delay(1000);
        }

        await target.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
    }
}
