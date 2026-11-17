using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        ArgumentNullException.ThrowIfNull(attachmentRepository);

        var unionIds = UnionAttachmentIds(explicitIds, description);
        if (unionIds.Count == 0)
        {
            return Array.Empty<int>();
        }

        foreach (var id in unionIds)
        {
            if (id <= 0)
            {
                throw new AttachmentNotFoundException(id, workspaceId);
            }
        }

        var existing = await attachmentRepository.GetByIdsAsync(unionIds, cancellationToken);
        var byId = existing.ToDictionary(a => a.Id);
        foreach (var id in unionIds)
        {
            if (!byId.TryGetValue(id, out var attachment) || attachment.WorkspaceId != workspaceId)
            {
                throw new AttachmentNotFoundException(id, workspaceId);
            }
        }

        return unionIds.OrderBy(id => id).ToList();
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
