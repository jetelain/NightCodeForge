using System.Globalization;
using System.Text.RegularExpressions;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Live intake state of an attachment chip surfaced in Ideation chat or composer controls.
/// </summary>
public enum AttachmentIntakeStatus
{
    Converting = 0,
    Stored = 1,
    Symlinked = 2,
    Failed = 3
}

/// <summary>
/// View model representing an attachment chip displayed in the Ideation composer or user message bubble.
/// </summary>
public sealed class ConversationAttachmentChipViewModel
{
    public Guid ChipId { get; init; } = Guid.NewGuid();
    public int? AttachmentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public AttachmentIntakeStatus Status { get; set; }
    public bool IsSymlink { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public TaskAttachmentContentKind ContentKind { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ThumbnailDataUri { get; set; }
    public bool IsImage => ContentKind == TaskAttachmentContentKind.Image;

    public static ConversationAttachmentChipViewModel FromAttachment(
        TaskAttachment attachment,
        AttachmentIntakeStatus? statusOverride = null)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        return new ConversationAttachmentChipViewModel
        {
            AttachmentId = attachment.Id,
            Title = attachment.Title,
            Status = statusOverride ?? AttachmentPresentationHelper.ResolveIntakeStatus(attachment),
            IsSymlink = attachment.IsSymlink,
            SourceRef = attachment.SourceRef,
            ContentKind = attachment.ContentKind,
            MediaType = attachment.MediaType,
            SizeBytes = attachment.SizeBytes
        };
    }

    public static ConversationAttachmentChipViewModel CreatePendingUpload(string fileName)
    {
        var raw = fileName ?? string.Empty;
        return new ConversationAttachmentChipViewModel
        {
            Title = string.IsNullOrWhiteSpace(raw) ? "upload" : raw,
            Status = AttachmentIntakeStatus.Converting,
            IsSymlink = false,
            SourceRef = raw
        };
    }

    public static ConversationAttachmentChipViewModel CreatePendingHostPath(string hostPath)
    {
        var normalized = hostPath?.Trim() ?? string.Empty;
        var fileName = Path.GetFileName(normalized.Replace('\\', '/'));
        return new ConversationAttachmentChipViewModel
        {
            Title = string.IsNullOrWhiteSpace(fileName) ? normalized : fileName,
            Status = AttachmentIntakeStatus.Converting,
            IsSymlink = true,
            SourceRef = normalized
        };
    }

    public static ConversationAttachmentChipViewModel CreatePendingUrl(string url)
    {
        var normalized = url?.Trim() ?? string.Empty;
        return new ConversationAttachmentChipViewModel
        {
            Title = normalized,
            Status = AttachmentIntakeStatus.Converting,
            IsSymlink = false,
            SourceRef = normalized
        };
    }

    public void ApplyIngested(TaskAttachment ingested)
    {
        ArgumentNullException.ThrowIfNull(ingested);
        AttachmentId = ingested.Id;
        Title = ingested.Title;
        IsSymlink = ingested.IsSymlink;
        SourceRef = ingested.SourceRef;
        ContentKind = ingested.ContentKind;
        MediaType = ingested.MediaType;
        SizeBytes = ingested.SizeBytes;
        Status = AttachmentPresentationHelper.ResolveIntakeStatus(ingested);
        ErrorMessage = null;
    }

    public void MarkFailed(string? errorMessage)
    {
        Status = AttachmentIntakeStatus.Failed;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Result of extracting host paths, URLs, and <c>@attachment:{id}</c> tokens from a chat message.
/// </summary>
public sealed record MessageCitationExtraction(
    IReadOnlyList<string> HostPaths,
    IReadOnlyList<string> Urls,
    IReadOnlyList<int> CitedAttachmentIds)
{
    public bool HasAny => HostPaths.Count > 0 || Urls.Count > 0 || CitedAttachmentIds.Count > 0;
}

/// <summary>
/// Loaded preview data for <see cref="Components.Ideation.AttachmentPreviewDrawer"/>.
/// </summary>
public sealed record AttachmentPreviewModel
{
    public required TaskAttachment Attachment { get; init; }
    public string? WorkspaceName { get; init; }
    public string RenderedHtml { get; init; } = string.Empty;
    public string MarkdownContent { get; init; } = string.Empty;
    public string? ImageDataUri { get; init; }
    public bool IsTargetReachable { get; init; } = true;
    public IReadOnlyList<ForgeTask> LinkedTasks { get; init; } = Array.Empty<ForgeTask>();
}

/// <summary>
/// Impact summary presented to the user before deleting an attachment.
/// </summary>
public sealed record AttachmentDeleteImpact(
    TaskAttachment Attachment,
    IReadOnlyList<ForgeTask> LinkedTasks);

/// <summary>
/// Pure presentation, formatting, filtering, and citation extraction helpers for workspace attachments.
/// </summary>
public static partial class AttachmentPresentationHelper
{
    public const string StatusStored = "stored";
    public const string StatusSymlinked = "symlinked";
    public const string StatusConverting = "converting";
    public const string StatusFailed = "failed";

    public const string ReachableLabel = "Reachable";
    public const string TargetMissingLabel = "Target Missing";

    public const string SymlinkIconPrefix = "🔗";
    public const string CitationPrefix = "@attachment:";
    public const int MaxBatchUploadFiles = 10;

    private const double Kilobyte = 1024d;
    private const double Megabyte = Kilobyte * 1024d;
    private const double Gigabyte = Megabyte * 1024d;

    private static readonly char[] TrailingCitationPunctuation =
        ['.', ',', ';', ':', '!', '?', ')', ']', '>', '}', '\'', '"', '`'];

    [GeneratedRegex(@"""(?<path>(?:[a-zA-Z]:[\\/]|\\\\(?:wsl\.localhost|wsl\$|[a-zA-Z0-9._-]+)\\|/(?:home|mnt|Users|tmp|var|opt)/|~/)(?:[^""\r\n])+)""", RegexOptions.Compiled)]
    private static partial Regex QuotedHostPathRegex();

    [GeneratedRegex(@"`(?<path>(?:[a-zA-Z]:[\\/]|\\\\(?:wsl\.localhost|wsl\$|[a-zA-Z0-9._-]+)\\|/(?:home|mnt|Users|tmp|var|opt)/|~/)(?:[^`\r\n])+)`", RegexOptions.Compiled)]
    private static partial Regex BacktickHostPathRegex();

    [GeneratedRegex(@"(?<![:\w])(?<path>[a-zA-Z]:[\\/][^\s""'`<>|*?]+)", RegexOptions.Compiled)]
    private static partial Regex WindowsDrivePathRegex();

    [GeneratedRegex(@"(?<![:\\/])(?<path>\\\\[^\s""'`<>|*?\\]+\\[^\s""'`<>|*?]+)", RegexOptions.Compiled)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"(?<=^|[\s(\['""`])(?<path>(?:/(?:home|mnt|Users|tmp|var|opt)/|~/)[A-Za-z0-9._/\-~+%:@-]+)", RegexOptions.Compiled)]
    private static partial Regex PosixOrTildePathRegex();

    [GeneratedRegex(@"(?<url>https?://[^\s<>""'`\)\]]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HttpUrlRegex();

    public static AttachmentIntakeStatus ResolveIntakeStatus(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return attachment.IsSymlink ? AttachmentIntakeStatus.Symlinked : AttachmentIntakeStatus.Stored;
    }

    public static string FormatIntakeStatusLabel(AttachmentIntakeStatus status) => status switch
    {
        AttachmentIntakeStatus.Converting => StatusConverting,
        AttachmentIntakeStatus.Stored => StatusStored,
        AttachmentIntakeStatus.Symlinked => StatusSymlinked,
        AttachmentIntakeStatus.Failed => StatusFailed,
        _ => StatusStored
    };

    public static string GetIntakeStatusBadgeClass(AttachmentIntakeStatus status) => status switch
    {
        AttachmentIntakeStatus.Converting => "badge-amber",
        AttachmentIntakeStatus.Stored => "badge-teal",
        AttachmentIntakeStatus.Symlinked => "badge-indigo",
        AttachmentIntakeStatus.Failed => "badge-crimson",
        _ => "badge-gray"
    };

    public static string FormatSymlinkBadge(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return FormatSymlinkBadge(attachment.IsSymlink, attachment.SourceRef);
    }

    public static string FormatSymlinkBadge(bool isSymlink, string? sourceRef) =>
        isSymlink && !string.IsNullOrWhiteSpace(sourceRef)
            ? $"{SymlinkIconPrefix} {sourceRef.Trim()}"
            : string.Empty;

    public static string FormatTargetReachabilityLabel(bool isReachable) =>
        isReachable ? ReachableLabel : TargetMissingLabel;

    public static string GetTargetReachabilityBadgeClass(bool isReachable) =>
        isReachable ? "badge-emerald" : "badge-crimson";

    public static string FormatSizeBytes(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return "0 B";
        }

        if (sizeBytes < (long)Kilobyte)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes} B");
        }

        if (sizeBytes < (long)Megabyte)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes / Kilobyte:0.#} KB");
        }

        if (sizeBytes < (long)Gigabyte)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes / Megabyte:0.#} MB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes / Gigabyte:0.#} GB");
    }

