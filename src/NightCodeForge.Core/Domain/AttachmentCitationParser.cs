using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Deterministic parser for <c>@attachment:{id}</c> citations in task descriptions and proposals.
/// </summary>
public static partial class AttachmentCitationParser
{
    [GeneratedRegex(@"(?<![A-Za-z0-9._%+-])@attachment:#?(?<id>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();

    /// <summary>
    /// Extracts distinct positive attachment IDs cited via <c>@attachment:{id}</c> in order of appearance.
    /// </summary>
    public static IReadOnlyList<int> ExtractAttachmentIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();

        foreach (Match match in CitationRegex().Matches(text))
        {
            if (int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                id > 0 &&
                seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Alias for <see cref="ExtractAttachmentIds"/>.
    /// </summary>
    public static IReadOnlyList<int> Parse(string? text) => ExtractAttachmentIds(text);

    /// <summary>
    /// Alias for <see cref="ExtractAttachmentIds"/>.
    /// </summary>
    public static IReadOnlyList<int> ParseAttachmentIds(string? text) => ExtractAttachmentIds(text);

    /// <summary>
    /// Unions explicit attachment IDs (preserving any supplied values for downstream validation)
    /// with <c>@attachment:{id}</c> citations extracted from <paramref name="description"/>.
    /// </summary>
    public static IReadOnlyList<int> UnionAttachmentIds(IEnumerable<int>? explicitIds, string? description)
    {
        var ids = new List<int>();
        var seen = new HashSet<int>();

        if (explicitIds != null)
        {
            foreach (var id in explicitIds)
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        foreach (var citedId in ExtractAttachmentIds(description))
        {
            if (seen.Add(citedId))
            {
                ids.Add(citedId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Unions explicit attachment IDs with citations in <paramref name="description"/> and verifies
    /// that every ID exists in <paramref name="attachmentRepository"/> and belongs to <paramref name="workspaceId"/>.
    /// </summary>
    public static async Task<IReadOnlyList<int>> ValidateAndResolveWorkspaceIdsAsync(
        ITaskAttachmentRepository attachmentRepository,
        int workspaceId,
        IEnumerable<int>? explicitIds,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var (resolvedIds, missingIds) = await ResolveWorkspaceAttachmentIdsCoreAsync(
            attachmentRepository,
            workspaceId,
            explicitIds,
            description,
            cancellationToken);

        if (missingIds.Count > 0)
        {
            throw new AttachmentNotFoundException(missingIds[0], workspaceId);
        }

        return resolvedIds;
    }

    /// <summary>
    /// Unions explicit attachment IDs with citations in <paramref name="description"/> and resolves
    /// only those IDs that currently exist in <paramref name="attachmentRepository"/> and belong to
    /// <paramref name="workspaceId"/>. Missing, deleted, or cross-workspace IDs are logged and ignored.
    /// </summary>
    public static async Task<IReadOnlyList<int>> ResolveExistingWorkspaceIdsAsync(
        ITaskAttachmentRepository attachmentRepository,
        int workspaceId,
        IEnumerable<int>? explicitIds,
        string? description,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedIds, missingIds) = await ResolveWorkspaceAttachmentIdsCoreAsync(
            attachmentRepository,
            workspaceId,
            explicitIds,
            description,
            cancellationToken);

        if (missingIds.Count > 0)
        {
            logger?.LogWarning(
                "Ignoring missing or inaccessible attachment IDs [{AttachmentIds}] in workspace #{WorkspaceId}.",
                string.Join(", ", missingIds),
                workspaceId);
        }

        return resolvedIds;
    }

    /// <summary>
    /// Replaces attachment links for <paramref name="taskId"/> using only currently existing workspace
    /// attachments, retrying safely if an attachment is deleted concurrently before persistence completes.
    /// </summary>
    public static async Task<IReadOnlyList<int>> SetExistingTaskAttachmentLinksAsync(
        ITaskAttachmentRepository attachmentRepository,
        int workspaceId,
        int taskId,
        IEnumerable<int>? candidateAttachmentIds,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentRepository);

        var remaining = (candidateAttachmentIds ?? Array.Empty<int>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        while (true)
        {
            try
            {
                await attachmentRepository.SetTaskAttachmentLinksAsync(taskId, remaining, cancellationToken);
                return remaining;
            }
            catch (AttachmentNotFoundException ex) when (remaining.Count > 0)
            {
                logger?.LogWarning(
                    ex,
                    "Attachment #{AttachmentId} was missing while updating links for task #{TaskId} in workspace #{WorkspaceId}; filtering to remaining existing attachments.",
                    ex.AttachmentId,
                    taskId,
                    workspaceId);

                var candidates = remaining.Where(id => id != ex.AttachmentId).ToList();
                var nextRemaining = (await ResolveExistingWorkspaceIdsAsync(
                        attachmentRepository,
                        workspaceId,
                        candidates,
                        description: null,
                        logger,
                        cancellationToken))
                    .ToList();

                if (nextRemaining.Count >= remaining.Count)
                {
                    logger?.LogWarning(
                        ex,
                        "Unable to shrink candidate attachment links for task #{TaskId} in workspace #{WorkspaceId} after missing attachment #{AttachmentId}; aborting link update safely.",
                        taskId,
                        workspaceId,
                        ex.AttachmentId);
                    var persisted = await attachmentRepository.GetByTaskIdAsync(taskId, cancellationToken)
                        ?? Array.Empty<TaskAttachment>();
                    return persisted
                        .Where(a => a != null && a.Id > 0)
                        .Select(a => a.Id)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToList();
                }

                remaining = nextRemaining;
            }
        }
    }

    /// <summary>
    /// Removes all <c>@attachment:{id}</c> (and optional trailing attachment title) citations
    /// from <paramref name="text"/> cleanly while collapsing redundant inline whitespace.
    /// </summary>
    public static string RemoveAttachmentCitation(string? text, int attachmentId, string? title = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (attachmentId <= 0)
        {
            return text;
        }

        var idStr = attachmentId.ToString(CultureInfo.InvariantCulture);
        var tokenPattern = $@"(?<![A-Za-z0-9._%+-])@attachment:#?{idStr}\b";
        if (!string.IsNullOrWhiteSpace(title))
        {
            var escapedTitle = Regex.Escape(title.Trim());
            tokenPattern = $@"{tokenPattern}(?:[ \t]+{escapedTitle}(?=$|[\s.,;:!?)\]\}}>'""`]))?";
        }

        var fullPattern = $@"(?<lead>[ \t]*)(?:{tokenPattern})(?<trail>[ \t]*)";
        var updated = Regex.Replace(
            text,
            fullPattern,
            match =>
            {
                var atLineStart = match.Index == 0 || text[match.Index - 1] is '\n' or '\r';
                var endIndex = match.Index + match.Length;
                var atLineEnd = endIndex >= text.Length || text[endIndex] is '\r' or '\n';
                if (!atLineStart && !atLineEnd && match.Groups["lead"].Length > 0 && match.Groups["trail"].Length > 0)
                {
                    return " ";
                }

                return string.Empty;
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return updated.Trim(' ', '\t');
    }

    private static async Task<(IReadOnlyList<int> ResolvedIds, IReadOnlyList<int> MissingIds)> ResolveWorkspaceAttachmentIdsCoreAsync(
        ITaskAttachmentRepository attachmentRepository,
        int workspaceId,
        IEnumerable<int>? explicitIds,
        string? description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentRepository);

        var unionIds = UnionAttachmentIds(explicitIds, description);
        if (unionIds.Count == 0)
        {
            return (Array.Empty<int>(), Array.Empty<int>());
        }

        var positiveIds = unionIds.Where(id => id > 0).ToList();
        var existing = positiveIds.Count > 0
            ? await attachmentRepository.GetByIdsAsync(positiveIds, cancellationToken) ?? Array.Empty<TaskAttachment>()
            : Array.Empty<TaskAttachment>();
        var byId = existing
            .Where(a => a != null)
            .ToDictionary(a => a.Id);

        var resolved = new List<int>(unionIds.Count);
        var missing = new List<int>();
        foreach (var id in unionIds)
        {
            if (id > 0 && byId.TryGetValue(id, out var attachment) && attachment.WorkspaceId == workspaceId)
            {
                resolved.Add(id);
            }
            else
            {
                missing.Add(id);
            }
        }

        resolved.Sort();
        return (resolved, missing);
    }
}

/// <summary>
/// Compact digest entry serialized inside <c>&lt;workspace_attachments&gt;</c> and <c>&lt;task_attachments&gt;</c>.
/// </summary>
public sealed record AttachmentDigestEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("contentKind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskAttachmentContentKind ContentKind { get; init; }

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = string.Empty;

    [JsonPropertyName("dimensions")]
    public string? Dimensions { get; init; }

    [JsonPropertyName("isSymlink")]
    public bool IsSymlink { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

/// <summary>
/// Formats progressive-disclosure <c>&lt;workspace_attachments&gt;</c> and <c>&lt;task_attachments&gt;</c> prompt digests.
/// </summary>
public static class AttachmentDigestFormatter
{
    public const string WorkspaceAttachmentsTag = "workspace_attachments";
    public const string TaskAttachmentsTag = "task_attachments";

    private static readonly JsonSerializerOptions DigestJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Resolves the canonical virtual sandbox path (<c>/workspace/attachments/...</c>) for an attachment.
    /// Uses <see cref="TaskAttachment.MarkdownPath"/> when present, otherwise <see cref="TaskAttachment.RelativePath"/>.
    /// </summary>
    public static string ResolveVirtualAttachmentPath(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var rel = !string.IsNullOrWhiteSpace(attachment.MarkdownPath)
            ? attachment.MarkdownPath
            : attachment.RelativePath;
        var cleanRel = (rel ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(cleanRel)
            ? AttachmentConstants.VirtualAttachmentsMountPath
            : $"{AttachmentConstants.VirtualAttachmentsMountPath}/{cleanRel}";
    }

    /// <summary>
    /// Formats image pixel dimensions (<c>{width}x{height}</c>) when both dimensions are positive, or null otherwise.
    /// </summary>
    public static string? FormatDimensions(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.WidthPx is > 0 && attachment.HeightPx is > 0
            ? $"{attachment.WidthPx.Value}x{attachment.HeightPx.Value}"
            : null;
    }

    /// <summary>
    /// Maps a <see cref="TaskAttachment"/> domain entity into its canonical prompt digest entry.
    /// </summary>
    public static AttachmentDigestEntry ToDigestEntry(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return new AttachmentDigestEntry
        {
            Id = attachment.Id,
            Title = attachment.Title,
            ContentKind = attachment.ContentKind,
            MediaType = attachment.MediaType,
            Dimensions = FormatDimensions(attachment),
            IsSymlink = attachment.IsSymlink,
            Path = ResolveVirtualAttachmentPath(attachment)
        };
    }

    /// <summary>
    /// Builds the <c>&lt;workspace_attachments&gt;</c> digest for the Ideation Agent, or empty string when none exist.
    /// </summary>
    public static string FormatWorkspaceAttachmentsDigest(IEnumerable<TaskAttachment>? attachments) =>
        FormatDigest(
            attachments,
            WorkspaceAttachmentsTag,
            "<!-- Workspace attachments are mounted read-only at /workspace/attachments. Use read(path) or bash to inspect them, and cite relevant attachments in tasks with '@attachment:{id} {Title}' and/or 'attachment_ids'. -->");

    /// <summary>
    /// Builds the <c>&lt;task_attachments&gt;</c> digest for Workflow Stage Agents (Layer 3), or empty string when none exist.
    /// </summary>
    public static string FormatTaskAttachmentsDigest(IEnumerable<TaskAttachment>? attachments) =>
        FormatDigest(
            attachments,
            TaskAttachmentsTag,
            "<!-- Linked attachments for this task are mounted read-only at /workspace/attachments. Pass 'path' directly to read(path) or inspect /workspace/attachments with bash. -->");

    private static string FormatDigest(
        IEnumerable<TaskAttachment>? attachments,
        string rootTag,
        string guidanceComment)
    {
        if (attachments == null)
        {
            return string.Empty;
        }

        var ordered = attachments
            .Where(a => a != null)
            .OrderBy(a => a.Id)
            .ToList();

        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<{rootTag}>");
        sb.AppendLine(guidanceComment);
        foreach (var attachment in ordered)
        {
            var entry = ToDigestEntry(attachment);
            sb.AppendLine(JsonSerializer.Serialize(entry, DigestJsonOptions));
        }

        sb.Append($"</{rootTag}>");
        return sb.ToString();
    }
}
