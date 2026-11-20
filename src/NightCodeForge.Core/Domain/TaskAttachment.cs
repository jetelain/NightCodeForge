using System.Text.Json.Serialization;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Categorizes how an attachment's content is stored and consumed by agents.
/// </summary>
public enum TaskAttachmentContentKind
{
    PlainText = 0,
    RichDocument = 1,
    Image = 2
}

/// <summary>
/// Identifies the intake channel through which an attachment entered the workspace store.
/// </summary>
public enum TaskAttachmentSourceKind
{
    FileDrop = 0,
    HostPath = 1,
    /// <summary>Legacy read-only value retained so existing database rows remain readable and deletable.</summary>
    UrlCapture = 2
}

/// <summary>
/// Represents an immutable workspace-level context artifact stored in ~/.nightcodeforge/wks/{workspaceId}/attachments/.
/// </summary>
public class TaskAttachment
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string? MarkdownPath { get; set; }
    public TaskAttachmentContentKind ContentKind { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public int? WidthPx { get; set; }
    public int? HeightPx { get; set; }
    public TaskAttachmentSourceKind SourceKind { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public bool IsSymlink { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<TaskAttachmentLink> TaskLinks { get; set; } = new List<TaskAttachmentLink>();
}

/// <summary>
/// Join entity linking a <see cref="ForgeTask"/> to a workspace <see cref="TaskAttachment"/>.
/// </summary>
public class TaskAttachmentLink
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int TaskAttachmentId { get; set; }

    public ForgeTask? ForgeTask { get; set; }
    public TaskAttachment? TaskAttachment { get; set; }
}

/// <summary>
/// Machine-readable manifest serialized to attachments/manifest.json inside each workspace.
/// </summary>
public sealed record AttachmentManifest
{
    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("attachments")]
    public IReadOnlyList<AttachmentManifestEntry> Attachments { get; init; } = [];
}

