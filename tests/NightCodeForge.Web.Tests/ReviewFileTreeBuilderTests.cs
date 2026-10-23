using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ReviewFileTreeBuilderTests
{
    [Fact]
    public void BuildTree_WithEmptyList_ReturnsEmpty()
    {
        var result = ReviewFileTreeBuilder.BuildTree(null);
        Assert.Empty(result);

        result = ReviewFileTreeBuilder.BuildTree(Array.Empty<FileDiffSummary>());
        Assert.Empty(result);
    }

    [Fact]
    public void BuildTree_SingleChildDirectories_CompressesChain()
    {
        var files = new List<FileDiffSummary>
        {
            new("src/NightCodeForge.Web/Components/Pages/Review.razor", FileChangeType.Modified, 10, 2, "", ""),
            new("src/NightCodeForge.Web/Components/Pages/Inbox.razor", FileChangeType.Modified, 5, 1, "", "")
        };

        var tree = ReviewFileTreeBuilder.BuildTree(files);

        // Expected compressed directory: "src/NightCodeForge.Web/Components/Pages"
        Assert.Single(tree);
        var topDir = tree[0];
        Assert.True(topDir.IsDirectory);
        Assert.Equal("src/NightCodeForge.Web/Components/Pages", topDir.Name);
        Assert.Equal(2, topDir.Children.Count);

        // Files inside are sorted alphabetically
        Assert.Equal("Inbox.razor", topDir.Children[0].Name);
        Assert.Equal("Review.razor", topDir.Children[1].Name);
        Assert.False(topDir.Children[0].IsDirectory);
        Assert.False(topDir.Children[1].IsDirectory);

        // Totals calculated
        Assert.Equal(15, topDir.TotalAdditions);
        Assert.Equal(3, topDir.TotalDeletions);
    }

    [Fact]
    public void BuildTree_MultipleBranches_KeepsBranchingNodes()
    {
        var files = new List<FileDiffSummary>
        {
            new("src/Core/Parser.cs", FileChangeType.Modified, 3, 0, "", ""),
            new("src/Web/App.razor", FileChangeType.Added, 20, 0, "", "")
        };

        var tree = ReviewFileTreeBuilder.BuildTree(files);

        // Top level: "src" has 2 children: "Core" and "Web"
        Assert.Single(tree);
        var srcDir = tree[0];
        Assert.Equal("src", srcDir.Name);
        Assert.Equal(2, srcDir.Children.Count);

        Assert.Equal("Core", srcDir.Children[0].Name);
        Assert.Single(srcDir.Children[0].Children);
        Assert.Equal("Parser.cs", srcDir.Children[0].Children[0].Name);

        Assert.Equal("Web", srcDir.Children[1].Name);
        Assert.Single(srcDir.Children[1].Children);
        Assert.Equal("App.razor", srcDir.Children[1].Children[0].Name);
    }
}
