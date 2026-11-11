namespace NightCodeForge.Core.Domain;

/// <summary>
/// Scope defining which open GitHub issues are eligible for triage ingestion.
/// </summary>
public enum GitHubIssueTriageScope
{
    AssignedToMeOrUnassigned = 0,
    AssignedToMeOnly = 1,
    AllOpenIssues = 2
}

/// <summary>
/// Per-workspace GitHub integration configuration.
/// Credentials and access tokens are strictly excluded and managed separately via the OS keyring token store (Pmad.KeySharp).
/// </summary>
public class GitHubIntegrationSettings
{
    public const string DefaultClientId = "Iv23lienpd2ZF3rkKyfg";

    public bool Enabled { get; set; } = false;
    public string ClientId { get; set; } = DefaultClientId;
    public string? RepositoryOwner { get; set; }
    public string? RepositoryName { get; set; }

    // Sub-feature A: Untrusted Issue Intake & Triage (Step 12.11C)
    public bool IssueTriageEnabled { get; set; } = false;
    public GitHubIssueTriageScope IssueTriageScope { get; set; } = GitHubIssueTriageScope.AssignedToMeOrUnassigned;
    public string? IssueFilterLabel { get; set; }
    public int PollingIntervalMinutes { get; set; } = 15;
    public List<int> TriagedIssueNumbers { get; set; } = new();

    // Sub-feature B: PR Delivery (Step 12.11B)
    public bool PullRequestDeliveryEnabled { get; set; } = false;
    public bool CreateAsDraft { get; set; } = false;
    public bool CloseTaskOnPrMerge { get; set; } = true;

    // Sub-feature C: Task Mirroring & Multi-User Coordination (Step 12.11D)
    public bool TaskMirroringEnabled { get; set; } = false;
    public bool CreateGitHubIssuesForLocalTasks { get; set; } = false;
    public bool AutoAssignIssueOnStart { get; set; } = true;
    public bool SyncStatusLabels { get; set; } = true;
    public bool PostMilestoneComments { get; set; } = true;
}

