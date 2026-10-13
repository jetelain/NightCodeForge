namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a dependency relationship where IssueId depends on DependsOnIssueId.
/// </summary>
public class IssueDependency
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public int DependsOnIssueId { get; set; }

    public Issue? Issue { get; set; }
    public Issue? DependsOnIssue { get; set; }
}
