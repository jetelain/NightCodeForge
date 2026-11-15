using System.Collections.Concurrent;
using System.Security.Cryptography;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Services;

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
    Task<IReadOnlyList<ForgeTask>>? ExecutionTask = null);

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
        Task<IReadOnlyList<ForgeTask>>? executionTask = null);

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
        Task<IReadOnlyList<ForgeTask>>? executionTask = null)
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
            ExecutionTask: executionTask);

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
