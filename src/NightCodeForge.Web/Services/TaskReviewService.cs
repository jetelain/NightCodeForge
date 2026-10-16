using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public interface ITaskReviewService
{
    Task<TaskReviewSnapshot?> GetSnapshotAsync(int taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskReviewSnapshot>> GetPendingReviewsAsync(int? workspaceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileDiffSummary>> GetTaskDiffFilesAsync(int taskId, CancellationToken cancellationToken = default);
}

public partial class TaskReviewService : ITaskReviewService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IGitWorkspaceManager? _gitWorkspaceManager;
    private readonly ILogger<TaskReviewService> _logger;

    [GeneratedRegex(@"^diff --git a/(?<oldPath>.+?) b/(?<newPath>.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex DiffHeaderRegex();

    public TaskReviewService(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        ILogger<TaskReviewService> logger,
        IGitWorkspaceManager? gitWorkspaceManager = null)
    {
        _taskRepository = taskRepository;
        _workspaceRepository = workspaceRepository;
        _logger = logger;
        _gitWorkspaceManager = gitWorkspaceManager;
    }

    public async Task<TaskReviewSnapshot?> GetSnapshotAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null) return null;

        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch) ? "main" : task.BaseBranch;
        var featureBranch = string.IsNullOrWhiteSpace(task.FeatureBranch) ? $"feature/task-{task.Id}" : task.FeatureBranch;

        var metrics = ExtractGateMetrics(task);
        var files = await LoadChangedFilesAsync(task, baseBranch, cancellationToken);

        return new TaskReviewSnapshot(
            TaskId: task.Id,
            Title: task.Title,
            FeatureBranch: featureBranch,
            BaseBranch: baseBranch,
            MergeMode: task.MergeMode,
            Status: task.Status,
            Files: files,
            GateMetrics: metrics);
    }

    public async Task<IReadOnlyList<TaskReviewSnapshot>> GetPendingReviewsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ForgeTask> candidateTasks;
        if (workspaceId.HasValue)
        {
            candidateTasks = await _taskRepository.GetByWorkspaceIdAsync(workspaceId.Value, cancellationToken);
        }
        else
        {
            var readyTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken);
            var implementedTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Implemented, cancellationToken);
            var reviewedTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Reviewed, cancellationToken);
            candidateTasks = readyTasks.Concat(implementedTasks).Concat(reviewedTasks).ToList();
        }

        var results = new List<TaskReviewSnapshot>();
        foreach (var task in candidateTasks)
        {
            var snapshot = await GetSnapshotAsync(task.Id, cancellationToken);
            if (snapshot != null)
            {
                results.Add(snapshot);
            }
        }

        return results;
    }

    private static QualityGateMetrics ExtractGateMetrics(ForgeTask task)
    {
        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);

        double currentCov = 0.0;
        if (bag.TryGet<double>("coverage.current.line_rate", out var cRate))
        {
            currentCov = cRate <= 1.0 ? cRate * 100.0 : cRate;
        }
        else if (bag.TryGet<double>("coverage.current", out var cVal))
        {
            currentCov = cVal;
        }

        double baseCov = currentCov;
        if (bag.TryGet<double>("coverage.baseline.line_rate", out var bRate))
        {
            baseCov = bRate <= 1.0 ? bRate * 100.0 : bRate;
        }
        else if (bag.TryGet<double>("coverage.baseline", out var bVal))
        {
            baseCov = bVal;
        }

        double covDelta = currentCov - baseCov;
        if (bag.TryGet<double>("coverage.delta", out var explicitCovDelta))
        {
            covDelta = explicitCovDelta <= 1.0 && explicitCovDelta >= -1.0 ? explicitCovDelta * 100.0 : explicitCovDelta;
        }

        int currentWarns = bag.TryGet<int>("warnings.current.total", out var cw) ? cw :
            (bag.TryGet<int>("warnings.current.count", out cw) ? cw : 0);
        int baseWarns = bag.TryGet<int>("warnings.baseline.total", out var bw) ? bw :
            (bag.TryGet<int>("warnings.baseline.count", out bw) ? bw : currentWarns);
        int warnDelta = bag.TryGet<int>("warnings.delta", out var wd) ? wd : (currentWarns - baseWarns);

        int totalTests = bag.TryGet<int>("tests.current.total", out var tt) ? tt :
            (bag.TryGet<int>("tests.total", out tt) ? tt : 0);
        int passedTests = bag.TryGet<int>("tests.current.passed", out var pt) ? pt :
            (bag.TryGet<int>("tests.passed", out pt) ? pt : totalTests);
        int failedTests = bag.TryGet<int>("tests.current.failed", out var ft) ? ft :
            (bag.TryGet<int>("tests.failed", out ft) ? ft : 0);
        int durationMs = bag.TryGet<int>("tests.current.duration_ms", out var dm) ? dm :
            (bag.TryGet<int>("tests.duration_ms", out dm) ? dm : 0);

        return new QualityGateMetrics(
            BaselineCoverage: Math.Round(baseCov, 2),
            CurrentCoverage: Math.Round(currentCov, 2),
            CoverageDelta: Math.Round(covDelta, 2),
            BaselineWarnings: baseWarns,
            CurrentWarnings: currentWarns,
            WarningDelta: warnDelta,
            TotalTests: totalTests,
            PassedTests: passedTests,
            FailedTests: failedTests,
            DurationMs: durationMs);
    }

    public async Task<IReadOnlyList<FileDiffSummary>> GetTaskDiffFilesAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null) return Array.Empty<FileDiffSummary>();
        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch) ? "main" : task.BaseBranch;
        return await LoadChangedFilesAsync(task, baseBranch, cancellationToken);
    }

    private async Task<IReadOnlyList<FileDiffSummary>> LoadChangedFilesAsync(
        ForgeTask task,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        if (bag.TryGet<List<FileDiffSummary>>("ReviewFiles", out var customFiles) && customFiles != null && customFiles.Count > 0)
        {
            return customFiles;
        }

        if (_gitWorkspaceManager != null)
        {
            try
            {
                var workspace = await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
                if (workspace != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath))
                {
                    string rawDiff = string.Empty;

                    // 1. If task is finished/merged and has a recorded SquashCommitSha, retrieve commit patch diff
                    if (task.Status == TaskOverallStatus.Closed && !string.IsNullOrWhiteSpace(task.SquashCommitSha))
                    {
                        rawDiff = await _gitWorkspaceManager.GetCommitDiffAsync(workspace, task.SquashCommitSha, cancellationToken);
                    }

                    // 2. If active (not Closed), retrieve live diff directly from the worktree
                    if (string.IsNullOrWhiteSpace(rawDiff) && task.Status != TaskOverallStatus.Closed)
                    {
                        rawDiff = await _gitWorkspaceManager.GenerateLiveDiffAsync(workspace, task.Id, baseBranch, cancellationToken);
                    }

                    // 3. Fallback to branch diff
                    if (string.IsNullOrWhiteSpace(rawDiff))
                    {
                        rawDiff = await _gitWorkspaceManager.GenerateDiffAsync(workspace, task.Id, baseBranch, cancellationToken);
                    }

                    if (!string.IsNullOrWhiteSpace(rawDiff))
                    {
                        var parsed = ParseGitDiff(rawDiff);
                        if (parsed.Count > 0)
                        {
                            return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve git diff for task #{TaskId}", task.Id);
            }
        }

        return Array.Empty<FileDiffSummary>();
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

            var changeType = isNewFile ? FileChangeType.Added
                : isDeletedFile ? FileChangeType.Deleted
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
