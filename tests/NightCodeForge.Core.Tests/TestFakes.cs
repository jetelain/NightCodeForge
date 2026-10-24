using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class FakeTaskJobQueue : ITaskJobQueue
{
    public List<int> UrgentQueue { get; } = new();
    public List<int> CodeReviewQueue { get; } = new();
    public List<int> ImplementationQueue { get; } = new();
    public List<int> TaskReviewQueue { get; } = new();
    public List<int> InferenceQueue { get; } = new();
    public List<(int TaskId, StageQueueTier Tier, int Priority)> EnqueuedStages { get; } = new();

    public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
    {
        UrgentQueue.Add(taskId);
        return $"job-urgent-{taskId}-{Guid.NewGuid():N}";
    }

    public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
    {
        EnqueuedStages.Add((taskId, tier, priority));
        if (priority <= TaskPriority.Critical)
        {
            UrgentQueue.Add(taskId);
            return $"job-urgent-{taskId}-{Guid.NewGuid():N}";
        }

        InferenceQueue.Add(taskId);
        switch (tier)
        {
            case StageQueueTier.CodeReview:
                CodeReviewQueue.Add(taskId);
                return $"job-cr-{taskId}-{Guid.NewGuid():N}";
            case StageQueueTier.Implementation:
                ImplementationQueue.Add(taskId);
                return $"job-impl-{taskId}-{Guid.NewGuid():N}";
            case StageQueueTier.TaskReview:
                TaskReviewQueue.Add(taskId);
                return $"job-tr-{taskId}-{Guid.NewGuid():N}";
            default:
                throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown queue tier");
        }
    }

    public List<string> DeletedJobs { get; } = new();

    public bool Delete(string jobId)
    {
        DeletedJobs.Add(jobId);
        return true;
    }
}

public class FakeGitWorkspaceManager : IGitWorkspaceManager
{
    public List<(int TaskId, string BaseBranch)> CheckedOutBranches { get; } = new();
    public List<(int TaskId, string StageName, string Summary)> WipCheckpoints { get; } = new();
    public List<(int TaskId, string BaseBranch, string CommitMessage)> SquashMerges { get; } = new();
    public string GeneratedDiff { get; set; } = "diff --git a/file.cs b/file.cs\n+Added line";

    public Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        CheckedOutBranches.Add((taskId, baseBranch));
        return Task.CompletedTask;
    }

    public Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageName, string summary, CancellationToken cancellationToken = default)
    {
        WipCheckpoints.Add((taskId, stageName, summary));
        return Task.FromResult($"commit-sha-{WipCheckpoints.Count:D4}");
    }

    public Task<string> GenerateDiffAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GeneratedDiff);
    }

    public string GeneratedDiffStat { get; set; } = "file.cs | 2 +-\n 1 file changed, 1 insertion(+), 1 deletion(-)";

    public Task<string> GenerateDiffStatAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GeneratedDiffStat);
    }

    public Task<string> GenerateLiveDiffStatAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GeneratedDiffStat);
    }

    public Task<string> GenerateLiveDiffAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GeneratedDiff);
    }

    public Task<string> GetCommitDiffAsync(Workspace workspace, string commitSha, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GeneratedDiff);
    }

    public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default)
        => SquashAndMergeAsync(workspace, taskId, baseBranch, commitMessage, false, cancellationToken);

    public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, bool allowCreateBaseBranch, CancellationToken cancellationToken = default)
    {
        SquashMerges.Add((taskId, baseBranch, commitMessage));
        return Task.FromResult($"squash-sha-{taskId:D4}");
    }

    public Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        CheckedOutBranches.Add((taskId, baseBranch));
        return Task.FromResult(GetWorktreePath(workspace, taskId));
    }

    public List<int> NormalizedTaskIds { get; } = new();
    public Exception? NormalizeFailure { get; set; }

    public Task EnsureWorktreeRelativePathsAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        NormalizedTaskIds.Add(taskId);
        if (NormalizeFailure is not null) throw NormalizeFailure;
        return Task.CompletedTask;
    }

    public Task RemoveWorktreeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public string GetWorktreePath(Workspace workspace, int taskId)
    {
        return $"{workspace.RepositoryPath}/task-{taskId}";
    }

    public Task RevertOrCleanAsync(Workspace workspace, int? taskId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public MergeSyncResult SyncResult { get; set; } = new(Success: true, HasConflict: false, ConflictedFiles: Array.Empty<string>(), MergeCommitSha: "sync-sha-0001");
    public MergeabilityCheckResult MergeabilityResult { get; set; } = new(IsMergeable: true, ConflictedFiles: Array.Empty<string>(), BaseBranchSha: "base-sha", FeatureBranchSha: "feat-sha");

    public Task ValidateGitVersionAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<MergeSyncResult> SyncFromBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SyncResult);
    }

    public Task<MergeabilityCheckResult> CheckMergeableAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MergeabilityResult);
    }

    public Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<string> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("branch-sha");
    }

    public Task ResetCanonicalBranchAsync(Workspace workspace, string branchName, string targetSha, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public class FakeGitCliRunner : IGitCliRunner
{
    public List<FakeGitExecutedCommand> ExecutedCommands { get; } = new();
    public Func<string, string, GitCommandResult>? Handler { get; set; }
    public Func<string, IReadOnlyList<string>, GitCommandResult>? ListHandler { get; set; }

    public Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var list = argumentsList.ToList();
        ExecutedCommands.Add(new FakeGitExecutedCommand(workingDirectory, list));
        if (ListHandler != null)
        {
            return Task.FromResult(ListHandler(workingDirectory, list));
        }

        if (Handler != null)
        {
            return Task.FromResult(Handler(workingDirectory, string.Join(" ", list)));
        }

        return Task.FromResult(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));
    }
}

public record FakeGitExecutedCommand(string WorkingDirectory, IReadOnlyList<string> ArgumentsList)
{
    public string Arguments => string.Join(" ", ArgumentsList);

    public void Deconstruct(out string workingDirectory, out string arguments)
    {
        workingDirectory = WorkingDirectory;
        arguments = Arguments;
    }

    public static implicit operator (string WorkingDirectory, string Arguments)(FakeGitExecutedCommand cmd)
        => (cmd.WorkingDirectory, cmd.Arguments);
}

public class TestWorkflowStage : IWorkflowStage
{
    public string StageId { get; set; }
    public string DisplayName => StageId;
    public WorkflowStageKind Kind { get; set; } = WorkflowStageKind.Agentic;
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
    public AgentMode RequiredMode { get; set; } = AgentMode.Full;
    public string? PromptTemplate { get; set; }
    public string? LoopBackStageId { get; set; }
    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public TestWorkflowStage(
        string stageId,
        WorkflowStageKind kind = WorkflowStageKind.Agentic,
        AgentMode mode = AgentMode.Full,
        string? promptTemplate = null)
    {
        StageId = stageId;
        Kind = kind;
        RequiredMode = mode;
        PromptTemplate = promptTemplate ?? "Execute stage '{{ stage.id }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}{{ if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{ end }}{{ if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}Q: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}{{ end }}";
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null) return await Handler(context, cancellationToken);
        return StageExecutionResult.Success();
    }
}
