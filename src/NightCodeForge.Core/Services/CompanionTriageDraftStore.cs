using System.Collections.Concurrent;
using System.Security.Cryptography;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Thread-safe handle exposing the persisted triage session task ID as soon as it is created
/// during background/companion triage execution.
/// </summary>
public sealed class TriageRunHandle
{
    private int _triageTaskId;
    private readonly TaskCompletionSource<int> _triageTaskIdTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Persisted triage session task ID once created, or <see langword="null"/> before creation.</summary>
    public int? TriageTaskId
    {
        get
        {
            var id = Volatile.Read(ref _triageTaskId);
            return id > 0 ? id : null;
        }
    }

    /// <summary>Completes when the triage task ID is created (or with 0 if triage failed prior to task creation).</summary>
    public Task<int> TriageTaskIdTask => _triageTaskIdTcs.Task;

    /// <summary>Raised when the triage session task ID is recorded.</summary>
    public event Action<int>? OnTriageTaskCreated;

    /// <summary>Records the persisted triage task ID once and notifies listeners.</summary>
    public void NotifyTriageTaskCreated(int taskId)
    {
        if (taskId <= 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _triageTaskId, taskId, 0) == 0)
        {
            _triageTaskIdTcs.TrySetResult(taskId);
            OnTriageTaskCreated?.Invoke(taskId);
        }
    }

    /// <summary>Completes <see cref="TriageTaskIdTask"/> with 0 if triage exited before creating a session task.</summary>
    public void NotifyFailedWithoutTask()
    {
        _triageTaskIdTcs.TrySetResult(0);
    }
}

/// <summary>
/// Represents a staged companion exploratory report awaiting triage completion or interactive ideation.
/// </summary>
public sealed record CompanionTriageDraft(
    string DraftId,
    int WorkspaceId,
    string Title,
    string ReportMarkdown,
    string BaseBranch,
    string? SessionDirectoryPath,
    DateTimeOffset CreatedAt,
    Task<IReadOnlyList<ForgeTask>>? ExecutionTask = null,
    TriageRunHandle? RunHandle = null,
    Task<TriageSessionResult>? SessionExecutionTask = null)
{
    /// <summary>Persisted triage session task ID if created during companion execution.</summary>
    public int? TriageTaskId => RunHandle?.TriageTaskId;
}

/// <summary>
/// Thread-safe store for staged companion triage and ideation drafts handed off from external companion tools.
/// </summary>
public interface ICompanionTriageDraftStore
{
    /// <summary>
    /// Stages a new companion draft for the specified workspace and returns the created draft record.
    /// </summary>
    CompanionTriageDraft CreateDraft(
        int workspaceId,
        string title,
        string reportMarkdown,
        string baseBranch,
        string? sessionDirectoryPath = null,
        Task<IReadOnlyList<ForgeTask>>? executionTask = null,
        TriageRunHandle? runHandle = null,
        Task<TriageSessionResult>? sessionExecutionTask = null);

    /// <summary>
    /// Retrieves an active, non-expired companion draft by identifier, or <see langword="null"/> if not found.
    /// </summary>
    CompanionTriageDraft? GetDraft(string? draftId);
}

/// <summary>
/// In-memory singleton implementation of <see cref="ICompanionTriageDraftStore"/> with deterministic 24-hour TTL pruning.
/// </summary>
public sealed class CompanionTriageDraftStore : ICompanionTriageDraftStore
{
    public const int DraftIdByteLength = 6;
    public static readonly TimeSpan DraftTimeToLive = TimeSpan.FromHours(24);

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CompanionTriageDraft> _drafts = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of <see cref="CompanionTriageDraftStore"/>.
    /// </summary>
    public CompanionTriageDraftStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public CompanionTriageDraft CreateDraft(
        int workspaceId,
        string title,
        string reportMarkdown,
        string baseBranch,
        string? sessionDirectoryPath = null,
        Task<IReadOnlyList<ForgeTask>>? executionTask = null,
        TriageRunHandle? runHandle = null,
        Task<TriageSessionResult>? sessionExecutionTask = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        var now = _timeProvider.GetUtcNow();
        PruneExpiredDrafts(now);

        var draftId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(DraftIdByteLength));
        var draft = new CompanionTriageDraft(
            DraftId: draftId,
            WorkspaceId: workspaceId,
            Title: title,
            ReportMarkdown: reportMarkdown,
            BaseBranch: baseBranch,
            SessionDirectoryPath: sessionDirectoryPath,
            CreatedAt: now,
            ExecutionTask: executionTask,
            RunHandle: runHandle,
            SessionExecutionTask: sessionExecutionTask);

        _drafts[draftId] = draft;
        return draft;
    }

    /// <inheritdoc />
    public CompanionTriageDraft? GetDraft(string? draftId)
    {
        if (string.IsNullOrWhiteSpace(draftId))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        PruneExpiredDrafts(now);

        return _drafts.TryGetValue(draftId.Trim(), out var draft) ? draft : null;
    }

    private void PruneExpiredDrafts(DateTimeOffset now)
    {
        foreach (var entry in _drafts)
        {
            if (now - entry.Value.CreatedAt > DraftTimeToLive)
            {
                _drafts.TryRemove(entry.Key, out _);
            }
        }
    }
}
