using System.Globalization;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using YamlDotNet.Serialization;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Outcome of validating a prospective task specification before it is written to disk.
/// </summary>
public sealed record TaskMarkdownValidationResult(bool IsValid, string? ErrorMessage)
{
    public static TaskMarkdownValidationResult Valid { get; } = new(true, null);

    public static TaskMarkdownValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}

/// <summary>
/// Validates prospective <c>docs/tasks/*.md</c> content (before any disk write) so that
/// an agent can self-correct within its active turn.
/// </summary>
public interface ITaskMarkdownValidator
{
    /// <summary>
    /// Returns true when <paramref name="path"/> designates a task specification under <c>docs/tasks/</c>.
    /// </summary>
    bool IsTaskSpecificationPath(string path);

    /// <summary>
    /// Validates the prospective content of a task specification. Content outside <c>docs/tasks/*.md</c> is always valid.
    /// </summary>
    /// <param name="workspaceId">Workspace owning the task graph.</param>
    /// <param name="currentTaskId">Task the agent is working on; used when the frontmatter has no <c>id</c>.</param>
    /// <param name="path">Path of the file being written.</param>
    /// <param name="content">Prospective full file content.</param>
    Task<TaskMarkdownValidationResult> ValidateAsync(int workspaceId, int? currentTaskId, string path, string content, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rejects task specification edits whose <c>depends_on</c> frontmatter would create a dependency cycle.
/// </summary>
public class TaskMarkdownValidator : ITaskMarkdownValidator
{
    private const string FrontmatterFence = "---";
    private const string ClosingFence = "\n---";
    private const char TaskIdPrefix = '#';

    private readonly ITaskRepository _taskRepository;
    private readonly IDependencyResolutionEngine _dependencyEngine;
    private readonly ILogger<TaskMarkdownValidator> _logger;
    private readonly IDeserializer _yamlDeserializer = TaskMarkdownSyncService.CreateYamlDeserializer();

    public TaskMarkdownValidator(
        ITaskRepository taskRepository,
        IDependencyResolutionEngine dependencyEngine,
        ILogger<TaskMarkdownValidator> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsTaskSpecificationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(TaskSyncConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 &&
               string.Equals(segments[^3], TaskSyncConstants.DocsSegment, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[^2], TaskSyncConstants.TasksSegment, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TaskMarkdownValidationResult> ValidateAsync(
        int workspaceId,
        int? currentTaskId,
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!IsTaskSpecificationPath(path))
        {
            return TaskMarkdownValidationResult.Valid;
        }

        var frontmatter = TryReadFrontmatter(path, content);
        if (frontmatter?.DependsOn == null || frontmatter.DependsOn.Count == 0)
        {
            return TaskMarkdownValidationResult.Valid;
        }

        var workspaceTasks = await _taskRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        var taskId = ResolveTargetTaskId(frontmatter, currentTaskId, path, workspaceTasks);
        if (taskId is not > 0)
        {
            if (HasNewTaskSelfDependency(frontmatter, path, workspaceTasks))
            {
                var selfLabel = !string.IsNullOrWhiteSpace(frontmatter.Title) ? frontmatter.Title.Trim() : Path.GetFileNameWithoutExtension(path);
                _logger.LogWarning("Rejected edit of '{Path}': new task '{Title}' depends on itself.", path, selfLabel);
                return TaskMarkdownValidationResult.Invalid(
                    $"[ERROR] Circular dependency detected: new task '{selfLabel}' cannot depend on itself.{Environment.NewLine}The dependency update was rejected and the file was not modified. Re-check your dependency graph and resolve the circular link.");
            }

            return TaskMarkdownValidationResult.Valid;
        }

        var prerequisiteIds = ResolvePrerequisites(frontmatter.DependsOn, workspaceTasks, path);

        var baseline = new List<(int TaskId, int DependsOnTaskId)>();
        var workspaceDependencies = await _taskRepository.GetWorkspaceDependenciesAsync(workspaceId, cancellationToken);
        foreach (var (dependentId, prerequisites) in workspaceDependencies)
        {
            if (dependentId == taskId.Value)
            {
                continue;
            }

            foreach (var prerequisiteId in prerequisites)
            {
                baseline.Add((dependentId, prerequisiteId));
            }
        }

        foreach (var prerequisiteId in prerequisiteIds)
        {
            var candidate = new List<(int TaskId, int DependsOnTaskId)>(baseline) { (taskId.Value, prerequisiteId) };
            if (_dependencyEngine.HasCycle(workspaceId, candidate))
            {
                _logger.LogWarning(
                    "Rejected edit of '{Path}': task #{TaskId} depending on #{PrerequisiteId} creates a dependency cycle.",
                    path, taskId.Value, prerequisiteId);
                return TaskMarkdownValidationResult.Invalid(FormatCycleError(taskId.Value, prerequisiteId));
            }
        }

        return TaskMarkdownValidationResult.Valid;
    }

    public static string FormatCycleError(int taskId, int prerequisiteId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[ERROR] Circular dependency detected: Task #{taskId} -> #{prerequisiteId} creates a directed graph cycle.{Environment.NewLine}The dependency update was rejected and the file was not modified. Re-check your dependency graph and resolve the circular link.");

    private TaskFrontmatter? TryReadFrontmatter(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith(FrontmatterFence, StringComparison.Ordinal))
        {
            return null;
        }

        var closing = normalized.IndexOf(ClosingFence, FrontmatterFence.Length, StringComparison.Ordinal);
        if (closing < 0)
        {
            _logger.LogWarning("Cannot validate dependencies of '{Path}': missing closing frontmatter fence.", path);
            return null;
        }

        var yaml = normalized.Substring(FrontmatterFence.Length, closing - FrontmatterFence.Length).Trim();
        try
        {
            return _yamlDeserializer.Deserialize<TaskFrontmatter>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // The post-stage markdown sync reports malformed frontmatter with full context.
            _logger.LogWarning(ex, "Cannot validate dependencies of '{Path}': frontmatter is not valid YAML.", path);
            return null;
        }
    }

    private List<int> ResolvePrerequisites(IEnumerable<object> references, IReadOnlyList<ForgeTask> workspaceTasks, string path)
    {
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in workspaceTasks)
        {
            byKey[task.Id.ToString(CultureInfo.InvariantCulture)] = task.Id;
            if (!string.IsNullOrWhiteSpace(task.Title))
            {
                byKey[task.Title.Trim()] = task.Id;
                var slug = TaskMarkdownSyncService.NormalizeDependencyKey(task.Title);
                if (!string.IsNullOrEmpty(slug))
                {
                    byKey[slug] = task.Id;
                }
            }
        }

        var resolved = new List<int>();
        foreach (var reference in references)
        {
            var text = reference?.ToString()?.Trim().TrimStart(TaskIdPrefix) ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            if (byKey.TryGetValue(text, out var id) ||
                byKey.TryGetValue(TaskMarkdownSyncService.NormalizeDependencyKey(text), out id))
            {
                resolved.Add(id);
            }
            else
            {
                // Unknown references are reported by the post-stage markdown sync.
                _logger.LogDebug("Dependency reference '{Reference}' in '{Path}' does not match any workspace task.", text, path);
            }
        }

        return resolved.Distinct().ToList();
    }

    private static int? ResolveTargetTaskId(
        TaskFrontmatter frontmatter,
        int? currentTaskId,
        string path,
        IReadOnlyList<ForgeTask> workspaceTasks)
    {
        if (frontmatter.Id is > 0)
        {
            return frontmatter.Id;
        }

        if (currentTaskId is not > 0)
        {
            return null;
        }

        var currentTask = workspaceTasks.FirstOrDefault(t => t.Id == currentTaskId.Value);
        if (currentTask == null || string.IsNullOrWhiteSpace(frontmatter.Title))
        {
            return currentTaskId;
        }

        var currentTitleKey = TaskMarkdownSyncService.NormalizeDependencyKey(currentTask.Title);
        var frontmatterTitleKey = TaskMarkdownSyncService.NormalizeDependencyKey(frontmatter.Title);
        var fileKey = TaskMarkdownSyncService.NormalizeDependencyKey(Path.GetFileName(path));

        if (!string.IsNullOrEmpty(currentTitleKey) &&
            (string.Equals(frontmatterTitleKey, currentTitleKey, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(fileKey, currentTitleKey, StringComparison.OrdinalIgnoreCase)))
        {
            return currentTaskId;
        }

        // Frontmatter has no id and its title/filename do not match currentTaskId: this is a brand-new follow-up task specification.
        return null;
    }

    private static bool HasNewTaskSelfDependency(
        TaskFrontmatter frontmatter,
        string path,
        IReadOnlyList<ForgeTask> workspaceTasks)
    {
        if (frontmatter.DependsOn == null || frontmatter.DependsOn.Count == 0)
        {
            return false;
        }

        var selfKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(frontmatter.Title))
        {
            selfKeys.Add(frontmatter.Title.Trim());
            var titleSlug = TaskMarkdownSyncService.NormalizeDependencyKey(frontmatter.Title);
            if (!string.IsNullOrEmpty(titleSlug))
            {
                selfKeys.Add(titleSlug);
            }
        }

        var fileName = Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            selfKeys.Add(fileName);
            selfKeys.Add(Path.GetFileNameWithoutExtension(path));
            var fileSlug = TaskMarkdownSyncService.NormalizeDependencyKey(fileName);
            if (!string.IsNullOrEmpty(fileSlug))
            {
                selfKeys.Add(fileSlug);
            }
        }

        if (selfKeys.Count == 0)
        {
            return false;
        }

        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in workspaceTasks)
        {
            existingKeys.Add(task.Id.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(task.Title))
            {
                existingKeys.Add(task.Title.Trim());
                var slug = TaskMarkdownSyncService.NormalizeDependencyKey(task.Title);
                if (!string.IsNullOrEmpty(slug))
                {
                    existingKeys.Add(slug);
                }
            }
        }

        foreach (var reference in frontmatter.DependsOn)
        {
            var text = reference?.ToString()?.Trim().TrimStart(TaskIdPrefix) ?? string.Empty;
            if (text.Length == 0 || existingKeys.Contains(text))
            {
                continue;
            }

            var normText = TaskMarkdownSyncService.NormalizeDependencyKey(text);
            if (!string.IsNullOrEmpty(normText) && existingKeys.Contains(normText))
            {
                continue;
            }

            if (selfKeys.Contains(text) || (!string.IsNullOrEmpty(normText) && selfKeys.Contains(normText)))
            {
                return true;
            }
        }

        return false;
    }
}
