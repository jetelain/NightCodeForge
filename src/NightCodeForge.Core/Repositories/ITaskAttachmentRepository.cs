using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Repository interface for workspace attachment persistence and task-attachment links.
/// </summary>
public interface ITaskAttachmentRepository
{
    Task<TaskAttachment?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskAttachment>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskAttachment>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<TaskAttachment?> GetByWorkspaceAndSha256Async(int workspaceId, string sha256, CancellationToken cancellationToken = default);
    Task<TaskAttachment> CreateAsync(TaskAttachment attachment, CancellationToken cancellationToken = default);
    Task UpdateAsync(TaskAttachment attachment, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskAttachment>> GetByTaskIdAsync(int taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetLinkedTaskIdsAsync(int attachmentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTask>> GetLinkedTasksAsync(int attachmentId, CancellationToken cancellationToken = default);
    Task SetTaskAttachmentLinksAsync(int taskId, IEnumerable<int> attachmentIds, CancellationToken cancellationToken = default);
    Task AddTaskAttachmentLinksAsync(int taskId, IEnumerable<int> attachmentIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory attachment repository for unit testing and detached execution.
/// </summary>
public class InMemoryTaskAttachmentRepository : ITaskAttachmentRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskAttachment> _attachments = new();
    private readonly List<TaskAttachmentLink> _links = new();
    private readonly ITaskRepository? _taskRepository;
    private int _nextAttachmentId = 1;
    private int _nextLinkId = 1;

    public InMemoryTaskAttachmentRepository()
    {
    }

    public InMemoryTaskAttachmentRepository(ITaskRepository taskRepository)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
    }

    public Task<TaskAttachment?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_attachments.TryGetValue(id, out var attachment) ? CloneAttachment(attachment) : null);
        }
    }

    public Task<IReadOnlyList<TaskAttachment>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(ids);

        var idSet = ids.Distinct().ToHashSet();
        if (idSet.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<TaskAttachment>>(Array.Empty<TaskAttachment>());
        }

