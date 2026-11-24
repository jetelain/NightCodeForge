using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Components.Transcripts;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class MemoryPageTests : BunitContext
{
    private const int WorkspaceId = 3;

    private readonly Mock<IWorkspaceMemoryUiService> _service = new();
    private readonly OrchestratorHubClientConnection _hub = TestHubConnectionHelper.Create();
    private readonly WorkspaceStateService _workspaceState = new();

    public MemoryPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_hub);
        Services.AddSingleton<MemoryUpdateTracker>();
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(_service.Object);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<IWorkspaceRepository>(new InMemoryWorkspaceRepository());
        _workspaceState.SetActiveWorkspace(new Workspace
        {
            Id = WorkspaceId,
            Name = "Memory Test Repo",
            RepositoryPath = "/repos/mem"
        });
        SetupLocal();
        SetupShared();
    }

    private static MemoryFileEntry Entry(
        AgentMemoryScope scope,
        string path,
        string summary = "",
        int lineCount = 2,
        long sizeBytes = 10) =>
        new(scope, path, sizeBytes, lineCount, DateTime.UtcNow, summary);

    private void SetupLocal(params MemoryFileEntry[] entries) =>
        _service.Setup(s => s.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

    private void SetupShared(params MemoryFileEntry[] entries) =>
        _service.Setup(s => s.ListAsync(WorkspaceId, AgentMemoryScope.SharedRepository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

    private void SetupRead(AgentMemoryScope scope, string path, string content) =>
        _service.Setup(s => s.ReadAsync(WorkspaceId, scope, path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryFileContent(Entry(scope, path, lineCount: MemoryPresentationHelper.ComputeBudget(content).Lines, sizeBytes: content.Length), content));

    [Fact]
    public void MemoryPage_NoActiveWorkspace_RendersWorkspaceEmptyStateHero()
    {
        using var emptyContext = new BunitContext();
        emptyContext.JSInterop.Mode = JSRuntimeMode.Loose;
        emptyContext.Services.AddSingleton(_hub);
        emptyContext.Services.AddSingleton<MemoryUpdateTracker>();
        emptyContext.Services.AddSingleton(TestServices.CreateToastService());
        emptyContext.Services.AddSingleton(_service.Object);
        emptyContext.Services.AddSingleton(new WorkspaceStateService());
        emptyContext.Services.AddSingleton<IWorkspaceRepository>(new InMemoryWorkspaceRepository());

        var cut = emptyContext.Render<Memory>();

        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
        Assert.Empty(cut.FindAll("#memory-section-local"));
        Assert.Empty(cut.FindAll("#memory-quick-actions"));
    }

    [Fact]
    public void MemoryPage_ListsBothTiersSimultaneously_AndResetsUnreadCount()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "Pinned index"));
        SetupShared(Entry(AgentMemoryScope.SharedRepository, "AGENTS.md", "Repo rules"));

        var tracker = Services.GetRequiredService<MemoryUpdateTracker>();
        _hub.TriggerMemoryUpdated(WorkspaceId, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/MEMORY.md", "Updated", "x");
        Assert.Equal(1, tracker.GetCount(WorkspaceId));

        var cut = Render<Memory>();

        Assert.Equal(0, tracker.GetCount(WorkspaceId));
        Assert.NotEmpty(cut.FindAll("#memory-section-local"));
        Assert.NotEmpty(cut.FindAll("#memory-section-shared"));
        Assert.Contains("MEMORY.md", cut.Find("#memory-section-local").TextContent);
        Assert.Contains("Pinned index", cut.Find("#memory-section-local").TextContent);
        Assert.Contains("AGENTS.md", cut.Find("#memory-section-shared").TextContent);
        Assert.Contains("Repo rules", cut.Find("#memory-section-shared").TextContent);
        Assert.NotEmpty(cut.FindAll("#memory-create-btn"));
        Assert.NotEmpty(cut.FindAll("#memory-committed-readonly-note"));
    }

    [Fact]
    public void MemoryPage_DeepLinkQueryParams_OpensFile_WithHtmlDisabledRendering()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "vision.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "vision.md", "# Title\n\n<script>alert('xss')</script>\n\n<img src=x onerror=alert(1)>");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=vision.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        var rendered = cut.Find("#memory-rendered").InnerHtml;
        Assert.Contains("Title", rendered);
        Assert.DoesNotContain("<script", rendered);
        Assert.DoesNotContain("<img", rendered);
        Assert.Equal("vision.md", cut.Find("#memory-selected-path").TextContent);
        Assert.Equal(MemoryUiConstants.ScopeLocalLabel, cut.Find("#memory-selected-scope").TextContent.Trim());
    }

    [Fact]
    public void MemoryPage_InvalidDeepLinkScopeOrMissingFile_ShowsWarningAndKeepsTreeUsable()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "Pinned"));
        SetupShared(Entry(AgentMemoryScope.SharedRepository, "AGENTS.md", "Rules"));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=bogus&path=MEMORY.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("Unknown memory scope", cut.Find("#memory-error").TextContent);
        Assert.Contains("MEMORY.md", cut.Find("#memory-section-local").TextContent);
        Assert.Contains("AGENTS.md", cut.Find("#memory-section-shared").TextContent);

        _service.Setup(s => s.ReadAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, "missing.md", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MemoryFileNotFoundException("missing.md"));

        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=missing.md");
        var missingCut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("missing.md", missingCut.Find("#memory-error").TextContent);
        Assert.Contains("MEMORY.md", missingCut.Find("#memory-section-local").TextContent);
    }

    [Fact]
    public void MemoryPage_PinnedMemory_ShowsNearBudgetAndOverBudgetStates()
    {
        var nearLines = (int)Math.Ceiling(AgentMemoryConstants.MaxPinnedMemoryLines * 0.85);
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "Pinned", lineCount: nearLines, sizeBytes: 500));

        var nearCut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        var nearMeter = nearCut.Find("#memory-budget-meter");
        Assert.Contains("near budget", nearMeter.TextContent);
        Assert.Contains("near-budget", nearMeter.ClassName);
        Assert.Contains("memory-action-emphasized", nearCut.Find("#memory-action-summarize-pinned").ClassName);

        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "Pinned", lineCount: AgentMemoryConstants.MaxPinnedMemoryLines + 1, sizeBytes: 500));
        SetupRead(AgentMemoryScope.LocalWorkspace, "MEMORY.md", new string('x', AgentMemoryConstants.MaxPinnedMemoryBytes + 1));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=MEMORY.md");

        var overCut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        var overMeter = overCut.Find("#memory-budget-meter");
        Assert.Contains("over budget", overMeter.TextContent);
        Assert.Contains("over-budget", overMeter.ClassName);
        Assert.Contains("memory-action-emphasized", overCut.Find("#memory-action-summarize-pinned").ClassName);
    }

    [Fact]
    public void MemoryPage_EditAndSave_CallsServiceWithEditedContent()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "a.md", "old");
        _service.Setup(s => s.SaveLocalAsync(WorkspaceId, "a.md", "new text", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentMemoryChangeKind.Updated);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=a.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-edit-btn").Click();
        cut.Find("#memory-editor").Input("new text");
        cut.Find("#memory-save-btn").Click();

        _service.Verify(s => s.SaveLocalAsync(WorkspaceId, "a.md", "new text", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(cut.FindAll("#memory-editor"));
    }

    [Fact]
    public void MemoryPage_Delete_RequiresConfirmation()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "a.md", "old");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=a.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-delete-btn").Click();
        _service.Verify(s => s.DeleteLocalAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Find("#memory-delete-confirm-btn").Click();
        _service.Verify(s => s.DeleteLocalAsync(WorkspaceId, "a.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void MemoryPage_NewFile_InvalidNameAndTrimmedValidName()
    {
        _service.Setup(s => s.ValidateLocalFileName("bad.txt")).Throws(new MemoryValidationException("Memory files must be markdown (.md)."));
        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-new-file-name").Change("bad.txt");
        cut.Find("#memory-create-btn").Click();

        Assert.Contains("markdown", cut.Find("#memory-error").TextContent);
        Assert.Empty(cut.FindAll("#memory-editor"));

        cut.Find("#memory-new-file-name").Change("  trimmed.md  ");
        cut.Find("#memory-create-btn").Click();

        _service.Verify(s => s.ValidateLocalFileName("trimmed.md"), Times.Once);
        Assert.Equal("trimmed.md", cut.Find("#memory-selected-path").TextContent);
        Assert.NotEmpty(cut.FindAll("#memory-editor"));
    }

    [Fact]
    public void MemoryPage_SharedFileSelection_IsReadOnly_WhileLocalTreeRemainsVisible()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "notes.md"));
        SetupShared(Entry(AgentMemoryScope.SharedRepository, "AGENTS.md"));
        SetupRead(AgentMemoryScope.SharedRepository, "AGENTS.md", "# Rules");
        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("[data-memory-scope='shared']").Click();

        Assert.NotEmpty(cut.FindAll("#memory-committed-readonly-note"));
        Assert.Empty(cut.FindAll("#memory-edit-btn"));
        Assert.Empty(cut.FindAll("#memory-delete-btn"));
        Assert.Contains("Rules", cut.Find("#memory-rendered").InnerHtml);
        Assert.Contains("notes.md", cut.Find("#memory-section-local").TextContent);
    }

    [Fact]
    public void MemoryPage_RefreshesBothTiersOnUpdate_PreservesInProgressEdits_AndResetsOnExternalDelete()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "a.md", "initial");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=a.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        // Preserves in-progress edit when external update arrives
        cut.Find("#memory-edit-btn").Click();
        cut.Find("#memory-editor").Input("unsaved draft");
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"), Entry(AgentMemoryScope.LocalWorkspace, "fresh.md"));
        SetupShared(Entry(AgentMemoryScope.SharedRepository, "AGENTS.md"));

        _hub.TriggerMemoryUpdated(WorkspaceId, null, "", AgentMemoryConstants.ScopeSharedGitMetadataValue, "AGENTS.md", "Created", "x");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("fresh.md", cut.Find("#memory-section-local").TextContent);
            Assert.Contains("AGENTS.md", cut.Find("#memory-section-shared").TextContent);
            Assert.NotEmpty(cut.FindAll("#memory-editor"));
            Assert.Equal("unsaved draft", cut.Find("#memory-editor").GetAttribute("value"));
        });

        // Ignores updates from other workspaces
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "other.md"));
        _hub.TriggerMemoryUpdated(WorkspaceId + 1, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/other.md", "Created", "x");
        Assert.DoesNotContain("other.md", cut.Markup);

        // Cancel edit, then external delete resets selection cleanly without error banner
        cut.Find("#memory-cancel-edit-btn").Click();
        SetupLocal();
        _hub.TriggerMemoryUpdated(WorkspaceId, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Deleted", "");

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#memory-selected-path"));
            Assert.Empty(cut.FindAll("#memory-error"));
            Assert.Contains("No local memory files", cut.Markup);
        });
    }

    [Fact]
    public void MemoryPage_EditingAndCreatingPinnedMemory_UpdatesLiveBudgetMeterWhileTyping()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "small");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=MEMORY.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-edit-btn").Click();
        Assert.DoesNotContain("over budget", cut.Find("#memory-budget-meter").TextContent);

        cut.Find("#memory-editor").Input(new string('x', AgentMemoryConstants.MaxPinnedMemoryBytes + 1));
        Assert.Contains("over budget", cut.Find("#memory-budget-meter").TextContent);

        SetupLocal();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory");
        var freshCut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        freshCut.Find("#memory-new-file-name").Change("MEMORY.md");
        freshCut.Find("#memory-create-btn").Click();
        freshCut.Find("#memory-editor").Input(new string('y', AgentMemoryConstants.MaxPinnedMemoryBytes + 10));
        Assert.Contains("over budget", freshCut.Find("#memory-budget-meter").TextContent);
    }

    [Fact]
    public void MemoryPage_QuickIdeationActions_EnablementAndNavigation()
    {
        // 1. Empty local memory: summarize/deduplicate/promote/discuss all disabled
        var emptyCut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.True(emptyCut.Find("#memory-action-summarize-pinned").HasAttribute("disabled"));
        Assert.True(emptyCut.Find("#memory-action-deduplicate-local").HasAttribute("disabled"));
        Assert.True(emptyCut.Find("#memory-action-promote-repo").HasAttribute("disabled"));
        Assert.True(emptyCut.Find("#memory-action-discuss-selected").HasAttribute("disabled"));

        // 2. Local topic file only (no MEMORY.md), unselected -> deduplicate & promote enabled; summarize & discuss disabled
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "conventions.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "conventions.md", "notes");
        var topicCut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.True(topicCut.Find("#memory-action-summarize-pinned").HasAttribute("disabled"));
        Assert.False(topicCut.Find("#memory-action-deduplicate-local").HasAttribute("disabled"));
        Assert.False(topicCut.Find("#memory-action-promote-repo").HasAttribute("disabled"));
        Assert.True(topicCut.Find("#memory-action-discuss-selected").HasAttribute("disabled"));

        // 3. Select file -> discuss enabled and navigates to ideation with query params
        topicCut.Find("[data-memory-scope='local']").Click();
        Assert.False(topicCut.Find("#memory-action-discuss-selected").HasAttribute("disabled"));
        topicCut.Find("#memory-action-discuss-selected").Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith($"/w/{WorkspaceId}/ideation?memoryAction=discuss-file&scope=local&path=conventions.md", nav.Uri);
    }

    [Fact]
    public void WorkspaceMemoryTab_RendersLinkToMemoryPage()
    {
        var cut = Render<WorkspaceMemoryTab>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.WorkspaceName, "Test Workspace"));

        var link = cut.Find("#open-memory-page-link");
        Assert.Equal($"/w/{WorkspaceId}/memory", link.GetAttribute("href"));
    }

    [Fact]
    public void StageConversationViewer_ClickingMemoryBadge_NavigatesToMemoryPageDeepLink()
    {
        Services.AddSingleton<ITaskRepository>(new InMemoryTaskRepository());
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());

        var task = new ForgeTask { Id = 42, WorkspaceId = WorkspaceId, Title = "Mem Task" };
        var conv = new Conversation
        {
            Id = 7,
            TaskId = 42,
            StageId = "stage-2",
            Turns =
            [
                new ConversationTurn
                {
                    Id = 1,
                    TurnIndex = 0,
                    Role = StageTurnRole.Tool,
                    ToolName = "edit",
                    ToolArguments = "{\"path\":\".agents/memory/arch.md\"}",
                    ToolResult = "ok"
                }
            ]
        };

        var cut = Render<StageConversationViewer>(p => p
            .Add(x => x.Task, task)
            .Add(x => x.Conversation, conv));

        cut.Find("[data-testid=memory-badge]").Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith($"/w/{WorkspaceId}/memory?scope=shared&path=.agents%2Fmemory%2Farch.md", nav.Uri);
    }

    [Fact]
    public void MemoryPage_NewUnsavedFile_DisablesDiscussSelectedUntilSaved()
    {
        _service.Setup(s => s.SaveLocalAsync(WorkspaceId, "draft-note.md", "hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentMemoryChangeKind.Created);

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        cut.Find("#memory-new-file-name").Change("draft-note.md");
        cut.Find("#memory-create-btn").Click();

        Assert.True(cut.Find("#memory-action-discuss-selected").HasAttribute("disabled"));

        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "draft-note.md"));
        cut.Find("#memory-editor").Input("hello");
        cut.Find("#memory-save-btn").Click();

        Assert.False(cut.Find("#memory-action-discuss-selected").HasAttribute("disabled"));
    }

    [Fact]
    public void MemoryPage_StartEdit_ClearsPendingDeleteConfirmation()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "a.md", "initial");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local&path=a.md");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-delete-btn").Click();
        Assert.NotEmpty(cut.FindAll("#memory-delete-confirm"));

        cut.Find("#memory-edit-btn").Click();
        Assert.Empty(cut.FindAll("#memory-delete-confirm"));
        Assert.NotEmpty(cut.FindAll("#memory-editor"));
    }

    [Fact]
    public void MemoryPage_DeepLinkWithScopeOnly_ShowsWarningAndKeepsTreeUsable()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "Pinned"));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/memory?scope=local");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("missing a file path", cut.Find("#memory-error").TextContent);
        Assert.Contains("MEMORY.md", cut.Find("#memory-section-local").TextContent);
    }

    [Fact]
    public async Task MemoryPage_StaleAsyncSelection_DoesNotOverwriteNewerSelection()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "slow.md"), Entry(AgentMemoryScope.LocalWorkspace, "fast.md"));
        var slowGate = new TaskCompletionSource<MemoryFileContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(s => s.ReadAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, "slow.md", It.IsAny<CancellationToken>()))
            .Returns(slowGate.Task);
        SetupRead(AgentMemoryScope.LocalWorkspace, "fast.md", "Fast content");

        var cut = Render<Memory>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        var slowClickTask = cut.InvokeAsync(() => cut.FindAll("[data-testid=memory-file-item]")[0].Click());
        cut.FindAll("[data-testid=memory-file-item]")[1].Click();

        Assert.Equal("fast.md", cut.Find("#memory-selected-path").TextContent);

        slowGate.SetResult(new MemoryFileContent(Entry(AgentMemoryScope.LocalWorkspace, "slow.md"), "Slow content"));
        await slowClickTask;

        Assert.Equal("fast.md", cut.Find("#memory-selected-path").TextContent);
        Assert.Contains("Fast content", cut.Find("#memory-rendered").InnerHtml);
    }
}