    public static string FormatContentKindLabel(TaskAttachmentContentKind kind) => kind switch
    {
        TaskAttachmentContentKind.PlainText => "Plain Text",
        TaskAttachmentContentKind.RichDocument => "Rich Document",
        TaskAttachmentContentKind.Image => "Image",
        _ => kind.ToString()
    };

    public static string GetContentKindIcon(TaskAttachmentContentKind kind) => kind switch
    {
        TaskAttachmentContentKind.PlainText => "📄",
        TaskAttachmentContentKind.RichDocument => "📑",
        TaskAttachmentContentKind.Image => "🖼️",
        _ => "📎"
    };

    public static string FormatDimensions(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return FormatDimensions(attachment.WidthPx, attachment.HeightPx);
    }

    public static string FormatDimensions(int? widthPx, int? heightPx) =>
        widthPx is > 0 && heightPx is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{widthPx.Value} x {heightPx.Value}")
            : string.Empty;

    public static string FormatCitationToken(int attachmentId) =>
        string.Create(CultureInfo.InvariantCulture, $"{CitationPrefix}{attachmentId}");

    public static string FormatCitationSnippet(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return FormatCitationSnippet(attachment.Id, attachment.Title);
    }

    public static string FormatCitationSnippet(int attachmentId, string? title)
    {
        var token = FormatCitationToken(attachmentId);
        return string.IsNullOrWhiteSpace(title) ? token : $"{token} {title.Trim()}";
    }

