namespace NightCodeForge.Web.Services;

public record InlineDiffComment(
    Guid Id,
    string FilePath,
    int LineNumber,
    string LineContent,
    string CommentText,
    DateTime CreatedAt);

public class ReviewCommentStateService
{
    private readonly List<InlineDiffComment> _comments = new();

    public IReadOnlyList<InlineDiffComment> Comments => _comments;
    public int CommentCount => _comments.Count;

    public event Action? OnCommentsChanged;

    public void AddComment(string filePath, int lineNumber, string lineContent, string commentText)
    {
        var comment = new InlineDiffComment(
            Guid.NewGuid(),
            filePath,
            lineNumber,
            lineContent,
            commentText.Trim(),
            DateTime.UtcNow);

        _comments.Add(comment);
        OnCommentsChanged?.Invoke();
    }

    public void RemoveComment(Guid id)
    {
        _comments.RemoveAll(c => c.Id == id);
        OnCommentsChanged?.Invoke();
    }

    public void Clear()
    {
        _comments.Clear();
        OnCommentsChanged?.Invoke();
    }

    /// <summary>
    /// Compiles overall notes and inline line comments into structured Markdown for the implementation agent.
    /// </summary>
    public string FormatAggregatedFeedback(string? generalNotes)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(generalNotes))
        {
            sb.AppendLine("### Reviewer Overall Instructions:");
            sb.AppendLine(generalNotes.Trim());
            sb.AppendLine();
        }

        if (_comments.Any())
        {
            sb.AppendLine("### Inline Code Review Comments:");
            var grouped = _comments.GroupBy(c => c.FilePath);

            foreach (var group in grouped)
            {
                sb.AppendLine($"#### File: `{group.Key}`");
                foreach (var comment in group.OrderBy(c => c.LineNumber))
                {
                    sb.AppendLine($"- **Line {comment.LineNumber}**:");
                    sb.AppendLine($"  > `{comment.LineContent.Trim()}`");
                    sb.AppendLine($"  *Reviewer Comment:* {comment.CommentText}");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString().Trim();
    }
}
