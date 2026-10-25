using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Web.Components.Review;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DiffWorkspaceTests : BunitContext
{
    public DiffWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ReviewCommentStateService>();
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
    }

    [Fact]
    public void DiffWorkspace_WhenEmpty_RendersEmptyMessage()
    {
        var cut = Render<DiffWorkspace>(p => p
            .Add(x => x.Files, Array.Empty<FileDiffSummary>())
            .Add(x => x.EmptyMessage, "No changes found."));

        Assert.Contains("No changes found.", cut.Markup);
        Assert.NotNull(cut.Find("#diff-workspace-grid"));
        Assert.Empty(cut.FindAll(".review-file-node"));
    }

    [Fact]
    public void DiffWorkspace_WithFiles_RendersFileTreeAndDiffViewer()
    {
        var files = new List<FileDiffSummary>
        {
            new("src/Core/App.cs", FileChangeType.Modified, 10, 2, "old", "new"),
            new("src/Core/Config.cs", FileChangeType.Added, 5, 0, "", "config")
        };

        FileDiffSummary? selected = null;
        var cut = Render<DiffWorkspace>(p => p
            .Add(x => x.Files, files)
            .Add(x => x.SelectedFileChanged, f => selected = f));

        Assert.NotNull(cut.Find("#diff-workspace-grid"));
        Assert.NotNull(cut.Find("#file-list"));
        Assert.NotNull(cut.Find("#diff-viewer-panel"));

        // Tree structure: directory node and file nodes
        var dirNodes = cut.FindAll(".review-dir-node");
        Assert.NotEmpty(dirNodes);

        var fileNodes = cut.FindAll(".review-file-node");
        Assert.Equal(2, fileNodes.Count);

        // First file is selected by default in DiffViewer
        Assert.Contains("src/Core/App.cs", cut.Find("#diff-viewer-panel").TextContent);

        // Click second file
        fileNodes[1].Click();
        Assert.NotNull(selected);
        Assert.Equal("src/Core/Config.cs", selected!.Path);
        Assert.Contains("src/Core/Config.cs", cut.Find("#diff-viewer-panel").TextContent);
    }

    [Fact]
    public void DiffWorkspace_PreservesDirectoryCollapsedState_AcrossFileSelection()
    {
        var files = new List<FileDiffSummary>
        {
            new("src/Core/App.cs", FileChangeType.Modified, 10, 2, "old", "new"),
            new("src/Core/Config.cs", FileChangeType.Added, 5, 0, "", "config")
        };

        var cut = Render<DiffWorkspace>(p => p.Add(x => x.Files, files));

        // Initially directory is expanded and children are visible
        Assert.NotEmpty(cut.FindAll(".review-dir-children"));
        Assert.Contains("▼", cut.Find(".review-dir-chevron").TextContent);

        // Click directory header to collapse it
        cut.Find(".review-dir-header").Click();
        Assert.Empty(cut.FindAll(".review-dir-children"));
        Assert.Contains("▶", cut.Find(".review-dir-chevron").TextContent);

        // Re-render parameters (e.g. parent state change)
        cut.Render(p => p.Add(x => x.Files, files));

        // Must still be collapsed
        Assert.Empty(cut.FindAll(".review-dir-children"));
        Assert.Contains("▶", cut.Find(".review-dir-chevron").TextContent);

        // Expand it again
        cut.Find(".review-dir-header").Click();
        Assert.NotEmpty(cut.FindAll(".review-dir-children"));
        Assert.Contains("▼", cut.Find(".review-dir-chevron").TextContent);
    }

    [Fact]
    public void DiffWorkspace_WhenParentClearsSelection_RendersNoFileSelectedMessage()
    {
        var files = new List<FileDiffSummary>
        {
            new("src/Core/App.cs", FileChangeType.Modified, 10, 2, "old", "new")
        };

        var cut = Render<DiffWorkspace>(p => p
            .Add(x => x.Files, files)
            .Add(x => x.SelectedFile, files[0]));

        Assert.Contains("src/Core/App.cs", cut.Find("#diff-viewer-panel").TextContent);

        // Parent explicitly clears selection
        cut.Render(p => p.Add(x => x.SelectedFile, null));

        Assert.Contains("Select a file on the left to inspect diff and add inline comments.", cut.Find("#diff-viewer-panel").TextContent);
    }
}
