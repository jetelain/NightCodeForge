using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class FakeIssueJobQueue : IIssueJobQueue
{
    public List<int> UrgentQueue { get; } = new();
    public List<int> InferenceQueue { get; } = new();

    public string EnqueueUrgent(int issueId)
    {
        UrgentQueue.Add(issueId);
        return $"job-urgent-{issueId}-{Guid.NewGuid():N}";
    }

    public string EnqueueInference(int issueId)
    {
        InferenceQueue.Add(issueId);
        return $"job-inference-{issueId}-{Guid.NewGuid():N}";
    }
}

public class FakeGitWorkspaceManager : IGitWorkspaceManager
{
    public List<(int IssueId, string BaseBranch)> CheckedOutBranches { get; } = new();
    public List<(int IssueId, string StageName, string Summary)> WipCheckpoints { get; } = new();
    public List<(int IssueId, string BaseBranch, string CommitMessage)> SquashMerges { get; } = new();
    public string GeneratedDiff { get; set; } = "diff --git a/file.cs b/file.cs\n+Added line";

    public Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default)
    {
        CheckedOutBranches.Add((issueId, baseBranch));
        return Task.CompletedTask;
    }

    public Task<string> CreateWipCheckpointAsync(Workspace workspace, int issueId, string stageName, string summary, CancellationToken cancellationToken = default)
    {
        WipCheckpoints.Add((issueId, stageName, summary));
        return Task.FromResult($"commit-sha-{WipCheckpoints.Count:D4}");
    }

    public Task<string> GenerateDiffAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GeneratedDiff);
    }

    public Task SquashAndMergeAsync(Workspace workspace, int issueId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default)
    {
        SquashMerges.Add((issueId, baseBranch, commitMessage));
        return Task.CompletedTask;
    }

    public Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public class FakeGitCliRunner : IGitCliRunner
{
    public List<(string WorkingDirectory, string Arguments)> ExecutedCommands { get; } = new();
    public Func<string, string, GitCommandResult>? Handler { get; set; }

    public Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ExecutedCommands.Add((workingDirectory, arguments));
        if (Handler != null)
        {
            return Task.FromResult(Handler(workingDirectory, arguments));
        }

        return Task.FromResult(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));
    }
}

public class TestWorkflowStage : IWorkflowStage
{
    public string StageId { get; set; }
    public string DisplayName => StageId;
    public WorkflowStageKind Kind { get; set; } = WorkflowStageKind.Agentic;
    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public TestWorkflowStage(string stageId, WorkflowStageKind kind = WorkflowStageKind.Agentic)
    {
        StageId = stageId;
        Kind = kind;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null) return await Handler(context, cancellationToken);
        return StageExecutionResult.Success();
    }
}