    public static string AppendCitationToPrompt(string? currentPrompt, TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return AppendCitationToPrompt(currentPrompt, attachment.Id, attachment.Title);
    }

    public static string AppendCitationToPrompt(string? currentPrompt, int attachmentId, string? title = null)
    {
        var snippet = FormatCitationSnippet(attachmentId, title);
        if (string.IsNullOrWhiteSpace(currentPrompt))
        {
            return snippet;
        }

        var existingIds = AttachmentCitationParser.ExtractAttachmentIds(currentPrompt);
        if (existingIds.Contains(attachmentId))
        {
            return currentPrompt;
        }

        var separator = char.IsWhiteSpace(currentPrompt[^1]) ? string.Empty : " ";
        return $"{currentPrompt}{separator}{snippet}";
    }

    public static MessageCitationExtraction ExtractMessageCitations(
        string? message,
        IAttachmentIntakeService? intakeService = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new MessageCitationExtraction(
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<int>());
        }

        var hostPaths = (intakeService != null ? intakeService.ExtractCitedHostPaths(message) : null)
            ?? ExtractCitedHostPathsFallback(message);

        var urls = (intakeService != null ? intakeService.ExtractCitedUrls(message) : null)
            ?? ExtractCitedUrlsFallback(message);

        var citedIds = AttachmentCitationParser.ExtractAttachmentIds(message);

