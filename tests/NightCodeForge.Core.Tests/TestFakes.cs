using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class FakeTaskJobQueue : ITaskJobQueue
{
    public List<int> UrgentQueue { get; } = new();
    public List<int> InferenceQueue { get; } = new();

    public string EnqueueUrgent(int taskId)
    {
        UrgentQueue.Add(taskId);
        return $"job-urgent-{taskId}-{Guid.NewGuid():N}";
    }

    public string EnqueueInference(int taskId)
    {
        InferenceQueue.Add(taskId);
        return $"job-inference-{taskId}-{Guid.NewGuid():N}";
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

    public Task SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default)
    {
        SquashMerges.Add((taskId, baseBranch, commitMessage));
        return Task.CompletedTask;
    }

    public Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
    {
        CheckedOutBranches.Add((taskId, baseBranch));
        return Task.FromResult(GetWorktreePath(workspace, taskId));
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
    public AgentMode RequiredMode { get; set; } = AgentMode.Full;
    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public TestWorkflowStage(string stageId, WorkflowStageKind kind = WorkflowStageKind.Agentic, AgentMode mode = AgentMode.Full)
    {
        StageId = stageId;
        Kind = kind;
        RequiredMode = mode;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null) return await Handler(context, cancellationToken);
        return StageExecutionResult.Success();
    }
}
