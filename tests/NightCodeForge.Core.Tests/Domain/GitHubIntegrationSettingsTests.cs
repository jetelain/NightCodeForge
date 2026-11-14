using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class GitHubIntegrationSettingsTests
{
    [Theory]
    [InlineData("Acme", "NightCodeForge", "acme", "nightcodeforge", false)]
    [InlineData(" acme ", " repo ", "ACME", "REPO", false)]
    [InlineData(null, null, "acme", "repo", false)]
    [InlineData("", "", "acme", "repo", false)]
    [InlineData("acme", "repo-a", "acme", "repo-b", true)]
    [InlineData("org-one", "repo", "org-two", "repo", true)]
    [InlineData("acme", "repo", "", "", true)]
    public void HasRepositoryChanged_DistinguishesInitialSetupSameRepoAndRepoSwitch(
        string? prevOwner,
        string? prevName,
        string? currOwner,
        string? currName,
        bool expectedChanged)
    {
        var previous = new GitHubIntegrationSettings
        {
            RepositoryOwner = prevOwner,
            RepositoryName = prevName
        };
        var current = new GitHubIntegrationSettings
        {
            RepositoryOwner = currOwner,
            RepositoryName = currName
        };

        Assert.Equal(expectedChanged, GitHubIntegrationSettings.HasRepositoryChanged(previous, current));
    }

    [Fact]
    public void Clone_CopiesAllFieldsAndDeepCopiesTriagedIssueNumbers()
    {
        var original = new GitHubIntegrationSettings
        {
            Enabled = true,
            ClientId = "custom-client",
            RepositoryOwner = "acme",
            RepositoryName = "nightcodeforge",
            IssueTriageEnabled = true,
            IssueTriageScope = GitHubIssueTriageScope.AllOpenIssues,
            IssueFilterLabel = "triage",
            PollingIntervalMinutes = 30,
            TriagedIssueNumbers = [10, 20],
            PullRequestDeliveryEnabled = true,
            CreateAsDraft = true,
            CloseTaskOnPrMerge = false,
            TaskMirroringEnabled = true,
            CreateGitHubIssuesForLocalTasks = true,
            AutoAssignIssueOnStart = false,
            SyncStatusLabels = false,
            PostMilestoneComments = false
        };

        var cloned = original.Clone();
        cloned.TriagedIssueNumbers.Add(30);
        cloned.RepositoryName = "changed";

        Assert.Equal("acme", cloned.RepositoryOwner);
        Assert.True(cloned.CreateAsDraft);
        Assert.False(cloned.CloseTaskOnPrMerge);
        Assert.True(cloned.CreateGitHubIssuesForLocalTasks);
        Assert.Equal([10, 20], original.TriagedIssueNumbers);
        Assert.Equal("nightcodeforge", original.RepositoryName);
    }

    [Theory]
    [InlineData(GitHubClaimConflictConstants.OverrideClaimOption, true)]
    [InlineData("override", true)]
    [InlineData("Do not cancel, override claim please", true)]
    [InlineData("don't override", false)]
    [InlineData("do not override", false)]
    [InlineData("never override", false)]
    [InlineData("cancel override", false)]
    [InlineData(GitHubClaimConflictConstants.CancelTaskOption, false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void IsOverrideAnswer_RespectsWordBoundariesAndNegations(string? answer, bool expected)
    {
        Assert.Equal(expected, GitHubClaimConflictConstants.IsOverrideAnswer(answer));
    }

    [Theory]
    [InlineData(GitHubClaimConflictConstants.CancelTaskOption, true)]
    [InlineData("cancel", true)]
    [InlineData("Please cancel task", true)]
    [InlineData("don't cancel", false)]
    [InlineData("do not cancel, override instead", false)]
    [InlineData("never cancel", false)]
    [InlineData(GitHubClaimConflictConstants.OverrideClaimOption, false)]
    [InlineData(null, false)]
    public void IsCancelAnswer_RespectsWordBoundariesAndNegations(string? answer, bool expected)
    {
        Assert.Equal(expected, GitHubClaimConflictConstants.IsCancelAnswer(answer));
    }
}
