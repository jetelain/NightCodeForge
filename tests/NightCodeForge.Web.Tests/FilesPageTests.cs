using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class FilesPageTests : BunitContext
{
    private const int WorkspaceId = 4;

    private readonly Mock<IGitRepositoryBrowserService> _browserMock = new();
    private readonly WorkspaceStateService _workspaceState = new();

    public FilesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_browserMock.Object);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ISyntaxHighlightingService, SimpleSyntaxHighlighter>();
        Services.AddSingleton(TestServices.CreateToastService());

        _workspaceState.SetActiveWorkspace(new Workspace
        {
            Id = WorkspaceId,
            Name = "NightCodeForge",
            RepositoryPath = "/repos/ncf",
            DefaultBaseBranch = "main"
        });

        _browserMock.Setup(b => b.ListBranchesAsync(WorkspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["main", "feature/task-42"]);
    }

    private static GitResolvedRef Resolved(string resolvedRef = "main", string sha = "1234567890abcdef", GitRefFallbackInfo? fallback = null) =>
        new(resolvedRef, sha, fallback);

    private void SetupRootAndSrcTree(GitResolvedRef resolvedRef)
    {
        IReadOnlyList<GitTreeEntry> rootEntries =
        [
            new GitTreeEntry(GitTreeEntryKind.Directory, "src", "src", null, "040000", "tree-src"),
            new GitTreeEntry(GitTreeEntryKind.File, "README.md", "README.md", 32, "100644", "blob-readme")
        ];
        IReadOnlyList<GitTreeEntry> srcEntries =
        [
            new GitTreeEntry(GitTreeEntryKind.File, "Service.cs", "src/Service.cs", 120, "100644", "blob-service")
        ];

        _browserMock.Setup(b => b.ListAncestorDirectoriesAsync(WorkspaceId, resolvedRef.CommitSha, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<GitTreeEntry>>
            {
                [string.Empty] = rootEntries,
                ["src"] = srcEntries
            });

        _browserMock.Setup(b => b.ListDirectoryAsync(WorkspaceId, resolvedRef.CommitSha, string.Empty, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitDirectoryListing(resolvedRef, string.Empty, rootEntries));
    }

    [Fact]
    public void FilesPage_NoActiveWorkspace_RendersWorkspaceEmptyStateHero()
    {
        using var emptyCtx = new BunitContext();
        emptyCtx.JSInterop.Mode = JSRuntimeMode.Loose;
        emptyCtx.Services.AddSingleton(_browserMock.Object);
        emptyCtx.Services.AddSingleton(new WorkspaceStateService());
        emptyCtx.Services.AddSingleton<ISyntaxHighlightingService, SimpleSyntaxHighlighter>();
        emptyCtx.Services.AddSingleton(TestServices.CreateToastService());

        var cut = emptyCtx.Render<Files>();

        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
        Assert.Empty(cut.FindAll("#files-toolbar"));
    }

    [Fact]
    public void FilesPage_CodeFileWithLineRangeFragment_HighlightsLinesAndScrollsIntoView()
    {
        var resolved = Resolved("main", "aaaa1111bbbb2222");
        SetupRootAndSrcTree(resolved);

        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);

        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "src/Service.cs", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved,
                "src/Service.cs",
                "Service.cs",
                GitFileContentKind.Text,
                120,
                "public class Service\n{\n    public int Run() => 42;\n}",
                4,
                null,
                null,
                null,
                null,
                null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=src%2FService.cs#L2-L3");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.NotNull(cut.Find("#files-code-viewer"));
        Assert.DoesNotContain("highlighted", cut.Find("#file-line-1").ClassName);
        Assert.Contains("highlighted", cut.Find("#file-line-2").ClassName);
        Assert.Contains("highlighted", cut.Find("#file-line-3").ClassName);
        Assert.DoesNotContain("highlighted", cut.Find("#file-line-4").ClassName);
        Assert.Contains("tok-keyword", cut.Find("#file-line-1").InnerHtml);

        JSInterop.VerifyInvoke(FilesUiConstants.ScrollToLineJsFunction);

        // Tree should have expanded src and marked src/Service.cs active
        var activeNode = cut.Find("[data-testid=files-tree-node].active");
        Assert.Equal("src/Service.cs", activeNode.GetAttribute("data-entry-path"));
    }

    [Fact]
    public async Task FilesPage_LocationChangedFragmentOnly_UpdatesHighlightedRangeWithoutReloadingTree()
    {
        var resolved = Resolved("main", "aaaa1111bbbb2222");
        SetupRootAndSrcTree(resolved);

        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "src/Service.cs", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved,
                "src/Service.cs",
                "Service.cs",
                GitFileContentKind.Text,
                60,
                "line1\nline2\nline3\nline4",
                4,
                null,
                null,
                null,
                null,
                null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=src%2FService.cs#L1");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Contains("highlighted", cut.Find("#file-line-1").ClassName);
        _browserMock.Verify(b => b.ListAncestorDirectoriesAsync(WorkspaceId, resolved.CommitSha, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Click line 4 with Shift key to select L1-L4
        await cut.Find("#file-line-4 .file-line-num-btn").ClickAsync(new MouseEventArgs { ShiftKey = true });

        Assert.EndsWith("#L1-L4", nav.Uri);
        Assert.Contains("highlighted", cut.Find("#file-line-4").ClassName);

        // Fragment-only navigation via LocationChanged must NOT re-query Git directory tree
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=src%2FService.cs#L3");
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("highlighted", cut.Find("#file-line-1").ClassName);
            Assert.Contains("highlighted", cut.Find("#file-line-3").ClassName);
        });
        _browserMock.Verify(b => b.ListAncestorDirectoriesAsync(WorkspaceId, resolved.CommitSha, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FilesPage_MarkdownFile_DefaultsToRendered_AndAutoSwitchesToRawWhenAnchored()
    {
        var resolved = Resolved("main", "bbbb2222");
        SetupRootAndSrcTree(resolved);

        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "README.md", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved,
                "README.md",
                "README.md",
                GitFileContentKind.Text,
                48,
                "# Heading\n\n<script>alert(1)</script>\n\nLine 5",
                5,
                null,
                null,
                null,
                null,
                null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=README.md");

        var renderedCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        var mdHtml = renderedCut.Find("#files-markdown-rendered").InnerHtml;
        Assert.Contains("Heading", mdHtml);
        Assert.DoesNotContain("<script", mdHtml);

        // Toggle to Raw
        renderedCut.Find("#files-toggle-raw-btn").Click();
        Assert.NotEmpty(renderedCut.FindAll("#files-code-viewer"));

        // With #L5 anchor, opens directly in Raw mode with line 5 highlighted
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=README.md#L5");
        var anchoredCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.NotEmpty(anchoredCut.FindAll("#files-code-viewer"));
        Assert.Contains("highlighted", anchoredCut.Find("#file-line-5").ClassName);
    }

    [Fact]
    public void FilesPage_TaskFallbackAndDetachedCommit_RendersFallbackBannerAndDetachedPill()
    {
        var fallback = new GitRefFallbackInfo("feature/task-42", "deadbeef12345678", GitRefFallbackReason.SquashCommitSha, 42);
        var resolved = new GitResolvedRef("deadbeef12345678", "deadbeef12345678", fallback);
        SetupRootAndSrcTree(resolved);

        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "feature/task-42", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, string.Empty, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved,
                string.Empty,
                string.Empty,
                GitFileContentKind.Directory,
                null,
                null,
                null,
                null,
                null,
                [new GitTreeEntry(GitTreeEntryKind.Directory, "src", "src", null, "040000", "tree-src")],
                null,
                null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=feature%2Ftask-42&taskId=42");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        var banner = cut.Find("#files-fallback-banner");
        Assert.Contains("feature/task-42", banner.TextContent);
        Assert.Contains("deadbeef", banner.TextContent);
        Assert.Equal($"/w/{WorkspaceId}/tasks/42", cut.Find("#files-fallback-task-link").GetAttribute("href"));
        Assert.NotEmpty(cut.FindAll("#files-detached-ref-pill"));
        Assert.NotEmpty(cut.FindAll("#files-directory-table"));
    }

    [Fact]
    public void FilesPage_BinaryOversizedSymlinkSubmoduleAndMissingPath_RenderDegradedStatesAndKeepTreeUsable()
    {
        var resolved = Resolved("main", "cccc3333");
        SetupRootAndSrcTree(resolved);
        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);

        var nav = Services.GetRequiredService<NavigationManager>();

        // Binary
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "bin.dat", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(resolved, "bin.dat", "bin.dat", GitFileContentKind.Binary, 256, null, null, null, null, null, BinaryDetectionReason.NulByte, null));
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=bin.dat");
        var binCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Contains("Binary file preview omitted", binCut.Find("#files-binary-notice").TextContent);

        // Oversized
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "huge.log", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(resolved, "huge.log", "huge.log", GitFileContentKind.Oversized, 900_000, null, null, null, null, null, null, GitRepositoryBrowserConstants.MaxPreviewFileBytes));
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=huge.log");
        var bigCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Contains("exceeds preview size limit", bigCut.Find("#files-oversized-notice").TextContent);

        // Symlink
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "link.txt", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(resolved, "link.txt", "link.txt", GitFileContentKind.Symlink, 12, null, null, "../target.txt", null, null, null, null));
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=link.txt");
        var symCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Equal("../target.txt", symCut.Find("#files-symlink-target").TextContent);

        // Submodule
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "deps/sub", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(resolved, "deps/sub", "sub", GitFileContentKind.Submodule, null, null, null, null, "99887766", null, null, null));
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=deps%2Fsub");
        var subCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Equal("99887766", subCut.Find("#files-submodule-sha").TextContent);

        // Missing path preserves branch selector and tree
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "missing.cs", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitBrowserPathNotFoundException("missing.cs"));
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=missing.cs");
        var errCut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        Assert.Contains("missing.cs", errCut.Find("#files-error").TextContent);
        Assert.NotEmpty(errCut.FindAll("#files-ref-select"));
        Assert.NotEmpty(errCut.FindAll("[data-testid=files-tree-node]"));
    }

    [Fact]
    public void FilesPage_AnchoredMarkdownToggledToRendered_StaysRenderedAcrossParameterReRender()
    {
        var resolved = Resolved("main", "bbbb2222");
        SetupRootAndSrcTree(resolved);
        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "README.md", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved,
                "README.md",
                "README.md",
                GitFileContentKind.Text,
                40,
                "# Title\n\nLine 2\nLine 3\nLine 4\nLine 5",
                6,
                null,
                null,
                null,
                null,
                null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=README.md#L5");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.NotEmpty(cut.FindAll("#files-code-viewer"));
        cut.Find("#files-toggle-raw-btn").Click();
        Assert.NotEmpty(cut.FindAll("#files-markdown-rendered"));
        Assert.Empty(cut.FindAll("#files-code-viewer"));

        // Re-running OnParametersSetAsync with the same query must NOT force Markdown back to Raw
        cut.Render(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.NotEmpty(cut.FindAll("#files-markdown-rendered"));
        Assert.Empty(cut.FindAll("#files-code-viewer"));
    }

    [Fact]
    public async Task FilesPage_LinesQueryToFragmentClick_DoesNotReloadGitAndKeepsFragmentSyncWorking()
    {
        var resolved = Resolved("main", "aaaa1111bbbb2222");
        SetupRootAndSrcTree(resolved);
        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "src/Service.cs", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved,
                "src/Service.cs",
                "Service.cs",
                GitFileContentKind.Text,
                60,
                "line1\nline2\nline3\nline4",
                4,
                null,
                null,
                null,
                null,
                null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=src%2FService.cs&lines=2-3");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("highlighted", cut.Find("#file-line-2").ClassName);
        Assert.Contains("highlighted", cut.Find("#file-line-3").ClassName);
        _browserMock.Verify(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "src/Service.cs", null, It.IsAny<CancellationToken>()), Times.Once);

        // Click line 4 and simulate router re-rendering after ?lines= is replaced by #L4
        await cut.Find("#file-line-4 .file-line-num-btn").ClickAsync(new MouseEventArgs());
        cut.Render(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("highlighted", cut.Find("#file-line-4").ClassName);
        _browserMock.Verify(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "src/Service.cs", null, It.IsAny<CancellationToken>()), Times.Once);
        _browserMock.Verify(b => b.ListAncestorDirectoriesAsync(WorkspaceId, resolved.CommitSha, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Subsequent fragment-only navigation still updates highlighted line
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=src%2FService.cs#L1");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("highlighted", cut.Find("#file-line-1").ClassName);
            Assert.DoesNotContain("highlighted", cut.Find("#file-line-4").ClassName);
        });
    }

    [Fact]
    public void FilesPage_NavigatingToMissingRefClearsPreviousFallbackBannerAndDetachedPill()
    {
        var fallback = new GitRefFallbackInfo("feature/task-42", "deadbeef12345678", GitRefFallbackReason.SquashCommitSha, 42);
        var resolvedFallback = new GitResolvedRef("deadbeef12345678", "deadbeef12345678", fallback);
        SetupRootAndSrcTree(resolvedFallback);

        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "feature/task-42", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedFallback);
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolvedFallback.CommitSha, string.Empty, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolvedFallback, string.Empty, string.Empty, GitFileContentKind.Directory, null, null, null, null, null,
                [new GitTreeEntry(GitTreeEntryKind.Directory, "src", "src", null, "040000", "tree-src")], null, null));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=feature%2Ftask-42&taskId=42");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.NotEmpty(cut.FindAll("#files-fallback-banner"));
        Assert.NotEmpty(cut.FindAll("#files-detached-ref-pill"));

        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "missing-ref", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitBrowserRefNotFoundException("missing-ref"));

        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=missing-ref");
        cut.Render(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("missing-ref", cut.Find("#files-error").TextContent);
        Assert.Empty(cut.FindAll("#files-fallback-banner"));
        Assert.Empty(cut.FindAll("#files-detached-ref-pill"));
    }

    [Fact]
    public void FilesPage_InvalidPath_ShowsErrorWhileKeepingRootTreeNavigable()
    {
        var resolved = Resolved("main", "cccc3333");
        SetupRootAndSrcTree(resolved);
        _browserMock.Setup(b => b.ResolveRefAsync(WorkspaceId, "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        _browserMock.Setup(b => b.ListAncestorDirectoriesAsync(WorkspaceId, resolved.CommitSha, "../secret", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitBrowserInvalidInputException("Path '../secret' cannot contain parent directory traversal ('..')."));
        _browserMock.Setup(b => b.InspectPathAsync(WorkspaceId, resolved.CommitSha, "../secret", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitBrowserInvalidInputException("Path '../secret' cannot contain parent directory traversal ('..')."));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{WorkspaceId}/files?ref=main&path=..%2Fsecret");

        var cut = Render<Files>(p => p.Add(x => x.WorkspaceId, WorkspaceId));

        Assert.Contains("parent directory traversal", cut.Find("#files-error").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid=files-tree-node]"));
    }
}
