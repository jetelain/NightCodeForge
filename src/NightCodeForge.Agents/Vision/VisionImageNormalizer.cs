using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace NightCodeForge.Agents.Vision;

/// <summary>
/// Normalized image binary ready for multimodal vision LLM dispatch.
/// </summary>
public sealed record NormalizedVisionImage(
    byte[] Bytes,
    string MediaType,
    int WidthPx,
    int HeightPx,
    string Sha256,
    int EstimatedTokens);

/// <summary>
/// Validates, downscales (max <see cref="VisionConstants.MaxVisionImageDimensionPx"/> per side, aspect-preserving),
/// and normalizes image binaries using <see cref="SixLabors.ImageSharp"/> and <see cref="ImageHeaderInspector"/>.
/// </summary>
public static class VisionImageNormalizer
{
    private const string MediaTypePng = "image/png";
    private const string MediaTypeJpeg = "image/jpeg";

    public static async Task<NormalizedVisionImage> LoadAndNormalizeAsync(
        string hostPath,
        string logicalSourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalSourcePath);

        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException($"Image file not found: '{logicalSourcePath}'.", hostPath);
        }

        var fileInfo = new FileInfo(hostPath);
        if (fileInfo.Length <= 0)
        {
            throw new AttachmentConversionException(logicalSourcePath, $"Image '{logicalSourcePath}' is empty.");
        }

        if (fileInfo.Length > VisionConstants.MaxVisionImageBytes)
        {
            throw new AttachmentConversionException(
                logicalSourcePath,
                $"Image '{logicalSourcePath}' ({fileInfo.Length / (1024.0 * 1024.0):F1} MB) exceeds maximum allowed vision image size of {VisionConstants.MaxVisionImageBytes / (1024 * 1024)} MB.");
        }

        var rawBytes = await File.ReadAllBytesAsync(hostPath, cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(hostPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(logicalSourcePath);
        }
        return NormalizeBytes(rawBytes, extension, logicalSourcePath);
    }

    public static NormalizedVisionImage NormalizeBytes(
        byte[] rawBytes,
        string? extension,
        string logicalSourcePath)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalSourcePath);

        if (rawBytes.Length == 0)
        {
            throw new AttachmentConversionException(logicalSourcePath, $"Image '{logicalSourcePath}' is empty.");
        }

        if (rawBytes.Length > VisionConstants.MaxVisionImageBytes)
        {
            throw new AttachmentConversionException(
                logicalSourcePath,
                $"Image '{logicalSourcePath}' ({rawBytes.Length / (1024.0 * 1024.0):F1} MB) exceeds maximum allowed vision image size of {VisionConstants.MaxVisionImageBytes / (1024 * 1024)} MB.");
        }

        var rawSha256 = ComputeSha256Hex(rawBytes);

        ImageHeaderInfo headerInfo;
        var trimmedExt = extension?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmedExt) && AttachmentConstants.ImageExtensions.Contains(trimmedExt))
        {
            headerInfo = ImageHeaderInspector.Inspect(rawBytes, trimmedExt, logicalSourcePath);
        }
        else if (!ImageHeaderInspector.TryInspect(rawBytes, out headerInfo))
        {
            throw new AttachmentConversionException(
                logicalSourcePath,
                $"File '{logicalSourcePath}' does not contain a valid PNG, JPEG, WebP, or GIF image header.");
        }

        if (ExceedsMaxSourceBounds(headerInfo.WidthPx, headerInfo.HeightPx))
        {
            throw new AttachmentConversionException(
                logicalSourcePath,
                $"Image '{logicalSourcePath}' dimensions ({headerInfo.WidthPx}x{headerInfo.HeightPx}) exceed maximum allowed source dimensions ({VisionConstants.MaxSourceImageDimensionPx}px / {VisionConstants.MaxSourceImagePixels:N0} pixels).");
        }

        try
        {
            using var image = Image.Load(rawBytes);
            if (image.Width <= 0 || image.Height <= 0)
            {
                throw new AttachmentConversionException(
                    logicalSourcePath,
                    $"Image '{logicalSourcePath}' could not be decoded cleanly.");
            }

            if (ExceedsMaxSourceBounds(image.Width, image.Height))
            {
                throw new AttachmentConversionException(
                    logicalSourcePath,
                    $"Image '{logicalSourcePath}' decoded dimensions ({image.Width}x{image.Height}) exceed maximum allowed source dimensions ({VisionConstants.MaxSourceImageDimensionPx}px / {VisionConstants.MaxSourceImagePixels:N0} pixels).");
            }

            bool needsResize = image.Width > VisionConstants.MaxVisionImageDimensionPx
                || image.Height > VisionConstants.MaxVisionImageDimensionPx;
            bool isNativeVisionFormat = IsNativeVisionFormat(headerInfo.MediaType);

            if (!needsResize && isNativeVisionFormat)
            {
                return new NormalizedVisionImage(
                    Bytes: rawBytes,
                    MediaType: headerInfo.MediaType,
                    WidthPx: image.Width,
                    HeightPx: image.Height,
                    Sha256: rawSha256,
                    EstimatedTokens: VisionConstants.EstimateVisionTokens(image.Width, image.Height));
            }

            var (outputBytes, outputMediaType, outWidth, outHeight) = ResizeAndEncode(
                image,
                headerInfo.MediaType,
                VisionConstants.MaxVisionImageDimensionPx,
                VisionConstants.DefaultJpegEncodeQuality);

            if (outputBytes.Length == 0)
            {
                throw new AttachmentConversionException(
                    logicalSourcePath,
                    $"Image '{logicalSourcePath}' could not be normalized or encoded cleanly.");
            }

            return new NormalizedVisionImage(
                Bytes: outputBytes,
                MediaType: outputMediaType,
                WidthPx: outWidth,
                HeightPx: outHeight,
                Sha256: rawSha256,
                EstimatedTokens: VisionConstants.EstimateVisionTokens(outWidth, outHeight));
        }
        catch (AttachmentConversionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new AttachmentConversionException(
                logicalSourcePath,
                $"Image '{logicalSourcePath}' could not be decoded cleanly ({ex.Message}).",
                ex);
        }
    }

    /// <summary>
    /// Creates a compact UI thumbnail (max <paramref name="maxDimensionPx"/> per side) when the raw bytes
    /// represent a valid non-bomb image, returning <see langword="null"/> if decoding or validation fails.
    /// </summary>
    public static (byte[] Bytes, string MediaType)? TryCreateThumbnailBytes(
        byte[] rawBytes,
        int maxDimensionPx = VisionConstants.ThumbnailMaxDimensionPx)
    {
        if (rawBytes == null || rawBytes.Length == 0 || maxDimensionPx <= 0)
        {
            return null;
        }

        if (!ImageHeaderInspector.TryInspect(rawBytes, out var headerInfo) ||
            ExceedsMaxSourceBounds(headerInfo.WidthPx, headerInfo.HeightPx))
        {
            return null;
        }

        bool isNativeVisionFormat = IsNativeVisionFormat(headerInfo.MediaType);
        if (headerInfo.WidthPx <= maxDimensionPx &&
            headerInfo.HeightPx <= maxDimensionPx &&
            isNativeVisionFormat)
        {
            return (rawBytes, headerInfo.MediaType);
        }

        try
        {
            using var image = Image.Load(rawBytes);
            if (image.Width <= 0 || image.Height <= 0 ||
                ExceedsMaxSourceBounds(image.Width, image.Height))
            {
                return null;
            }

            if (image.Width <= maxDimensionPx &&
                image.Height <= maxDimensionPx &&
                isNativeVisionFormat)
            {
                return (rawBytes, headerInfo.MediaType);
            }

            var (outputBytes, outputMediaType, _, _) = ResizeAndEncode(
                image,
                headerInfo.MediaType,
                maxDimensionPx,
                VisionConstants.DefaultThumbnailJpegEncodeQuality);

            return outputBytes.Length > 0 ? (outputBytes, outputMediaType) : null;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            // Best-effort UI thumbnail generation: undecodable images fall back to the file icon chip.
            return null;
        }
    }

    private static (byte[] Bytes, string MediaType, int WidthPx, int HeightPx) ResizeAndEncode(
        Image image,
        string sourceMediaType,
        int maxDimensionPx,
        int jpegQuality)
    {
        if (image.Width > maxDimensionPx || image.Height > maxDimensionPx)
        {
            var (targetW, targetH) = CalculateDownscaledDimensions(image.Width, image.Height, maxDimensionPx);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(targetW, targetH),
                Mode = ResizeMode.Max
            }));
        }

        using var outputStream = new MemoryStream();
        string outputMediaType;
        if (string.Equals(sourceMediaType, MediaTypeJpeg, StringComparison.OrdinalIgnoreCase))
        {
            image.SaveAsJpeg(outputStream, new JpegEncoder { Quality = jpegQuality });
            outputMediaType = MediaTypeJpeg;
        }
        else
        {
            image.SaveAsPng(outputStream, new PngEncoder());
            outputMediaType = MediaTypePng;
        }

        return (outputStream.ToArray(), outputMediaType, image.Width, image.Height);
    }

    private static bool ExceedsMaxSourceBounds(int widthPx, int heightPx) =>
        widthPx > VisionConstants.MaxSourceImageDimensionPx
        || heightPx > VisionConstants.MaxSourceImageDimensionPx
        || (long)widthPx * heightPx > VisionConstants.MaxSourceImagePixels;

    private static bool IsNativeVisionFormat(string mediaType) =>
        string.Equals(mediaType, MediaTypePng, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, MediaTypeJpeg, StringComparison.OrdinalIgnoreCase);

    public static (int WidthPx, int HeightPx) CalculateDownscaledDimensions(int widthPx, int heightPx, int maxDimensionPx)
    {
        if (widthPx <= maxDimensionPx && heightPx <= maxDimensionPx)
        {
            return (Math.Max(1, widthPx), Math.Max(1, heightPx));
        }

        double scale = Math.Min((double)maxDimensionPx / widthPx, (double)maxDimensionPx / heightPx);
        int targetW = Math.Clamp((int)Math.Round(widthPx * scale), 1, maxDimensionPx);
        int targetH = Math.Clamp((int)Math.Round(heightPx * scale), 1, maxDimensionPx);
        return (targetW, targetH);
    }

    public static DataContent CreateDataContent(NormalizedVisionImage normalized, string logicalSourcePath, string? callId = null)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalSourcePath);

        var props = new AdditionalPropertiesDictionary
        {
            [VisionConstants.PropSourcePath] = logicalSourcePath,
            [VisionConstants.PropSha256] = normalized.Sha256,
            [VisionConstants.PropWidthPx] = normalized.WidthPx,
            [VisionConstants.PropHeightPx] = normalized.HeightPx
        };

        if (!string.IsNullOrWhiteSpace(callId))
        {
            props[VisionConstants.PropCallId] = callId;
        }

        return new DataContent(normalized.Bytes, normalized.MediaType)
        {
            Name = Path.GetFileName(logicalSourcePath),
            AdditionalProperties = props
        };
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
