using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Web.Components.Review;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DiffViewerTests : BunitContext
{
    private readonly ReviewCommentStateService _commentState;

    public DiffViewerTests()
    {
        _commentState = new ReviewCommentStateService();
        Services.AddSingleton(_commentState);
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
    }

    [Fact]
    public void DiffViewer_RendersSideBySideByDefault_AndTogglesToUnified()
    {
        var file = new FileDiffSummary(
            "src/Math.cs",
            FileChangeType.Modified,
            1,
            1,
            "int a = 1;\nint b = 2;",
            "int a = 1;\nint b = 3;");

        var cut = Render<DiffViewer>(parameters => parameters
            .Add(p => p.File, file));

        Assert.Contains("src/Math.cs", cut.Markup);
        Assert.NotNull(cut.Find(".diff-table-sbs"));
        Assert.Null(cut.FindAll(".diff-table-unified").FirstOrDefault());

        // Click Unified button
        cut.Find("#btn-diff-unified").Click();

        Assert.NotNull(cut.Find(".diff-table-unified"));
        Assert.Null(cut.FindAll(".diff-table-sbs").FirstOrDefault());
    }

    [Fact]
    public void DiffViewer_ClickingAddComment_OpensCommentBoxAndSavesComment()
    {
        var file = new FileDiffSummary(
            "src/Math.cs",
            FileChangeType.Modified,
            1,
            1,
            "int a = 1;",
            "int a = 2;");

        var cut = Render<DiffViewer>(parameters => parameters
            .Add(p => p.File, file));

        // Find the add comment button on a line
        var addBtn = cut.Find(".btn-add-comment");
        addBtn.Click();

        // Verify textarea is now rendered
        var textarea = cut.Find("textarea");
        Assert.NotNull(textarea);

        // Enter comment text
        textarea.Change("Please make this configurable.");

        // Click Save Comment
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Comment"));
        saveBtn.Click();

        // Verify comment is saved in state
        Assert.Equal(1, _commentState.CommentCount);
        Assert.Equal("Please make this configurable.", _commentState.Comments[0].CommentText);
        Assert.Equal("src/Math.cs", _commentState.Comments[0].FilePath);

        // Verify comment bubble is rendered in the viewer
        Assert.Contains("Please make this configurable.", cut.Markup);

        // Click delete comment button
        var deleteBtn = cut.Find(".btn-icon");
        deleteBtn.Click();

        Assert.Equal(0, _commentState.CommentCount);
    }
}
