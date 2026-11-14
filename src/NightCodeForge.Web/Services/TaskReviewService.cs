using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public interface ITaskReviewService
{
    Task<TaskReviewSnapshot?> GetSnapshotAsync(int taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskReviewSnapshot>> GetPendingReviewsAsync(int? workspaceId = null, CancellationToken cancellationToken = default);
    Task<TaskDiffResult> GetTaskDiffAsync(int taskId, CancellationToken cancellationToken = default);
    Task<bool> IsPullRequestDeliveryAvailableAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<GitHubPullRequest> CreateOrSyncPullRequestAsync(int taskId, CancellationToken cancellationToken = default);
    Task<CompleteTaskResult> CloseExternallyMergedTaskAsync(int taskId, CancellationToken cancellationToken = default);
    bool IsPendingReview(TaskReviewSnapshot snapshot);
    bool ShouldDisplayConflictResolvedBanner(TaskReviewSnapshot snapshot);
}

public partial class TaskReviewService : ITaskReviewService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IGitHubPullRequestDeliveryService _gitHubPrDeliveryService;
    private readonly ITaskOrchestrator _taskOrchestrator;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly ILogger<TaskReviewService> _logger;

    [GeneratedRegex(@"^diff --git a/(?<oldPath>.+?) b/(?<newPath>.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex DiffHeaderRegex();

    public TaskReviewService(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IGitWorkspaceManager gitWorkspaceManager,
        IGitHubPullRequestDeliveryService gitHubPrDeliveryService,
        ITaskOrchestrator taskOrchestrator,
        ITaskMarkdownSyncService markdownSyncService,
        ILogger<TaskReviewService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _gitHubPrDeliveryService = gitHubPrDeliveryService ?? throw new ArgumentNullException(nameof(gitHubPrDeliveryService));
        _taskOrchestrator = taskOrchestrator ?? throw new ArgumentNullException(nameof(taskOrchestrator));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static bool IsPendingReview(TaskReviewSnapshot snapshot) =>
        snapshot.HasExecutionRun && snapshot.Status == TaskOverallStatus.Ready;

    public static bool ShouldDisplayConflictResolvedBanner(TaskReviewSnapshot snapshot) =>
        snapshot.HasResolvedConflict && snapshot.Status == TaskOverallStatus.Ready;

    bool ITaskReviewService.IsPendingReview(TaskReviewSnapshot snapshot) => IsPendingReview(snapshot);

    bool ITaskReviewService.ShouldDisplayConflictResolvedBanner(TaskReviewSnapshot snapshot) => ShouldDisplayConflictResolvedBanner(snapshot);

    public async Task<bool> IsPullRequestDeliveryAvailableAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
        {
            return false;
        }

        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);
        return await _gitHubPrDeliveryService.IsPullRequestDeliveryAvailableAsync(workspace, cancellationToken);
    }

    public async Task<GitHubPullRequest> CreateOrSyncPullRequestAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken)
            ?? throw new TaskNotFoundException(taskId);

        var workspace = await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace #{task.WorkspaceId} not found for task #{taskId}.");

        var pr = await _gitHubPrDeliveryService.DeliverPullRequestAsync(task, workspace, cancellationToken);

        await _taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                await _markdownSyncService.SyncTaskToDiskAsync(task, workspace.RepositoryPath, cancellationToken: cancellationToken);
            }
            catch (Exception mex) when (mex is not OperationCanceledException)
            {
                _logger.LogWarning(mex, "Failed syncing markdown frontmatter after PR delivery for task #{TaskId}", task.Id);
            }
        }

        await _taskRepository.AddExecutionLogAsync(new ExecutionLog
        {
            TaskId = task.Id,
            Level = LogLevel.Information,
            Message = $"Synchronized GitHub Pull Request #{pr.Number} ({pr.HtmlUrl}) for task #{task.Id}.",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        return pr;
    }

    public Task<CompleteTaskResult> CloseExternallyMergedTaskAsync(int taskId, CancellationToken cancellationToken = default) =>
        _taskOrchestrator.CloseExternallyMergedTaskAsync(taskId, cancellationToken);

    public async Task<TaskReviewSnapshot?> GetSnapshotAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null) return null;

        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch) ? "main" : task.BaseBranch;
        var featureBranch = string.IsNullOrWhiteSpace(task.FeatureBranch) ? $"feature/task-{task.Id}" : task.FeatureBranch;

        var metrics = ExtractGateMetrics(task);
        var files = await LoadChangedFilesAsync(task, baseBranch, cancellationToken);
        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        var hasResolvedConflict = bag.Get<bool?>(PipelineDataBagKeys.HasResolvedConflict) ?? false;

        return new TaskReviewSnapshot(
            TaskId: task.Id,
            Title: task.Title,
            FeatureBranch: featureBranch,
            BaseBranch: baseBranch,
            MergeMode: task.MergeMode,
            Status: task.Status,
            Files: files,
            GateMetrics: metrics,
            HasResolvedConflict: hasResolvedConflict,
            HasExecutionRun: task.HasExecutionRun,
            PullRequestNumber: task.PullRequestNumber,
            PullRequestUrl: task.PullRequestUrl,
            ExternalSource: task.ExternalSource,
            ExternalReferenceId: task.ExternalReferenceId);
    }

    public async Task<IReadOnlyList<TaskReviewSnapshot>> GetPendingReviewsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ForgeTask> candidateTasks;
        if (workspaceId.HasValue)
        {
            var wsTasks = await _taskRepository.GetByWorkspaceIdAsync(workspaceId.Value, cancellationToken);
            candidateTasks = wsTasks
                .Where(t => t.HasExecutionRun && t.Status == TaskOverallStatus.Ready)
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
        }
        else
        {
            var readyTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken);
            candidateTasks = readyTasks
                .Where(t => t.HasExecutionRun)
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
        }

        var results = new List<TaskReviewSnapshot>();
        foreach (var task in candidateTasks)
        {
            try
            {
                var snapshot = await GetSnapshotAsync(task.Id, cancellationToken);
                if (snapshot != null)
                {
                    results.Add(snapshot);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load review snapshot for candidate task #{TaskId} in pending reviews", task.Id);
            }
        }

        return results;
    }

    public static QualityGateMetrics ExtractGateMetrics(ForgeTask task) =>
        QualityGateMetricsExtractor.Extract(task);

    public async Task<TaskDiffResult> GetTaskDiffAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new TaskNotFoundException(taskId);
        }
        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch) ? "main" : task.BaseBranch;
        return await LoadTaskDiffAsync(task, baseBranch, cancellationToken);
    }

    private async Task<IReadOnlyList<FileDiffSummary>> LoadChangedFilesAsync(
        ForgeTask task,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        try
        {
            var diffResult = await LoadTaskDiffAsync(task, baseBranch, cancellationToken);
            return diffResult.Files;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load changed files for review snapshot of task #{TaskId}", task.Id);
            return Array.Empty<FileDiffSummary>();
        }
    }

    private async Task<TaskDiffResult> LoadTaskDiffAsync(
        ForgeTask task,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        if (bag.TryGet<List<FileDiffSummary>>(PipelineDataBagKeys.ReviewFiles, out var customFiles) && customFiles != null && customFiles.Count > 0)
        {
            return new TaskDiffResult(customFiles, DiffProvenance.PipelineSnapshot, baseBranch);
        }

        var workspace = await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
        if (workspace == null || (string.IsNullOrWhiteSpace(workspace.RepositoryPath) && string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)))
        {
            return new TaskDiffResult(Array.Empty<FileDiffSummary>(), DiffProvenance.BranchHeadFallback, baseBranch);
        }

        // Case B: If task is finished/merged and has a recorded SquashCommitSha, retrieve commit patch diff
        if (task.Status == TaskOverallStatus.Closed && !string.IsNullOrWhiteSpace(task.SquashCommitSha))
        {
            try
            {
                var rawDiff = await _gitWorkspaceManager.GetCommitDiffAsync(workspace, task.SquashCommitSha, cancellationToken);
                return new TaskDiffResult(ParseGitDiff(rawDiff), DiffProvenance.SquashCommit, baseBranch, task.SquashCommitSha);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve squashed commit diff for closed task #{TaskId}", task.Id);
                return new TaskDiffResult(Array.Empty<FileDiffSummary>(), DiffProvenance.SquashCommit, baseBranch, task.SquashCommitSha);
            }
        }

        // Case B.2: If terminal task without a SquashCommitSha (e.g. cancelled, rejected, or closed without merge), fallback to branch diff
        if ((task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected) && string.IsNullOrWhiteSpace(task.SquashCommitSha))
        {
            try
            {
                var rawDiff = await _gitWorkspaceManager.GenerateDiffAsync(workspace, task.Id, baseBranch, cancellationToken);
                return new TaskDiffResult(ParseGitDiff(rawDiff), DiffProvenance.BranchHeadFallback, baseBranch);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve branch diff for closed/cancelled/rejected task #{TaskId} without squash commit", task.Id);
                return new TaskDiffResult(Array.Empty<FileDiffSummary>(), DiffProvenance.BranchHeadFallback, baseBranch);
            }
        }

        // Case C: Active task worktree
        try
        {
            var rawDiff = await _gitWorkspaceManager.GenerateLiveDiffAsync(workspace, task.Id, baseBranch, cancellationToken);
            return new TaskDiffResult(ParseGitDiff(rawDiff), DiffProvenance.LiveWorktree, baseBranch);
        }
        catch (Exception ex) when (ex is WorktreeNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "Live worktree missing for task #{TaskId}. Falling back to branch diff against '{BaseBranch}'.", task.Id, baseBranch);
            try
            {
                var rawDiff = await _gitWorkspaceManager.GenerateDiffAsync(workspace, task.Id, baseBranch, cancellationToken);
                return new TaskDiffResult(ParseGitDiff(rawDiff), DiffProvenance.BranchHeadFallback, baseBranch);
            }
            catch (Exception branchEx)
            {
                _logger.LogWarning(branchEx, "Fallback branch diff also failed for task #{TaskId}", task.Id);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve live diff for active task #{TaskId}", task.Id);
            throw;
        }
    }

    public static IReadOnlyList<FileDiffSummary> ParseGitDiff(string diffText)
    {
        var results = new List<FileDiffSummary>();
        if (string.IsNullOrWhiteSpace(diffText)) return results;

        diffText = diffText.Replace("\r\n", "\n").Replace('\r', '\n');

        var fileSections = Regex.Split(diffText, @"(?=^diff --git )", RegexOptions.Multiline);

        foreach (var section in fileSections)
        {
            if (string.IsNullOrWhiteSpace(section) || !section.StartsWith("diff --git")) continue;

            var match = DiffHeaderRegex().Match(section);
            var filePath = match.Success ? match.Groups["newPath"].Value.Trim() : "file.txt";

            var isNewFile = section.Contains("new file mode");
            var isDeletedFile = section.Contains("deleted file mode");
            var isRenamedFile = section.Contains("rename to ") || section.Contains("similarity index ");

            var changeType = isNewFile ? FileChangeType.Added
                : isDeletedFile ? FileChangeType.Deleted
                : isRenamedFile ? FileChangeType.Renamed
                : FileChangeType.Modified;

            var lines = section.Split('\n');
            var oldBuilder = new System.Text.StringBuilder();
            var newBuilder = new System.Text.StringBuilder();
            int additions = 0;
            int deletions = 0;

            bool inHunk = false;
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("@@"))
                {
                    inHunk = true;
                    continue;
                }

                if (!inHunk) continue;

                if (line.StartsWith("+") && !line.StartsWith("+++"))
                {
                    additions++;
                    newBuilder.AppendLine(line.Length > 1 ? line[1..] : string.Empty);
                }
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                {
                    deletions++;
                    oldBuilder.AppendLine(line.Length > 1 ? line[1..] : string.Empty);
                }
                else if (line.StartsWith(" "))
                {
                    var content = line.Length > 1 ? line[1..] : string.Empty;
                    oldBuilder.AppendLine(content);
                    newBuilder.AppendLine(content);
                }
            }

            results.Add(new FileDiffSummary(
                Path: filePath,
                ChangeType: changeType,
                Additions: additions,
                Deletions: deletions,
                OldContent: oldBuilder.ToString().TrimEnd('\r', '\n'),
                NewContent: newBuilder.ToString().TrimEnd('\r', '\n')));
        }

        return results;
    }
}
