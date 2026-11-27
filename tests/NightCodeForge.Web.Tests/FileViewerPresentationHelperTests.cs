using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class FileViewerPresentationHelperTests
{
    [Fact]
    public void BuildFileViewerUrl_EncodesParametersAndAppendsFragment()
    {
        var url = FileViewerPresentationHelper.BuildFileViewerUrl(
            workspaceId: 9,
            gitRef: "feature/task-42",
            path: "docs/my file.md",
            taskId: 42,
            lineRange: new FileLineRange(12, 34));

        Assert.Equal("/w/9/files?ref=feature%2Ftask-42&path=docs%2Fmy%20file.md&taskId=42#L12-L34", url);
    }

    [Fact]
    public void BuildBreadcrumbs_BuildsRootAndNestedSegments()
    {
        var crumbs = FileViewerPresentationHelper.BuildBreadcrumbs("src/Core/Foo.cs", "MyRepo");

        Assert.Equal(4, crumbs.Count);
        Assert.Equal(new FileBreadcrumbSegment("MyRepo", "", false), crumbs[0]);
        Assert.Equal(new FileBreadcrumbSegment("src", "src", false), crumbs[1]);
        Assert.Equal(new FileBreadcrumbSegment("Core", "src/Core", false), crumbs[2]);
        Assert.Equal(new FileBreadcrumbSegment("Foo.cs", "src/Core/Foo.cs", true), crumbs[3]);
    }

    [Fact]
    public void GetAncestorDirectoryPaths_ReturnsRootAndParents()
    {
        var ancestors = FileViewerPresentationHelper.GetAncestorDirectoryPaths("src/Core/Sub/Foo.cs");
        Assert.Equal(["", "src", "src/Core", "src/Core/Sub"], ancestors);
    }

    [Fact]
    public void BuildVisibleTreeNodes_ExpandsOnlyExpandedDirectoriesAndMarksActiveAndAncestors()
    {
        var loaded = new Dictionary<string, IReadOnlyList<GitTreeEntry>>
        {
            [""] =
            [
                new GitTreeEntry(GitTreeEntryKind.Directory, "src", "src", null, "040000", "aaa"),
                new GitTreeEntry(GitTreeEntryKind.File, "README.md", "README.md", 12, "100644", "bbb")
            ],
            ["src"] =
            [
                new GitTreeEntry(GitTreeEntryKind.File, "Program.cs", "src/Program.cs", 40, "100644", "ccc")
            ]
        };

        var expanded = new HashSet<string>(StringComparer.Ordinal) { "src" };
        var nodes = FileViewerPresentationHelper.BuildVisibleTreeNodes(loaded, expanded, "src/Program.cs");

        Assert.Equal(3, nodes.Count);
        Assert.Equal("src", nodes[0].Entry.Path);
        Assert.Equal(0, nodes[0].Depth);
        Assert.True(nodes[0].IsExpanded);
        Assert.True(nodes[0].IsAncestorOfActive);
        Assert.False(nodes[0].IsActive);

        Assert.Equal("src/Program.cs", nodes[1].Entry.Path);
        Assert.Equal(1, nodes[1].Depth);
        Assert.True(nodes[1].IsActive);

        Assert.Equal("README.md", nodes[2].Entry.Path);
        Assert.Equal(0, nodes[2].Depth);
    }

    [Fact]
    public void SplitLines_HandlesEmptyTrailingNewlineAndCrLf()
    {
        Assert.Empty(FileViewerPresentationHelper.SplitLines(""));
        Assert.Equal(["a", "b"], FileViewerPresentationHelper.SplitLines("a\r\nb\r\n"));
        Assert.Equal(["a", "", "c"], FileViewerPresentationHelper.SplitLines("a\n\nc"));
    }

    [Fact]
    public void FormattingHelpers_ReturnExpectedLabelsAndSizes()
    {
        Assert.True(FileViewerPresentationHelper.IsMarkdownFile("docs/PLAN.MD"));
        Assert.False(FileViewerPresentationHelper.IsMarkdownFile("src/Program.cs"));
        Assert.Equal("C#", FileViewerPresentationHelper.DetectLanguageLabel("src/Program.cs"));
        Assert.Equal("512 B", FileViewerPresentationHelper.FormatByteSize(512));
        Assert.Equal("2.0 KB", FileViewerPresentationHelper.FormatByteSize(2048));
        Assert.Equal("abcdef12", FileViewerPresentationHelper.ShortSha("abcdef1234567890"));
        Assert.Equal("1 line", FileViewerPresentationHelper.FormatLineCountLabel(1));
        Assert.Equal("42 lines", FileViewerPresentationHelper.FormatLineCountLabel(42));
        Assert.Equal("develop", FileViewerPresentationHelper.ResolveEffectiveRef(null, "develop"));
        Assert.Equal("main", FileViewerPresentationHelper.ResolveEffectiveRef(null, null));
        Assert.Equal("feature/x", FileViewerPresentationHelper.ResolveEffectiveRef(" feature/x ", "main"));
        Assert.Equal("/w/1/files?ref=main", FileViewerPresentationHelper.StripFragment("/w/1/files?ref=main#L12"));
        Assert.Equal("padding-left: 2.25rem;", FileViewerPresentationHelper.FormatTreeRowIndentStyle(2));
        Assert.Equal(
            FileViewerPresentationHelper.BuildTargetQueryKey(1, "main", "a.cs", 5),
            FileViewerPresentationHelper.BuildTargetQueryKey(1, "main", "a.cs", 5));
    }
}
