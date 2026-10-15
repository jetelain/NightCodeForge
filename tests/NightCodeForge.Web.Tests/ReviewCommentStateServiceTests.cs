using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ReviewCommentStateServiceTests
{
    [Fact]
    public void AddComment_AddsCommentAndTriggersEvent()
    {
        var service = new ReviewCommentStateService();
        bool eventFired = false;
        service.OnCommentsChanged += () => eventFired = true;

        service.AddComment("src/Services/Math.cs", 42, "public int Add(int a, int b)", "Add parameter validation");

        Assert.True(eventFired);
        Assert.Equal(1, service.CommentCount);
        var comment = service.Comments[0];
        Assert.Equal("src/Services/Math.cs", comment.FilePath);
        Assert.Equal(42, comment.LineNumber);
        Assert.Equal("public int Add(int a, int b)", comment.LineContent);
        Assert.Equal("Add parameter validation", comment.CommentText);
    }

    [Fact]
    public void RemoveComment_RemovesSpecificComment()
    {
        var service = new ReviewCommentStateService();
        service.AddComment("file1.cs", 10, "line 10", "Comment 1");
        service.AddComment("file2.cs", 20, "line 20", "Comment 2");

        var firstId = service.Comments[0].Id;
        service.RemoveComment(firstId);

        Assert.Equal(1, service.CommentCount);
        Assert.Equal("file2.cs", service.Comments[0].FilePath);
    }

    [Fact]
    public void Clear_RemovesAllComments()
    {
        var service = new ReviewCommentStateService();
        service.AddComment("file1.cs", 10, "line 10", "Comment 1");
        service.AddComment("file2.cs", 20, "line 20", "Comment 2");

        service.Clear();

        Assert.Equal(0, service.CommentCount);
        Assert.Empty(service.Comments);
    }

    [Fact]
    public void FormatAggregatedFeedback_GeneralNotesOnly()
    {
        var service = new ReviewCommentStateService();
        var result = service.FormatAggregatedFeedback("Please revise the authentication architecture.");

        Assert.Contains("### Reviewer Overall Instructions:", result);
        Assert.Contains("Please revise the authentication architecture.", result);
        Assert.DoesNotContain("### Inline Code Review Comments:", result);
    }

    [Fact]
    public void FormatAggregatedFeedback_InlineCommentsOnly_FormatsGroupedByFile()
    {
        var service = new ReviewCommentStateService();
        service.AddComment("src/Auth.cs", 50, "if (token == null)", "Ensure we log warning here");
        service.AddComment("src/Auth.cs", 25, "private string _secret;", "Use SecureString or Vault");
        service.AddComment("src/Logger.cs", 12, "public void Log()", "Add level parameter");

        var result = service.FormatAggregatedFeedback(null);

        Assert.DoesNotContain("### Reviewer Overall Instructions:", result);
        Assert.Contains("### Inline Code Review Comments:", result);
        Assert.Contains("#### File: `src/Auth.cs`", result);
        Assert.Contains("#### File: `src/Logger.cs`", result);

        // Check line ordering in Auth.cs (line 25 before line 50)
        int idxLine25 = result.IndexOf("**Line 25**", StringComparison.Ordinal);
        int idxLine50 = result.IndexOf("**Line 50**", StringComparison.Ordinal);
        Assert.True(idxLine25 < idxLine50);

        Assert.Contains("`private string _secret;`", result);
        Assert.Contains("*Reviewer Comment:* Use SecureString or Vault", result);
    }

    [Fact]
    public void FormatAggregatedFeedback_BothNotesAndInlineComments()
    {
        var service = new ReviewCommentStateService();
        service.AddComment("src/Math.cs", 42, "return a + b;", "Guard against overflow");

        var result = service.FormatAggregatedFeedback("Check edge cases with int.MaxValue");

        Assert.Contains("### Reviewer Overall Instructions:", result);
        Assert.Contains("Check edge cases with int.MaxValue", result);
        Assert.Contains("### Inline Code Review Comments:", result);
        Assert.Contains("#### File: `src/Math.cs`", result);
        Assert.Contains("- **Line 42**:", result);
        Assert.Contains("Guard against overflow", result);
    }
}