        lock (_lock)
        {
            IReadOnlyList<TaskAttachment> results = _attachments.Values
                .Where(a => idSet.Contains(a.Id))
                .OrderBy(a => a.Id)
                .Select(CloneAttachment)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<IReadOnlyList<TaskAttachment>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            IReadOnlyList<TaskAttachment> results = _attachments.Values
                .Where(a => a.WorkspaceId == workspaceId)
                .OrderBy(a => a.Id)
                .Select(CloneAttachment)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<TaskAttachment?> GetByWorkspaceAndSha256Async(int workspaceId, string sha256, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var normalizedSha256 = NormalizeSha256(sha256);

        lock (_lock)
        {
            var match = _attachments.Values.FirstOrDefault(a =>
                a.WorkspaceId == workspaceId &&
                string.Equals(a.Sha256, normalizedSha256, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(match != null ? CloneAttachment(match) : null);
        }
    }

    public Task<TaskAttachment> CreateAsync(TaskAttachment attachment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(attachment);
        attachment.Sha256 = NormalizeSha256(attachment.Sha256);

        lock (_lock)
        {
            var duplicate = _attachments.Values.FirstOrDefault(a =>
                a.WorkspaceId == attachment.WorkspaceId &&
                string.Equals(a.Sha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                throw new InvalidOperationException(
                    $"Attachment with SHA-256 '{attachment.Sha256}' already exists in Workspace #{attachment.WorkspaceId} (Attachment #{duplicate.Id}).");
            }

            if (attachment.Id <= 0)
            {
                attachment.Id = _nextAttachmentId++;
            }
            else if (attachment.Id >= _nextAttachmentId)
            {
                _nextAttachmentId = attachment.Id + 1;
            }

            _attachments[attachment.Id] = CloneAttachment(attachment);
            return Task.FromResult(attachment);
        }
    }

    public async Task UpdateAsync(TaskAttachment attachment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(attachment);
        attachment.Sha256 = NormalizeSha256(attachment.Sha256);

        List<int> linkedTaskIds;
        lock (_lock)
        {
            if (!_attachments.ContainsKey(attachment.Id))
            {
                throw new AttachmentNotFoundException(attachment.Id, attachment.WorkspaceId);
            }

            var duplicate = _attachments.Values.FirstOrDefault(a =>
                a.Id != attachment.Id &&
                a.WorkspaceId == attachment.WorkspaceId &&
                string.Equals(a.Sha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                throw new InvalidOperationException(
                    $"Attachment with SHA-256 '{attachment.Sha256}' already exists in Workspace #{attachment.WorkspaceId} (Attachment #{duplicate.Id}).");
            }

            _attachments[attachment.Id] = CloneAttachment(attachment);
            linkedTaskIds = _links
                .Where(l => l.TaskAttachmentId == attachment.Id)
                .Select(l => l.TaskId)
                .Distinct()
                .ToList();
        }

        await SyncTaskNavigationLinksAsync(linkedTaskIds, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<int> linkedTaskIds;
        lock (_lock)
        {
            linkedTaskIds = _links
                .Where(l => l.TaskAttachmentId == id)
                .Select(l => l.TaskId)
                .Distinct()
                .ToList();
            _attachments.Remove(id);
            _links.RemoveAll(l => l.TaskAttachmentId == id);
        }

        await SyncTaskNavigationLinksAsync(linkedTaskIds, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskAttachment>> GetByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_taskRepository != null)
        {
            var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
            if (task == null)
            {
                lock (_lock)
                {
                    _links.RemoveAll(l => l.TaskId == taskId);
                }

                return Array.Empty<TaskAttachment>();
            }
        }

        lock (_lock)
        {
            IReadOnlyList<TaskAttachment> results = _links
                .Where(l => l.TaskId == taskId)
                .Select(l => _attachments.TryGetValue(l.TaskAttachmentId, out var a) ? a : null)
                .Where(a => a != null)
                .Select(a => CloneAttachment(a!))
                .OrderBy(a => a.Id)
                .ToList();
            return results;
        }
    }

    public async Task<IReadOnlyList<int>> GetLinkedTaskIdsAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        var (taskIds, _) = await GetLiveLinkedTasksInternalAsync(attachmentId, cancellationToken);
        return taskIds;
    }

    public async Task<IReadOnlyList<ForgeTask>> GetLinkedTasksAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        var (_, tasks) = await GetLiveLinkedTasksInternalAsync(attachmentId, cancellationToken);
        return tasks;
    }

    public async Task SetTaskAttachmentLinksAsync(int taskId, IEnumerable<int> attachmentIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(attachmentIds);

        var distinctIds = attachmentIds.Distinct().ToList();
        ForgeTask? task = _taskRepository != null ? await _taskRepository.GetByIdAsync(taskId, cancellationToken) : null;
        if (_taskRepository != null && task == null)
        {
            throw new TaskNotFoundException(taskId);
        }

        lock (_lock)
        {
            ValidateAttachmentsLocked(distinctIds, task?.WorkspaceId);

            _links.RemoveAll(l => l.TaskId == taskId);
            foreach (var attachmentId in distinctIds)
            {
                _links.Add(new TaskAttachmentLink
                {
                    Id = _nextLinkId++,
                    TaskId = taskId,
                    TaskAttachmentId = attachmentId
                });
            }

            if (task != null)
            {
                PopulateTaskAttachmentLinksLocked(task);
            }
        }
    }

    public async Task AddTaskAttachmentLinksAsync(int taskId, IEnumerable<int> attachmentIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(attachmentIds);

        var distinctIds = attachmentIds.Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return;
        }

        ForgeTask? task = _taskRepository != null ? await _taskRepository.GetByIdAsync(taskId, cancellationToken) : null;
        if (_taskRepository != null && task == null)
        {
            throw new TaskNotFoundException(taskId);
        }

        lock (_lock)
        {
            ValidateAttachmentsLocked(distinctIds, task?.WorkspaceId);

            var existingIds = _links
                .Where(l => l.TaskId == taskId)
                .Select(l => l.TaskAttachmentId)
                .ToHashSet();

            foreach (var attachmentId in distinctIds)
            {
                if (existingIds.Add(attachmentId))
                {
                    _links.Add(new TaskAttachmentLink
                    {
                        Id = _nextLinkId++,
                        TaskId = taskId,
                        TaskAttachmentId = attachmentId
                    });
                }
            }

            if (task != null)
            {
                PopulateTaskAttachmentLinksLocked(task);
            }
        }
    }

    private async Task<(IReadOnlyList<int> TaskIds, IReadOnlyList<ForgeTask> Tasks)> GetLiveLinkedTasksInternalAsync(
        int attachmentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<int> candidateTaskIds;
        lock (_lock)
        {
            candidateTaskIds = _links
                .Where(l => l.TaskAttachmentId == attachmentId)
                .Select(l => l.TaskId)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }

        if (candidateTaskIds.Count == 0)
        {
            return (Array.Empty<int>(), Array.Empty<ForgeTask>());
        }

        if (_taskRepository == null)
        {
            return (candidateTaskIds, Array.Empty<ForgeTask>());
        }

        var existingTasks = (await _taskRepository.GetByIdsAsync(candidateTaskIds, cancellationToken))
            .OrderBy(t => t.Id)
            .ToList();
        var existingTaskIds = existingTasks.Select(t => t.Id).ToHashSet();

        if (existingTaskIds.Count < candidateTaskIds.Count)
        {
            var staleTaskIds = candidateTaskIds.Where(id => !existingTaskIds.Contains(id)).ToHashSet();
            lock (_lock)
            {
                _links.RemoveAll(l => staleTaskIds.Contains(l.TaskId));
            }
        }

        var liveTaskIds = candidateTaskIds.Where(existingTaskIds.Contains).ToList();
        return (liveTaskIds, existingTasks);
    }

    private async Task SyncTaskNavigationLinksAsync(IReadOnlyCollection<int> taskIds, CancellationToken cancellationToken)
    {
        if (_taskRepository == null || taskIds.Count == 0)
        {
            return;
        }

        var tasks = await _taskRepository.GetByIdsAsync(taskIds, cancellationToken);
        if (tasks.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var task in tasks)
            {
                PopulateTaskAttachmentLinksLocked(task);
            }
        }
    }

    private void PopulateTaskAttachmentLinksLocked(ForgeTask task)
    {
        task.AttachmentLinks = _links
            .Where(l => l.TaskId == task.Id)
            .Select(l => new TaskAttachmentLink
            {
                Id = l.Id,
                TaskId = l.TaskId,
                TaskAttachmentId = l.TaskAttachmentId,
                ForgeTask = task,
                TaskAttachment = _attachments.TryGetValue(l.TaskAttachmentId, out var a) ? CloneAttachment(a) : null
            })
            .OrderBy(l => l.TaskAttachmentId)
            .ToList();
    }

    private static string NormalizeSha256(string? sha256) =>
        (sha256 ?? string.Empty).Trim().ToLowerInvariant();

    private void ValidateAttachmentsLocked(IReadOnlyList<int> attachmentIds, int? expectedWorkspaceId)
    {
        foreach (var attachmentId in attachmentIds)
        {
            if (!_attachments.TryGetValue(attachmentId, out var attachment))
            {
                throw expectedWorkspaceId.HasValue
                    ? new AttachmentNotFoundException(attachmentId, expectedWorkspaceId.Value)
                    : new AttachmentNotFoundException(attachmentId);
            }

            if (expectedWorkspaceId.HasValue && attachment.WorkspaceId != expectedWorkspaceId.Value)
            {
                throw new AttachmentNotFoundException(attachmentId, expectedWorkspaceId.Value);
            }
        }
    }

    private static TaskAttachment CloneAttachment(TaskAttachment source) => new()
    {
        Id = source.Id,
        WorkspaceId = source.WorkspaceId,
        Title = source.Title,
        Slug = source.Slug,
        RelativePath = source.RelativePath,
        MarkdownPath = source.MarkdownPath,
        ContentKind = source.ContentKind,
        MediaType = source.MediaType,
        WidthPx = source.WidthPx,
        HeightPx = source.HeightPx,
        SourceKind = source.SourceKind,
        SourceRef = source.SourceRef,
        IsSymlink = source.IsSymlink,
        Sha256 = source.Sha256,
        SizeBytes = source.SizeBytes,
        CreatedAt = source.CreatedAt
    };
}
