using System.Runtime.InteropServices;
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
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<StartupRecoveryService> _logger;

    public StartupRecoveryService(
        IWorkspaceRepository workspaceRepository,
        IIssueRepository issueRepository,
        IGitCliRunner gitCliRunner,
        IWorkflowPipelineProvider pipelineProvider,
        IIssueJobQueue jobQueue,
        IWorkspacePathResolver? pathResolver = null,
        ILogger<StartupRecoveryService>? logger = null)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _issueRepository = issueRepository ?? throw new ArgumentNullException(nameof(issueRepository));
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _pathResolver = pathResolver;
        _logger = logger ?? NullLogger<StartupRecoveryService>.Instance;
    }

    public async Task ReconcileWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Beginning startup recovery and workspace reconciliation...");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            VerifyWslAvailability();
        }

        var workspaces = await _workspaceRepository.GetAllAsync(cancellationToken);

        foreach (var workspace in workspaces)
        {
            var pathsToReconcile = new List<string>();
            if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
            {
                pathsToReconcile.Add(workspace.RepositoryPath);
            }

            if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
            {
                pathsToReconcile.Add(workspace.Ext4MirrorPath);
            }

            foreach (var path in pathsToReconcile)
            {
                // 1. Purge stale .git/*.lock files
                PurgeStaleGitLocks(path, workspace.Id);

                // 2. Rescue uncommitted working tree
                await RescueUncommittedChangesAsync(path, cancellationToken);
            }
        }

        // 3. Reconcile active issues left interrupted by restart (Open, Reviewed, Implemented without WaitingForHuman)
        var activeMilestones = new[] { IssueOverallStatus.Open, IssueOverallStatus.Reviewed, IssueOverallStatus.Implemented };
        var reconciledCount = 0;

        foreach (var status in activeMilestones)
        {
            var activeIssues = await _issueRepository.GetByStatusAsync(status, cancellationToken);
            foreach (var issue in activeIssues.Where(i => !i.IsWaitingForHuman))
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
                reconciledCount++;
            }
        }

        _logger.LogInformation("Startup workspace reconciliation completed. Reconciled {Count} active issues.", reconciledCount);
    }

    private void PurgeStaleGitLocks(string repoPath, int workspaceId)
    {
        try
        {
            var effectivePath = repoPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (repoPath.StartsWith('/') || repoPath.StartsWith('~')))
            {
                effectivePath = _pathResolver?.ResolveCruciblePath(workspaceId, string.Empty) ?? repoPath;
            }

            var gitDir = Path.Combine(effectivePath, ".git");
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
    private void VerifyWslAvailability()
    {
        try
        {
            var wslPath = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (!File.Exists(wslPath))
            {
                _logger.LogWarning("⚠️ Windows WSL2 Requirement: wsl.exe was not found in system directory. WSL2 is mandatory for Unix agent execution. Please run 'wsl --install' in an administrative terminal.");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = wslPath,
                Arguments = "--status",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    _logger.LogWarning("⚠️ Windows WSL2 Requirement: 'wsl --status' timed out. Ensure WSL2 is responsive.");
                }
                else if (process.ExitCode != 0)
                {
                    _logger.LogWarning("⚠️ Windows WSL2 Requirement: 'wsl --status' exited with code {ExitCode}. If WSL2 is not installed, run 'wsl --install'.", process.ExitCode);
                }
                else
                {
                    _logger.LogInformation("Windows WSL2 environment verified and operational.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Windows WSL2 check failed: {Message}. Please ensure WSL2 is installed ('wsl --install').", ex.Message);
        }
    }
}
