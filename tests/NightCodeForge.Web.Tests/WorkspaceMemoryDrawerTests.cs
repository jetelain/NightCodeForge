using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Memory;
using NightCodeForge.Web.Components.Memory;
using NightCodeForge.Web.Components.Transcripts;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WorkspaceMemoryDrawerTests : BunitContext
{
    private const int WorkspaceId = 3;

    private readonly Mock<IWorkspaceMemoryUiService> _service = new();
    private readonly OrchestratorHubClientConnection _hub = TestHubConnectionHelper.Create();

    public WorkspaceMemoryDrawerTests()
    {
        Services.AddSingleton(_hub);
        Services.AddSingleton<MemoryUpdateTracker>();
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(_service.Object);
    }

    private static MemoryFileEntry Entry(AgentMemoryScope scope, string path, string summary = "") =>
        new(scope, path, 10, 2, DateTime.UtcNow, summary);

    private void SetupLocal(params MemoryFileEntry[] entries) =>
        _service.Setup(s => s.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

    private void SetupRead(AgentMemoryScope scope, string path, string content) =>
        _service.Setup(s => s.ReadAsync(WorkspaceId, scope, path, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryFileContent(Entry(scope, path), content));

    [Fact]
    public void Drawer_ListsLocalFiles_WithSummary()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "Pinned index"));

        var cut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("MEMORY.md", cut.Markup);
        Assert.Contains("Pinned index", cut.Markup);
        Assert.NotEmpty(cut.FindAll("#memory-create-btn"));
    }

    [Fact]
    public void Drawer_InitialPath_OpensFile_WithHtmlDisabledRendering()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "vision.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "vision.md", "# Title\n\n<script>alert('xss')</script>\n\n<img src=x onerror=alert(1)>");

        var cut = Render<WorkspaceMemoryDrawer>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.InitialRelativePath, "vision.md"));

        var rendered = cut.Find("#memory-rendered").InnerHtml;
        Assert.Contains("Title", rendered);
        Assert.DoesNotContain("<script", rendered);
        Assert.DoesNotContain("<img", rendered);
        Assert.Equal("vision.md", cut.Find("#memory-selected-path").TextContent);
    }

    [Fact]
    public void Drawer_PinnedMemory_ShowsBudgetMeter()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "MEMORY.md", new string('x', AgentMemoryConstants.MaxPinnedMemoryBytes + 1));

        var cut = Render<WorkspaceMemoryDrawer>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.InitialRelativePath, "MEMORY.md"));

        Assert.Contains("over budget", cut.Find("#memory-budget-meter").TextContent);
    }

    [Fact]
    public void Drawer_EditAndSave_CallsServiceWithEditedContent()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "a.md", "old");
        _service.Setup(s => s.SaveLocalAsync(WorkspaceId, "a.md", "new text", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentMemoryChangeKind.Updated);
        var cut = Render<WorkspaceMemoryDrawer>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.InitialRelativePath, "a.md"));

        cut.Find("#memory-edit-btn").Click();
        cut.Find("#memory-editor").Input("new text");
        cut.Find("#memory-save-btn").Click();

        _service.Verify(s => s.SaveLocalAsync(WorkspaceId, "a.md", "new text", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(cut.FindAll("#memory-editor"));
    }

    [Fact]
    public void Drawer_Delete_RequiresConfirmation()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "a.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "a.md", "old");
        var cut = Render<WorkspaceMemoryDrawer>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.InitialRelativePath, "a.md"));

        cut.Find("#memory-delete-btn").Click();
        _service.Verify(s => s.DeleteLocalAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Find("#memory-delete-confirm-btn").Click();
        _service.Verify(s => s.DeleteLocalAsync(WorkspaceId, "a.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Drawer_NewFile_InvalidName_ShowsValidationError()
    {
        SetupLocal();
        _service.Setup(s => s.ValidateLocalFileName("bad.txt")).Throws(new MemoryValidationException("Memory files must be markdown (.md)."));
        var cut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-new-file-name").Change("bad.txt");
        cut.Find("#memory-create-btn").Click();

        Assert.Contains("markdown", cut.Find("#memory-error").TextContent);
        Assert.Empty(cut.FindAll("#memory-editor"));
    }

    [Fact]
    public void Drawer_CommittedTab_IsReadOnly_AndReadsSharedScope()
    {
        SetupLocal();
        _service.Setup(s => s.ListAsync(WorkspaceId, AgentMemoryScope.SharedRepository, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Entry(AgentMemoryScope.SharedRepository, "AGENTS.md")]);
        SetupRead(AgentMemoryScope.SharedRepository, "AGENTS.md", "# Rules");
        var cut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-tab-shared").Click();
        cut.Find("[data-testid=memory-file-item]").Click();

        Assert.NotEmpty(cut.FindAll("#memory-committed-readonly-note"));
        Assert.Empty(cut.FindAll("#memory-edit-btn"));
        Assert.Empty(cut.FindAll("#memory-delete-btn"));
        Assert.Empty(cut.FindAll("#memory-create-btn"));
        Assert.Contains("Rules", cut.Find("#memory-rendered").InnerHtml);
    }

    [Fact]
    public void Drawer_RefreshesList_WhenMemoryUpdatedForCurrentWorkspace()
    {
        SetupLocal();
        var cut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Contains("No memory files", cut.Markup);
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "fresh.md"));

        _hub.TriggerMemoryUpdated(WorkspaceId, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/fresh.md", "Created", "x");

        cut.WaitForAssertion(() => Assert.Contains("fresh.md", cut.Markup));
    }

    [Fact]
    public void Drawer_IgnoresUpdatesFromOtherWorkspaces()
    {
        SetupLocal();
        var cut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "other.md"));

        _hub.TriggerMemoryUpdated(WorkspaceId + 1, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/other.md", "Created", "x");

        Assert.DoesNotContain("other.md", cut.Markup);
    }

    [Fact]
    public void Drawer_EditingAndCreatingPinnedMemory_UpdatesLiveBudgetMeterWhileTyping()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "MEMORY.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "MEMORY.md", "small");
        var cut = Render<WorkspaceMemoryDrawer>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.InitialRelativePath, "MEMORY.md"));

        cut.Find("#memory-edit-btn").Click();
        Assert.DoesNotContain("over budget", cut.Find("#memory-budget-meter").TextContent);

        cut.Find("#memory-editor").Input(new string('x', AgentMemoryConstants.MaxPinnedMemoryBytes + 1));
        Assert.Contains("over budget", cut.Find("#memory-budget-meter").TextContent);

        cut.Find("#memory-cancel-edit-btn").Click();
        SetupLocal();
        var freshCut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        freshCut.Find("#memory-new-file-name").Change("MEMORY.md");
        freshCut.Find("#memory-create-btn").Click();
        freshCut.Find("#memory-editor").Input(new string('y', AgentMemoryConstants.MaxPinnedMemoryBytes + 10));
        Assert.Contains("over budget", freshCut.Find("#memory-budget-meter").TextContent);
    }

    [Fact]
    public void Drawer_ExternalDeletedNotice_ResetsSelectionWithoutErrorBanner()
    {
        SetupLocal(Entry(AgentMemoryScope.LocalWorkspace, "gone.md"));
        SetupRead(AgentMemoryScope.LocalWorkspace, "gone.md", "content");
        var cut = Render<WorkspaceMemoryDrawer>(p => p
            .Add(x => x.WorkspaceId, WorkspaceId)
            .Add(x => x.InitialRelativePath, "gone.md"));

        SetupLocal();
        _hub.TriggerMemoryUpdated(WorkspaceId, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/gone.md", "Deleted", "");

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#memory-selected-path"));
            Assert.Empty(cut.FindAll("#memory-error"));
            Assert.Contains("No memory files", cut.Markup);
        });
    }

    [Fact]
    public void Drawer_CreateNewFile_TrimsSurroundingWhitespace()
    {
        SetupLocal();
        var cut = Render<WorkspaceMemoryDrawer>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        cut.Find("#memory-new-file-name").Change("  trimmed.md  ");
        cut.Find("#memory-create-btn").Click();

        _service.Verify(s => s.ValidateLocalFileName("trimmed.md"), Times.Once);
        Assert.Equal("trimmed.md", cut.Find("#memory-selected-path").TextContent);
        Assert.NotEmpty(cut.FindAll("#memory-editor"));
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
            .Add(x => x.MemoryBadgeTooltip, "Open a.md")
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