/// <summary>
/// Single attachment entry in attachments/manifest.json.
/// </summary>
public sealed record AttachmentManifestEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("markdownPath")]
    public string? MarkdownPath { get; init; }

    [JsonPropertyName("contentKind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskAttachmentContentKind ContentKind { get; init; }

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = string.Empty;

    [JsonPropertyName("widthPx")]
    public int? WidthPx { get; init; }

    [JsonPropertyName("heightPx")]
    public int? HeightPx { get; init; }

    [JsonPropertyName("sourceKind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskAttachmentSourceKind SourceKind { get; init; }

    [JsonPropertyName("sourceRef")]
    public string SourceRef { get; init; } = string.Empty;

    [JsonPropertyName("isSymlink")]
    public bool IsSymlink { get; init; }

    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    public static AttachmentManifestEntry FromDomain(TaskAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return new AttachmentManifestEntry
        {
            Id = attachment.Id,
            WorkspaceId = attachment.WorkspaceId,
            Title = attachment.Title,
            Slug = attachment.Slug,
            RelativePath = attachment.RelativePath,
            MarkdownPath = attachment.MarkdownPath,
            ContentKind = attachment.ContentKind,
            MediaType = attachment.MediaType,
            WidthPx = attachment.WidthPx,
            HeightPx = attachment.HeightPx,
            SourceKind = attachment.SourceKind,
            SourceRef = attachment.SourceRef,
            IsSymlink = attachment.IsSymlink,
            TargetPath = attachment.IsSymlink ? attachment.SourceRef : null,
            Sha256 = attachment.Sha256,
            SizeBytes = attachment.SizeBytes,
            CreatedAt = attachment.CreatedAt
        };
    }
}

/// <summary>
/// Named constants and format classification rules for workspace attachments.
/// </summary>
public static class AttachmentConstants
{
    public const string AttachmentsDirectoryName = "attachments";
    public const string VirtualAttachmentsMountPath = "/workspace/attachments";
    public const string ManifestFileName = "manifest.json";
    public const string MarkdownExtension = ".md";

    public const long MaxFileDropBytes = 50L * 1024 * 1024;
    public const long MaxHostFileBytes = 50L * 1024 * 1024;
    public const int SymlinkCreationTimeoutSeconds = 15;
    public const int MaxSlugLength = 64;
    public const string DefaultSlug = "attachment";
    public const int SequencePaddingDigits = 3;

    public static readonly IReadOnlySet<string> PlainTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".text", ".log", ".json", ".xml", ".yaml", ".yml",
        ".csv", ".tsv", ".cs", ".razor", ".csproj", ".slnx", ".sln", ".props", ".targets",
        ".js", ".ts", ".tsx", ".jsx", ".py", ".sh", ".ps1", ".sql", ".css", ".scss",
        ".toml", ".ini", ".cfg"
    };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    public static readonly IReadOnlySet<string> RichDocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".html", ".htm", ".xhtml"
    };

    public static readonly string HtmlAcceptExtensions = string.Join(
        ",",
        PlainTextExtensions
            .Concat(ImageExtensions)
            .Concat(RichDocumentExtensions)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyDictionary<string, string> ExtensionMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = "text/markdown",
            [".markdown"] = "text/markdown",
            [".txt"] = "text/plain",
            [".text"] = "text/plain",
            [".log"] = "text/plain",
            [".json"] = "application/json",
            [".xml"] = "application/xml",
            [".yaml"] = "application/x-yaml",
            [".yml"] = "application/x-yaml",
            [".csv"] = "text/csv",
            [".tsv"] = "text/tab-separated-values",
            [".cs"] = "text/x-csharp",
            [".razor"] = "text/x-razor",
            [".csproj"] = "application/xml",
            [".slnx"] = "application/xml",
            [".sln"] = "text/plain",
            [".props"] = "application/xml",
            [".targets"] = "application/xml",
            [".js"] = "text/javascript",
            [".ts"] = "text/typescript",
            [".tsx"] = "text/tsx",
            [".jsx"] = "text/jsx",
            [".py"] = "text/x-python",
            [".sh"] = "application/x-sh",
            [".ps1"] = "text/plain",
            [".sql"] = "application/sql",
            [".css"] = "text/css",
            [".scss"] = "text/x-scss",
            [".toml"] = "application/toml",
            [".ini"] = "text/plain",
            [".cfg"] = "text/plain",
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".html"] = "text/html",
            [".htm"] = "text/html",
            [".xhtml"] = "application/xhtml+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif"
        };

    public static bool TryClassifyExtension(string? extension, out TaskAttachmentContentKind contentKind)
    {
        if (!string.IsNullOrWhiteSpace(extension))
        {
            var normalized = extension.StartsWith('.') ? extension : "." + extension;
            if (PlainTextExtensions.Contains(normalized))
            {
                contentKind = TaskAttachmentContentKind.PlainText;
                return true;
            }
            if (ImageExtensions.Contains(normalized))
            {
                contentKind = TaskAttachmentContentKind.Image;
                return true;
            }
            if (RichDocumentExtensions.Contains(normalized))
            {
                contentKind = TaskAttachmentContentKind.RichDocument;
                return true;
            }
        }

        contentKind = default;
        return false;
    }

    public static bool IsSupportedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var cleanFileName = Path.GetFileName(fileName.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(cleanFileName))
        {
            return false;
        }

        return TryClassifyExtension(Path.GetExtension(cleanFileName), out _);
    }

    public static string BuildUnsupportedFormatMessage(string fileName, string? extension)
    {
        var displayName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim();
        return string.IsNullOrWhiteSpace(extension)
            ? $"Unsupported attachment format (missing file extension) for '{displayName}'."
            : $"Unsupported attachment format '{extension}' for '{displayName}'.";
    }

    public static string ResolveMediaType(string? extension)
    {
        if (!string.IsNullOrWhiteSpace(extension))
        {
            var normalized = extension.StartsWith('.') ? extension : "." + extension;
            if (ExtensionMediaTypes.TryGetValue(normalized, out var mediaType))
            {
                return mediaType;
            }
        }

        return "application/octet-stream";
    }
}