public class MemoryBadgeComponentTests : BunitContext
{
    public MemoryBadgeComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<WorkspaceStateService>();
    }

    [Fact]
    public void ToolCallCard_MemoryBadge_RendersAndIsClickable()
    {
        var clicked = 0;

        var cut = Render<ToolCallCard>(p => p
            .Add(x => x.ToolName, "write")
            .Add(x => x.BadgeText, "priming")
            .Add(x => x.MemoryBadgeText, "🧠 Memory · Local · Updated")
            .Add(x => x.MemoryBadgeTooltip, "Open a.md in Agent Memory")
            .Add(x => x.OnMemoryBadgeClick, () => clicked++));

        Assert.Contains("priming", cut.Markup);
        cut.Find("[data-testid=memory-badge]").Click();
        Assert.Equal(1, clicked);
        Assert.Contains("Local", cut.Find("[data-testid=memory-badge]").TextContent);
    }

    [Fact]
    public void ToolCallCard_WithoutMemoryBadge_RendersNoMemoryPill()
    {
        var cut = Render<ToolCallCard>(p => p.Add(x => x.ToolName, "write"));

        Assert.Empty(cut.FindAll("[data-testid=memory-badge]"));
    }

    [Fact]
    public void IdeationToolCall_PassesMemoryBadgeThrough()
    {
        var clicked = 0;

        var cut = Render<IdeationToolCall>(p => p
            .Add(x => x.ToolName, "edit")
            .Add(x => x.Arguments, "{\"path\":\"/workspace/memory/a.md\"}")
            .Add(x => x.MemoryBadgeText, "🧠 Memory · Local")
            .Add(x => x.OnMemoryBadgeClick, () => clicked++));

        cut.Find("[data-testid=memory-badge]").Click();
        Assert.Equal(1, clicked);
    }
}
