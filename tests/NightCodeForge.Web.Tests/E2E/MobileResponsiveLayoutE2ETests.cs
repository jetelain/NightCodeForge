using Microsoft.Playwright;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Step 12.8A: Mobile and tablet viewport Playwright suite (zero LLM calls).
/// Verifies no horizontal root overflow, the off-canvas navigation drawer, the Ideation pane switcher
/// and the bottom-sheet task drawer across iPhone SE, iPhone 14 and iPad viewports.
/// </summary>
[Collection("PlaywrightUiE2E")]
[Trait("Category", "E2E")]
public class MobileResponsiveLayoutE2ETests : IAsyncLifetime
{
    private const int MobileBreakpointPx = 768;
    private const int ActionTimeoutMs = 30000;
    private const int PageReadyTimeoutMs = 20000;
    private const string RootOverflowScript = "() => document.documentElement.scrollWidth <= window.innerWidth";

    private readonly PlaywrightWebHostFixture _fixture;
    private readonly List<int> _seededWorkspaceIds = new();
    private IBrowserContext _context = default!;
    private IPage _page = default!;

    public MobileResponsiveLayoutE2ETests(PlaywrightWebHostFixture fixture)
    {
        _fixture = fixture;
    }

    public static IEnumerable<object[]> Viewports()
    {
        yield return new object[] { 375, 667 };   // iPhone SE
        yield return new object[] { 390, 844 };   // iPhone 14
        yield return new object[] { 768, 1024 };  // iPad
    }

    public static IEnumerable<object[]> PhoneViewports()
    {
        yield return new object[] { 375, 667 };
        yield return new object[] { 390, 844 };
    }

    public Task InitializeAsync() => Task.CompletedTask;

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

