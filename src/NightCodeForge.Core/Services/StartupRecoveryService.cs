using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Implements startup crash recovery: purges dangling Git locks, commits uncommitted dirty working trees,
/// and re-enqueues orphaned Running tasks to llm-urgent.
/// </summary>
public class StartupRecoveryService : IStartupRecoveryService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IIssueRepository _issueRepository;
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly IIssueJobQueue _jobQueue;
    private readonly ILogger<StartupRecoveryService> _logger;

    public StartupRecoveryService(
        IWorkspaceRepository workspaceRepository,
        IIssueRepository issueRepository,
        IGitCliRunner gitCliRunner,
        IWorkflowPipelineProvider pipelineProvider,
        IIssueJobQueue jobQueue,
        ILogger<StartupRecoveryService>? logger = null)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _issueRepository = issueRepository ?? throw new ArgumentNullException(nameof(issueRepository));
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _logger = logger ?? NullLogger<StartupRecoveryService>.Instance;
    }

    public async Task ReconcileWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Beginning startup recovery and workspace reconciliation...");

        var workspaces = await _workspaceRepository.GetAllAsync(cancellationToken);

        foreach (var workspace in workspaces)
        {
            if (string.IsNullOrWhiteSpace(workspace.RepositoryPath) || !Directory.Exists(workspace.RepositoryPath))
            {
                continue;
            }

            // 1. Purge stale .git/*.lock files
            PurgeStaleGitLocks(workspace.RepositoryPath);

            // 2. Rescue uncommitted working tree
            await RescueUncommittedChangesAsync(workspace.RepositoryPath, cancellationToken);
        }

        // 3. Reconcile issues left in Running status
        var runningIssues = await _issueRepository.GetByStatusAsync(IssueOverallStatus.Running, cancellationToken);
        foreach (var issue in runningIssues)
        {
            var pipeline = _pipelineProvider.GetPipeline(issue);
            var stage = (!string.IsNullOrWhiteSpace(issue.CurrentStageId)
                ? pipeline.GetStage(issue.CurrentStageId)
                : null) ?? pipeline.Stages.FirstOrDefault();

            var bag = PipelineDataBag.FromJson(issue.PipelineDataJson);

            if (stage != null && stage.Kind == WorkflowStageKind.Agentic)
            {
                // Agentic Stage: Rehydrate memory with recovery notice
                var lastCheckpoint = issue.Checkpoints?.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
                var memory = lastCheckpoint?.SerializedMemory ?? string.Empty;
                var notice = "[System Notice: Host restarted unexpectedly; workspace restored]";
                var restoredMemory = string.IsNullOrWhiteSpace(memory)
                    ? notice
                    : $"{memory}\n{notice}";

                bag.Set("SerializedMemory", restoredMemory);
                _logger.LogWarning("Rehydrated agentic session memory for orphaned issue #{IssueId}.", issue.Id);
            }
            else
            {
                // Deterministic Stage: Reset metrics and retries to execute freshly
                bag.Remove("DeterministicMetrics");
                _logger.LogWarning("Reset metrics for deterministic stage on orphaned issue #{IssueId}.", issue.Id);
            }

            issue.PipelineDataJson = bag.ToJson();
            issue.Status = IssueOverallStatus.Pending;

            await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
            await _issueRepository.AddExecutionLogAsync(new ExecutionLog
            {
                IssueId = issue.Id,
                Level = LogLevel.Warning,
                Message = $"Host reboot recovery: Re-enqueued issue #{issue.Id} to llm-urgent.",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            // Head-of-line priority re-enqueue
            _jobQueue.EnqueueUrgent(issue.Id);
        }

        _logger.LogInformation("Startup workspace reconciliation completed. Reconciled {Count} running issues.", runningIssues.Count);
    }

    private void PurgeStaleGitLocks(string repoPath)
    {
        try
        {
            var gitDir = Path.Combine(repoPath, ".git");
            if (!Directory.Exists(gitDir))
            {
                return;
            }

            var lockFiles = Directory.GetFiles(gitDir, "*.lock", SearchOption.AllDirectories);
            foreach (var lockFile in lockFiles)
            {
                try
                {
                    File.Delete(lockFile);
                    _logger.LogWarning("Purged stale Git lockfile: {LockFile}", lockFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete lockfile {LockFile}", lockFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning for lock files in {RepoPath}", repoPath);
        }
    }

    private async Task RescueUncommittedChangesAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var statusResult = await _gitCliRunner.ExecuteAsync(repoPath, "status --porcelain", cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
            {
                _logger.LogWarning("Dirty working tree detected in {RepoPath}. Creating rescue commit.", repoPath);
                await _gitCliRunner.ExecuteAsync(repoPath, "add -A", cancellationToken: cancellationToken);
                await _gitCliRunner.ExecuteAsync(
                    repoPath,
                    "commit -m \"WIP-RECOVERY: [Crash Recovery] Preserving uncommitted workspace changes\" --no-verify",
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rescue uncommitted changes in {RepoPath}", repoPath);
        }
    }
}
