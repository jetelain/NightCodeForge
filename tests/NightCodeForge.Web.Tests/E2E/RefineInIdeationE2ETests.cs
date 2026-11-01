using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Playwright E2E tests (zero LLM calls) for the "Refine in Ideation" workflow and the backlog mutation tool cards.
/// </summary>
[Collection("PlaywrightUiE2E")]
[Trait("Category", "E2E")]
public class RefineInIdeationE2ETests : IAsyncLifetime
{
    private readonly PlaywrightWebHostFixture _fixture;
    private IBrowserContext _context = default!;
    private IPage _page = default!;

    public RefineInIdeationE2ETests(PlaywrightWebHostFixture fixture)
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

    private static Workspace NewWorkspace(string name, string path) => new()
    {
        Name = name,
        RepositoryPath = path,
        DefaultBaseBranch = "main",
        ExecutionMode = ExecutionMode.BareHost,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task RefineButton_AppearsOnlyForUnstartedTasks_AndNavigatesWithSeededPrompt()
    {
        var ws = NewWorkspace("Refine Workspace", _fixture.CreateTempWorkspaceDirectory("synthetic_refine"));
        var unstarted = new ForgeTask { Title = "Unstarted Spec Task", Description = "d", Status = TaskOverallStatus.Open, HasExecutionRun = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var executed = new ForgeTask { Title = "Executed Task", Description = "d", Status = TaskOverallStatus.Open, HasExecutionRun = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            unstarted.WorkspaceId = ws.Id;
            executed.WorkspaceId = ws.Id;
            db.Tasks.AddRange(unstarted, executed);
        });

        // Executed task: no refine button on the detail page
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/tasks/{executed.Id}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".task-detail-page");
        await _page.WaitForSelectorAsync("#btn-inspect-task");
        Assert.Equal(0, await _page.Locator("#btn-refine-ideation").CountAsync());

        // Unstarted task: button present and navigates to ideation with the seeded prompt
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/tasks/{unstarted.Id}", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".task-detail-page");
        var refine = _page.Locator("#btn-refine-ideation");
        await refine.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        Assert.Contains("Refine in Ideation", await refine.InnerTextAsync());

        await refine.ClickAsync();
        await _page.WaitForURLAsync(new Regex($@"/w/{ws.Id}/ideation\?taskId={unstarted.Id}"), new PageWaitForURLOptions { Timeout = 15000 });

        var textarea = _page.Locator("textarea.chat-textarea");
        await Assertions.Expect(textarea).ToHaveValueAsync(new Regex($@"get_task\({unstarted.Id}\)"), new LocatorAssertionsToHaveValueOptions { Timeout = 15000 });
    }

    [Fact]
    public async Task IdeationChat_RendersBacklogMutationCards_FromSeededTranscript()
    {
        var ws = NewWorkspace("Mutation Cards Workspace", _fixture.CreateTempWorkspaceDirectory("synthetic_cards"));
        var conversation = new Conversation
        {
            ConversationType = ConversationType.Ideation,
            StageId = StandardStageIds.Ideation,
            Title = "Seeded mutation chat",
            Status = StageExecutionStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            conversation.WorkspaceId = ws.Id;
            conversation.Turns = new List<ConversationTurn>
            {
                new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Refine task 14" },
                new() { TurnIndex = 1, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.GetTask, ToolArguments = "{\"task_id\":14}", ToolResult = "{ \"id\": 14, \"title\": \"Add retry\", \"priority\": 3 }" },
                new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.EditTask, ToolArguments = "{\"task_id\":14,\"description\":\"x\"}", ToolResult = "Task #14 updated (description)." },
                new() { TurnIndex = 3, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.SetTaskPriority, ToolArguments = "{\"task_id\":14,\"priority\":2}", ToolResult = "Priorities updated for 1 task(s): #14: P3 -> P2." },
                new() { TurnIndex = 4, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.RemoveTask, ToolArguments = "{\"task_id\":18,\"reason\":\"duplicate\"}", ToolResult = "Task #18 removed (status Cancelled)." },
                new() { TurnIndex = 5, Role = StageTurnRole.Assistant, Content = "Done." }
            };
            db.Conversations.Add(conversation);
        });

        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/ideation", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var cards = _page.Locator("[data-testid='backlog-mutation-card']");
        await Assertions.Expect(cards).ToHaveCountAsync(4, new LocatorAssertionsToHaveCountOptions { Timeout = 20000 });
        var text = await _page.Locator(".chat-messages-container").InnerTextAsync();
        Assert.Contains("Loaded Task #14", text);
        Assert.Contains("Updated Task #14", text);
        Assert.Contains("Reprioritized Backlog", text);
        Assert.Contains("Removed Task #18", text);
    }
}

