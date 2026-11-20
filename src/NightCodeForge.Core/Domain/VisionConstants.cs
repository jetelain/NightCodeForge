using System.Globalization;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Named constants and canonical message/marker formatters for multimodal vision inspection,
/// context compaction budgeting, sliding-window image eviction, and UI capability badges.
/// </summary>
public static class VisionConstants
{
    public const long MaxVisionImageBytes = 10L * 1024 * 1024;
    public const int MaxVisionImageDimensionPx = 1568;
    public const int MaxImagesInActiveWindow = 5;

    public const int MaxSourceImageDimensionPx = 16384;
    public const long MaxSourceImagePixels = 64_000_000L;
    public const int ThumbnailMaxDimensionPx = 128;
    public const long MaxClipboardSignalRMessageBytes = 16L * 1024 * 1024;

    public const int VisionTileSizePx = 512;
    public const int VisionBaseTokens = 85;
    public const int VisionTokensPerTile = 170;
    public const int DefaultFallbackImageTokens = 768;

    public const int DefaultJpegEncodeQuality = 85;
    public const int DefaultThumbnailJpegEncodeQuality = 80;
    public const int Sha256PrefixLength = 12;

    public const string VisionBadgeLabel = "👁️ Vision";
    public const string ImageMediaTypePrefix = "image/";
    public const string TopLevelImageMediaType = "image";
    public const string InLoopVisualInspectionLabel = "In-Loop Visual Inspection";

    // AdditionalProperties keys attached to DataContent and injected visual ChatMessage instances
    public const string PropSourcePath = "vision.source_path";
    public const string PropSha256 = "vision.sha256";
    public const string PropWidthPx = "vision.width_px";
    public const string PropHeightPx = "vision.height_px";
    public const string PropCallId = "vision.call_id";
    public const string PropIsInjectedVisual = "vision.is_injected_visual";
    public const string PropInjectedCallIds = "vision.injected_call_ids";

    public const string LoadedImageToolResultPrefix = "Loaded image '";
    public const string EvictedImageMarkerPrefix = "[Earlier visual attachment '";
    public const string DefaultImageOnlyPrompt = "Please inspect the attached image(s).";

    public static string FormatEvictedImageMarker(string sourcePath) =>
        $"{EvictedImageMarkerPrefix}{sourcePath}' evicted from active image window to preserve context headroom; re-invoke read(\"{sourcePath}\") if needed]";

    public static string FormatVisionDisabledOmittedImageMarker(string sourcePath) =>
        $"[Visual attachment '{sourcePath}' omitted: active model does not support vision]";

    public static string FormatMissingImageMarker(string sourcePath) =>
        $"[Missing visual attachment: '{sourcePath}' — file no longer exists on disk]";

    public static string FormatVisualHeader(string sourcePath, string? callId, int widthPx, int heightPx) =>
        !string.IsNullOrWhiteSpace(callId)
            ? $"[Visual Attachment: {sourcePath} (call_id: {callId}, dimensions: {widthPx}x{heightPx})]"
            : $"[Visual Attachment: {sourcePath} ({widthPx}x{heightPx})]";

    public static string FormatReadImageToolResult(string sourcePath, int widthPx, int heightPx, string mediaType, int estimatedTokens) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Loaded image '{sourcePath}' ({widthPx}x{heightPx}, {mediaType}, ~{estimatedTokens} vision tokens). Visual content attached in the following user message.");

    public static string FormatTranscriptImageSummary(string mediaType, int? widthPx, int? heightPx, int byteCount, string? sha256)
    {
        var dims = widthPx is > 0 && heightPx is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{widthPx.Value}x{heightPx.Value}")
            : "unknown";
        var effectiveMediaType = string.IsNullOrWhiteSpace(mediaType) ? "image/*" : mediaType;
        var hashPrefix = FormatSha256Prefix(sha256);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[image: {effectiveMediaType}, {dims}, {byteCount} bytes, sha256:{hashPrefix}]");
    }

    public static string FormatCompactionImagePlaceholder(string sourcePath, int? widthPx, int? heightPx, string mediaType)
    {
        var dims = widthPx is > 0 && heightPx is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{widthPx.Value}x{heightPx.Value}")
            : "unknown";
        var effectiveMediaType = string.IsNullOrWhiteSpace(mediaType) ? "image/*" : mediaType;
        return $"[Visual Attachment: {sourcePath} ({dims}, {effectiveMediaType})]";
    }

    public static string FormatSha256Prefix(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return "unknown";
        }

        var trimmed = sha256.Trim();
        return trimmed.Length <= Sha256PrefixLength ? trimmed : trimmed[..Sha256PrefixLength];
    }

    public static int EstimateVisionTokens(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0)
        {
            return DefaultFallbackImageTokens;
        }

        int tilesX = (widthPx + VisionTileSizePx - 1) / VisionTileSizePx;
        int tilesY = (heightPx + VisionTileSizePx - 1) / VisionTileSizePx;
        return VisionBaseTokens + (VisionTokensPerTile * tilesX * tilesY);
    }
}