        return new MessageCitationExtraction(hostPaths, urls, citedIds);
    }

    public static void UpsertChip(
        IList<ConversationAttachmentChipViewModel> chips,
        ConversationAttachmentChipViewModel chip)
    {
        ArgumentNullException.ThrowIfNull(chips);
        ArgumentNullException.ThrowIfNull(chip);

        if (!chips.Contains(chip))
        {
            chips.Add(chip);
        }

        if (chip.AttachmentId.HasValue)
        {
            var targetId = chip.AttachmentId.Value;
            for (var i = chips.Count - 1; i >= 0; i--)
            {
                var existing = chips[i];
                if (!ReferenceEquals(existing, chip) && existing.AttachmentId == targetId)
                {
                    chips.RemoveAt(i);
                }
            }
        }
    }

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

    public static IReadOnlyList<ConversationAttachmentChipViewModel> ResolveRestoredMessageChips(
        string? message,
        IReadOnlyList<TaskAttachment>? workspaceAttachments)
    {
        if (string.IsNullOrWhiteSpace(message) || workspaceAttachments == null || workspaceAttachments.Count == 0)
        {
            return Array.Empty<ConversationAttachmentChipViewModel>();
        }

        var extraction = ExtractMessageCitations(message);
        if (!extraction.HasAny)
        {
            return Array.Empty<ConversationAttachmentChipViewModel>();
        }

        var orderedAttachments = workspaceAttachments
            .Where(a => a != null)
            .OrderBy(a => a.Id)
            .ToList();

        var chips = new List<ConversationAttachmentChipViewModel>();

        foreach (var hostPath in extraction.HostPaths)
        {
            var canonicalWsl = VirtualMountEntry.ToDefaultWslPath(hostPath);
            var matched = orderedAttachments.FirstOrDefault(a =>
                string.Equals(a.SourceRef, hostPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(canonicalWsl) &&
                 string.Equals(a.SourceRef, canonicalWsl, StringComparison.OrdinalIgnoreCase)));
            if (matched != null)
            {
                UpsertChip(chips, ConversationAttachmentChipViewModel.FromAttachment(matched));
            }
        }

        foreach (var url in extraction.Urls)
        {
            var matched = orderedAttachments.FirstOrDefault(a =>
                string.Equals(a.SourceRef, url, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                UpsertChip(chips, ConversationAttachmentChipViewModel.FromAttachment(matched));
            }
        }

        foreach (var citedId in extraction.CitedAttachmentIds)
        {
            var matched = orderedAttachments.FirstOrDefault(a => a.Id == citedId);
            if (matched != null)
            {
                UpsertChip(chips, ConversationAttachmentChipViewModel.FromAttachment(matched));
            }
        }

        return chips;
    }

    public static async Task<Dictionary<int, TaskAttachment>> ResolveProposalAttachmentMapAsync(
        IdeationProposalViewModel? proposal,
        IReadOnlyList<TaskAttachment>? workspaceAttachments,
        ITaskAttachmentRepository? attachmentRepository = null,
        int? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<int, TaskAttachment>();
        if (workspaceAttachments != null)
        {
            foreach (var att in workspaceAttachments)
            {
                if (att == null)
                {
                    continue;
                }

                if (!workspaceId.HasValue || workspaceId.Value <= 0 || att.WorkspaceId == workspaceId.Value)
                {
                    map[att.Id] = att;
                }
            }
        }

        if (proposal == null || attachmentRepository == null)
        {
            return map;
        }

        var effectiveIds = proposal.GetEffectiveAttachmentIds();
        var missingIds = effectiveIds.Where(id => !map.ContainsKey(id)).ToList();
        if (missingIds.Count > 0)
        {
            var fetched = await attachmentRepository.GetByIdsAsync(missingIds, cancellationToken)
                ?? Array.Empty<TaskAttachment>();
            foreach (var att in fetched)
            {
                if (att != null && (!workspaceId.HasValue || workspaceId.Value <= 0 || att.WorkspaceId == workspaceId.Value))
                {
                    map[att.Id] = att;
                }
            }
        }

        return map;
    }

    public static IReadOnlyList<TaskAttachment> FilterAttachments(
        IEnumerable<TaskAttachment>? attachments,
        string? searchQuery)
    {
        if (attachments == null)
        {
            return Array.Empty<TaskAttachment>();
        }

        var ordered = attachments
            .Where(a => a != null)
            .OrderBy(a => a.Id);

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return ordered.ToList();
        }

        var search = searchQuery.Trim();
        var hasIdMatch = int.TryParse(
            search.TrimStart('#'),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var searchId);

        return ordered
            .Where(a =>
                (hasIdMatch && a.Id == searchId) ||
                a.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                a.SourceRef?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                a.Slug?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                a.RelativePath?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractCitedHostPathsFallback(string message)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string raw, bool isQuoted)
        {
            var candidate = raw.Trim();
            if (!isQuoted)
            {
                candidate = candidate.TrimEnd(TrailingCitationPunctuation);
            }

            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals("/workspace", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (seen.Add(candidate))
            {
                results.Add(candidate);
            }
        }

        foreach (Match match in QuotedHostPathRegex().Matches(message))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: true);
        }

        var scrubbed = QuotedHostPathRegex().Replace(message, " ");

        foreach (Match match in BacktickHostPathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: true);
        }

        scrubbed = BacktickHostPathRegex().Replace(scrubbed, " ");

        foreach (Match match in UncPathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: false);
        }

        scrubbed = UncPathRegex().Replace(scrubbed, " ");

        foreach (Match match in WindowsDrivePathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: false);
        }

        foreach (Match match in PosixOrTildePathRegex().Matches(scrubbed))
        {
            TryAdd(match.Groups["path"].Value, isQuoted: false);
        }

        return results;
    }

    private static IReadOnlyList<string> ExtractCitedUrlsFallback(string message)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HttpUrlRegex().Matches(message))
        {
            var candidate = match.Groups["url"].Value.Trim().TrimEnd(TrailingCitationPunctuation);
            if (!string.IsNullOrWhiteSpace(candidate) &&
                Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                seen.Add(uri.AbsoluteUri))
            {
                results.Add(uri.AbsoluteUri);
            }
        }

        return results;
    }

    public static bool CanSubmitIdeationComposer(
        string? userInput,
        IReadOnlyList<ConversationAttachmentChipViewModel>? pendingChips,
        bool hasActiveWorkspace,
        bool isGenerating)
    {
        if (!hasActiveWorkspace || isGenerating)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(userInput))
        {
            return true;
        }

        return pendingChips != null && pendingChips.Any(IsReadyChip);
    }

    public static string BuildEffectivePromptWithCitations(
        string? userInput,
        IReadOnlyList<ConversationAttachmentChipViewModel>? pendingChips)
    {
        var effective = (userInput ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(effective))
        {
            return effective;
        }

        var readyChips = pendingChips?.Where(IsReadyChip).ToList() ?? new List<ConversationAttachmentChipViewModel>();
        if (readyChips.Count == 0)
        {
            return string.Empty;
        }

        effective = readyChips.Any(c => c.IsImage)
            ? VisionConstants.DefaultImageOnlyPrompt
            : string.Empty;

        foreach (var chip in readyChips)
        {
            effective = AppendCitationToPrompt(effective, chip.AttachmentId!.Value, chip.Title);
        }

        return effective.Trim();
    }

    public static bool HasPendingOrCitedImageAttachments(
        IReadOnlyList<ConversationAttachmentChipViewModel>? pendingChips,
        IReadOnlyList<TaskAttachment>? workspaceAttachments,
        string? userInput)
    {
        if (pendingChips != null && pendingChips.Any(c => c.IsImage))
        {
            return true;
        }

        return HasCitedImageAttachment(userInput, workspaceAttachments);
    }

    public static bool HasSelectedOrCitedImageAttachments(
        IReadOnlyList<TaskAttachment>? availableAttachments,
        IReadOnlyCollection<int>? selectedAttachmentIds,
        string? description)
    {
        if (availableAttachments == null || availableAttachments.Count == 0)
        {
            return false;
        }

        if (selectedAttachmentIds != null &&
            availableAttachments.Any(a => selectedAttachmentIds.Contains(a.Id) && a.ContentKind == TaskAttachmentContentKind.Image))
        {
            return true;
        }

        return HasCitedImageAttachment(description, availableAttachments);
    }

    private static bool HasCitedImageAttachment(string? text, IReadOnlyList<TaskAttachment>? attachments)
    {
        if (string.IsNullOrWhiteSpace(text) || attachments == null || attachments.Count == 0)
        {
            return false;
        }

        var citedIds = AttachmentCitationParser.ExtractAttachmentIds(text);
        if (citedIds.Count == 0)
        {
            return false;
        }

        return citedIds.Any(id => attachments.Any(a => a.Id == id && a.ContentKind == TaskAttachmentContentKind.Image));
    }

    public static string? BuildTextOnlyImageWarning(
        VisionCapabilityStatus? visionStatus,
        bool hasImageAttachments)
    {
        if (!hasImageAttachments || visionStatus == null || visionStatus.SupportsVision)
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(visionStatus.WarningMessage)
            ? visionStatus.WarningMessage
            : "Selected model does not support vision. Attached images cannot be inspected visually.";
    }

    public static string FormatModelOptionLabel(AvailableModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var badge = model.FormattedCapabilitiesBadge;
        return !string.IsNullOrWhiteSpace(badge)
            ? $"{model.DisplayName} ({badge})"
            : model.DisplayName;
    }

    public static string FormatAllowedModelOptionLabel(OpenAiAllowedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var label = string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelId : model.DisplayName;
        var visionSuffix = model.SupportsVision == true ? $", {VisionConstants.VisionBadgeLabel}" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{label} ({model.ContextWindowTokens:N0} tokens{visionSuffix})");
    }

    public static string FormatAllowedModelVisionBadge(bool? supportsVision) => supportsVision switch
    {
        true => VisionConstants.VisionBadgeLabel,
        false => "Text only",
        null => "Auto"
    };

    public static string FormatAllowedModelVisionValue(bool? supportsVision) => supportsVision switch
    {
        true => "true",
        false => "false",
        null => string.Empty
    };

    public static bool? ParseAllowedModelVisionValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return bool.TryParse(value.Trim(), out var parsed) ? parsed : null;
    }

    public static bool TryDecodeClipboardImage(
        string? base64Data,
        string? fileName,
        string? contentType,
        out byte[] bytes,
        out string normalizedFileName,
        out string? errorMessage)
    {
        bytes = Array.Empty<byte>();
        normalizedFileName = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(base64Data))
        {
            errorMessage = "Clipboard paste did not include image data.";
            return false;
        }

        var trimmed = base64Data.Trim();
        var commaIdx = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIdx >= 0)
        {
            trimmed = trimmed[(commaIdx + 1)..].Trim();
        }

        try
        {
            bytes = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            errorMessage = "Clipboard image payload was not valid base64 data.";
            return false;
        }

        if (bytes.Length == 0)
        {
            errorMessage = "Clipboard image payload is empty.";
            return false;
        }

        if (bytes.Length > VisionConstants.MaxVisionImageBytes)
        {
            errorMessage = $"Pasted image ({bytes.Length / (1024.0 * 1024.0):F1} MB) exceeds maximum allowed vision image size of {VisionConstants.MaxVisionImageBytes / (1024 * 1024)} MB.";
            bytes = Array.Empty<byte>();
            return false;
        }

        if (!ImageHeaderInspector.TryInspect(bytes, out var header))
        {
            errorMessage = string.IsNullOrWhiteSpace(contentType)
                ? "Pasted clipboard data does not contain a valid PNG, JPEG, WebP, or GIF image header."
                : $"Pasted clipboard image ('{contentType.Trim()}') does not contain a valid PNG, JPEG, WebP, or GIF image header.";
            bytes = Array.Empty<byte>();
            return false;
        }

        var ext = header.MediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };

        var stem = !string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileNameWithoutExtension(fileName.Trim())
            : string.Empty;
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        }

        normalizedFileName = $"{stem}{ext}";
        return true;
    }

    private static bool IsReadyChip(ConversationAttachmentChipViewModel chip) =>
        chip.AttachmentId is > 0
        && chip.Status is AttachmentIntakeStatus.Stored or AttachmentIntakeStatus.Symlinked;
}