        if (_seededWorkspaceIds.Count > 0)
        {
            await _fixture.SeedDatabaseAsync(async db =>
            {
                var taskIds = db.Tasks
                    .Where(t => _seededWorkspaceIds.Contains(t.WorkspaceId))
                    .Select(t => t.Id)
                    .ToList();
                var questions = db.Questions.Where(q => taskIds.Contains(q.TaskId));
                db.Questions.RemoveRange(questions);

                var tasks = db.Tasks.Where(t => _seededWorkspaceIds.Contains(t.WorkspaceId));
                db.Tasks.RemoveRange(tasks);

                var workspaces = db.Workspaces.Where(w => _seededWorkspaceIds.Contains(w.Id));
                db.Workspaces.RemoveRange(workspaces);
                await Task.CompletedTask;
            });
        }
    }

    private async Task OpenViewportAsync(int width, int height)
    {
        _context = await _fixture.CreateContextAsync(new ViewportSize { Width = width, Height = height });
        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(ActionTimeoutMs);
    }

    private async Task<(Workspace Workspace, ForgeTask WaitingTask, ForgeTask ReadyTask, ForgeTask OpenTask)> SeedAsync()
    {
        var ws = new Workspace
        {
            Name = $"Mobile E2E {Guid.NewGuid():N}"[..20],
            RepositoryPath = _fixture.CreateTempWorkspaceDirectory("synthetic_mobile"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            CreatedAt = DateTime.UtcNow
        };

        var waiting = new ForgeTask
        {
            Title = "Mobile layout pending question task with a deliberately long title to stress wrapping",
            Description = "Waiting for human clarification",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            IsWaitingForHuman = true,
            Priority = TaskPriority.High,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var ready = new ForgeTask
        {
            Title = "Mobile layout pending review task",
            Description = "Ready for human code review",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            BaseBranch = "main",
            FeatureBranch = "feature/mobile-review",
            HasExecutionRun = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-4)
        };

        var open = new ForgeTask
        {
            Title = "Mobile layout suggested backlog task",
            Description = "Suggested task for backlog rendering",
            Status = TaskOverallStatus.Suggested,
            Priority = TaskPriority.Normal,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-3)
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();

            waiting.WorkspaceId = ws.Id;
            ready.WorkspaceId = ws.Id;
            open.WorkspaceId = ws.Id;
            db.Tasks.AddRange(waiting, ready, open);
            await db.SaveChangesAsync();

            db.Questions.Add(new Question
            {
                TaskId = waiting.Id,
                Prompt = "Which cache provider topology do you prefer?",
                Rationale = "Topology determines replication latency.",
                OptionsJson = "[\"Redis Cluster (Sharded)\",\"Redis Sentinel (Primary-Replica)\",\"In-Memory MemoryCache\"]",
                Status = QuestionStatus.Pending,
                StageId = StandardStageIds.TaskReview,
                AskedAt = DateTime.UtcNow.AddMinutes(-10)
            });
        });

        _seededWorkspaceIds.Add(ws.Id);
        return (ws, waiting, ready, open);
    }

    private async Task AssertNoHorizontalOverflowAsync(string url, string readySelector)
    {
        await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(readySelector, new PageWaitForSelectorOptions { Timeout = PageReadyTimeoutMs });
        await _page.WaitForTimeoutAsync(500);

        var fits = await _page.EvaluateAsync<bool>(RootOverflowScript);
        Assert.True(fits, $"Horizontal root overflow detected at {url} (viewport {_page.ViewportSize!.Width}px).");
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task KeyPages_HaveNoHorizontalRootOverflow(int width, int height)
    {
        var (ws, waiting, _, _) = await SeedAsync();
        await OpenViewportAsync(width, height);

        var baseUrl = _fixture.BaseUrl;
        await AssertNoHorizontalOverflowAsync($"{baseUrl}/", ".home-dashboard");
        await AssertNoHorizontalOverflowAsync($"{baseUrl}/w/{ws.Id}/ideation", ".ideation-grid");
        await AssertNoHorizontalOverflowAsync($"{baseUrl}/w/{ws.Id}/backlog", ".backlog-header");
        await AssertNoHorizontalOverflowAsync($"{baseUrl}/w/{ws.Id}/inbox?taskId={waiting.Id}", "#question-card-task-" + waiting.Id);
        await AssertNoHorizontalOverflowAsync($"{baseUrl}/w/{ws.Id}/inspector/{waiting.Id}", ".forge-main-content");
        await AssertNoHorizontalOverflowAsync($"{baseUrl}/w/{ws.Id}/review", ".forge-main-content");
    }

    [Theory]
    [MemberData(nameof(PhoneViewports))]
    public async Task Hamburger_OpensOffCanvasDrawer_AndNavLinkNavigatesAndCloses(int width, int height)
    {
        var (ws, _, _, _) = await SeedAsync();
        await OpenViewportAsync(width, height);
        Assert.True(width < MobileBreakpointPx);

        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/backlog", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var hamburger = _page.Locator("#mobile-nav-toggle");
        var sidebar = _page.Locator("#forge-sidebar");
        await hamburger.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PageReadyTimeoutMs });

        await _page.WaitForTimeoutAsync(1500); // let the Blazor circuit settle before measuring
        var box = await hamburger.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= 44 && box.Height >= 44, $"Hamburger must be at least 44x44 but was {box.Width}x{box.Height}.");
        Assert.False(await sidebar.IsVisibleAsync(), "Drawer must be hidden before the hamburger is tapped.");

        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(hamburger, sidebar, TimeSpan.FromSeconds(20));
        await _page.Locator("#mobile-nav-backdrop").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await _page.WaitForFunctionAsync("() => document.querySelector('#forge-sidebar').getBoundingClientRect().left >= 0");

        await sidebar.Locator("a.forge-nav-link", new LocatorLocatorOptions { HasText = "Review Room" }).ClickAsync();

        await _page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/review"), new PageWaitForURLOptions { Timeout = PageReadyTimeoutMs });
        await _page.WaitForSelectorAsync("#mobile-nav-backdrop", new PageWaitForSelectorOptions { State = WaitForSelectorState.Detached });
        await sidebar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = PageReadyTimeoutMs });
    }

    [Theory]
    [MemberData(nameof(PhoneViewports))]
    public async Task Ideation_SegmentedSwitcher_TogglesChatAndProposalsPanes(int width, int height)
    {
        var (ws, _, _, _) = await SeedAsync();
        await OpenViewportAsync(width, height);

        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/ideation", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var chatPane = _page.Locator(".chat-pane");
        var proposalsPane = _page.Locator(".proposals-pane");
        var proposalsTab = _page.Locator("#ideation-tab-proposals");
        await chatPane.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PageReadyTimeoutMs });

        Assert.False(await proposalsPane.IsVisibleAsync());
        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(proposalsTab, proposalsPane, TimeSpan.FromSeconds(20));
        Assert.False(await chatPane.IsVisibleAsync());

        await _page.Locator("#ideation-tab-chat").ClickAsync();
        await chatPane.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.False(await proposalsPane.IsVisibleAsync());
    }

    [Theory]
    [MemberData(nameof(PhoneViewports))]
    public async Task TaskDrawer_RendersAsBottomSheet_OnMobile(int width, int height)
    {
        var (ws, _, _, _) = await SeedAsync();
        await OpenViewportAsync(width, height);

        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/backlog", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".backlog-header", new PageWaitForSelectorOptions { Timeout = PageReadyTimeoutMs });

        // TaskDetailDrawer is not hosted by any page yet, so exercise its stylesheet contract on a synthetic instance.
        await _page.EvaluateAsync(@"() => {
            const overlay = document.createElement('div');
            overlay.className = 'task-drawer-overlay';
            overlay.id = 'synthetic-task-drawer-overlay';
            overlay.innerHTML = '<aside class=""task-drawer card-glass"" id=""synthetic-task-drawer""><div class=""task-drawer-body"" style=""min-height: 2000px""></div></aside>';
            document.body.appendChild(overlay);
        }");
        await _page.WaitForTimeoutAsync(500);

        var drawer = await _page.Locator("#synthetic-task-drawer").BoundingBoxAsync();
        Assert.NotNull(drawer);
        Assert.True(drawer!.Width >= width - 1, $"Bottom sheet must span the viewport width ({width}) but was {drawer.Width}.");
        Assert.True(drawer.Height <= height * 0.9 + 1, $"Bottom sheet must be at most 90dvh ({height * 0.9}) but was {drawer.Height}.");
        Assert.True(Math.Abs(drawer.Y + drawer.Height - height) <= 1, "Bottom sheet must be anchored to the bottom edge.");
    }
}

