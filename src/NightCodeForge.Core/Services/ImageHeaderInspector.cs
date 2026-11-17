using System.Buffers.Binary;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Extracted metadata from a validated image binary header.
/// </summary>
public readonly record struct ImageHeaderInfo(string MediaType, int WidthPx, int HeightPx);

/// <summary>
/// Validates image magic bytes and extracts pixel dimensions from PNG, JPEG, WebP, and GIF headers without external native dependencies.
/// </summary>
public static class ImageHeaderInspector
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] PngIhdrChunk = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    private static readonly byte[] Gif87aSignature = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a'];
    private static readonly byte[] Gif89aSignature = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a'];
    private static readonly byte[] RiffSignature = [(byte)'R', (byte)'I', (byte)'F', (byte)'F'];
    private static readonly byte[] WebpSignature = [(byte)'W', (byte)'E', (byte)'B', (byte)'P'];
    private static readonly byte[] Vp8Chunk = [(byte)'V', (byte)'P', (byte)'8', (byte)' '];
    private static readonly byte[] Vp8LChunk = [(byte)'V', (byte)'P', (byte)'8', (byte)'L'];
    private static readonly byte[] Vp8XChunk = [(byte)'V', (byte)'P', (byte)'8', (byte)'X'];

    /// <summary>
    /// Validates that <paramref name="data"/> matches the declared <paramref name="extension"/> and contains valid non-zero dimensions.
    /// Throws <see cref="AttachmentConversionException"/> if validation fails.
    /// </summary>
    public static ImageHeaderInfo Inspect(ReadOnlySpan<byte> data, string extension, string sourceName)
    {
        if (data.IsEmpty)
        {
            throw new AttachmentConversionException(sourceName, $"Image '{sourceName}' is empty.");
        }

        var normalizedExt = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (!normalizedExt.StartsWith('.'))
        {
            normalizedExt = "." + normalizedExt;
        }

        return normalizedExt switch
        {
            ".png" => InspectPng(data, sourceName),
            ".jpg" or ".jpeg" => InspectJpeg(data, sourceName),
            ".gif" => InspectGif(data, sourceName),
            ".webp" => InspectWebp(data, sourceName),
            _ => throw new AttachmentConversionException(sourceName, $"Unsupported image extension '{extension}' for '{sourceName}'.")
        };
    }

    private static ImageHeaderInfo InspectPng(ReadOnlySpan<byte> data, string sourceName)
    {
        const int minPngHeaderBytes = 24;
        if (data.Length < minPngHeaderBytes ||
            !data[..PngSignature.Length].SequenceEqual(PngSignature) ||
            !data.Slice(12, PngIhdrChunk.Length).SequenceEqual(PngIhdrChunk))
        {
            throw new AttachmentConversionException(sourceName, $"File '{sourceName}' is not a valid PNG image.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new AttachmentConversionException(sourceName, $"PNG image '{sourceName}' has invalid dimensions ({width}x{height}).");
        }

        return new ImageHeaderInfo("image/png", (int)width, (int)height);
    }

    private static ImageHeaderInfo InspectGif(ReadOnlySpan<byte> data, string sourceName)
    {
        const int minGifHeaderBytes = 10;
        if (data.Length < minGifHeaderBytes ||
            (!data[..Gif87aSignature.Length].SequenceEqual(Gif87aSignature) &&
             !data[..Gif89aSignature.Length].SequenceEqual(Gif89aSignature)))
        {
            throw new AttachmentConversionException(sourceName, $"File '{sourceName}' is not a valid GIF image.");
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));

        if (width <= 0 || height <= 0)
        {
            throw new AttachmentConversionException(sourceName, $"GIF image '{sourceName}' has invalid dimensions ({width}x{height}).");
        }

        return new ImageHeaderInfo("image/gif", width, height);
    }

    private static ImageHeaderInfo InspectJpeg(ReadOnlySpan<byte> data, string sourceName)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new AttachmentConversionException(sourceName, $"File '{sourceName}' is not a valid JPEG image.");
        }

        int pos = 2;
        while (pos < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                break;
            }

            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                break;
            }

            byte marker = data[pos++];

            // End of image, start of scan, or stuffed byte ends header scanning
            if (marker is 0xD9 or 0xDA or 0x00)
            {
                break;
            }

            // Standalone markers without payload length
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (pos + 2 > data.Length)
            {
                break;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            if (segmentLength < 2 || pos + segmentLength > data.Length)
            {
                break;
            }

            if (IsJpegStartOfFrameMarker(marker))
            {
                const int minSofSegmentLength = 7;
                if (segmentLength < minSofSegmentLength)
                {
                    break;
                }

                int height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 3, 2));
                int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 5, 2));

                if (width <= 0 || height <= 0)
                {
                    throw new AttachmentConversionException(sourceName, $"JPEG image '{sourceName}' has invalid dimensions ({width}x{height}).");
                }

                return new ImageHeaderInfo("image/jpeg", width, height);
            }

            pos += segmentLength;
        }

        throw new AttachmentConversionException(sourceName, $"JPEG image '{sourceName}' does not contain a valid Start-of-Frame header.");
    }

    private static bool IsJpegStartOfFrameMarker(byte marker) =>
        marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);

    private static ImageHeaderInfo InspectWebp(ReadOnlySpan<byte> data, string sourceName)
    {
        const int minWebpHeaderBytes = 20;
        if (data.Length < minWebpHeaderBytes ||
            !data[..RiffSignature.Length].SequenceEqual(RiffSignature) ||
            !data.Slice(8, WebpSignature.Length).SequenceEqual(WebpSignature))
        {
            throw new AttachmentConversionException(sourceName, $"File '{sourceName}' is not a valid WebP image.");
        }

        var chunkHeader = data.Slice(12, 4);
        int width;
        int height;

        if (chunkHeader.SequenceEqual(Vp8Chunk))
        {
            const int minVp8Bytes = 30;
            if (data.Length < minVp8Bytes || data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
            {
                throw new AttachmentConversionException(sourceName, $"WebP VP8 image '{sourceName}' has a corrupt frame header.");
            }

            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
        }
        else if (chunkHeader.SequenceEqual(Vp8LChunk))
        {
            const int minVp8LBytes = 25;
            if (data.Length < minVp8LBytes || data[20] != 0x2F)
            {
                throw new AttachmentConversionException(sourceName, $"WebP VP8L image '{sourceName}' has a corrupt lossless header.");
            }

            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
        }
        else if (chunkHeader.SequenceEqual(Vp8XChunk))
        {
            const int minVp8XBytes = 30;
            if (data.Length < minVp8XBytes)
            {
                throw new AttachmentConversionException(sourceName, $"WebP VP8X image '{sourceName}' has a truncated extended header.");
            }

            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
        }
        else
        {
            throw new AttachmentConversionException(sourceName, $"WebP image '{sourceName}' uses an unsupported or corrupt chunk header.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new AttachmentConversionException(sourceName, $"WebP image '{sourceName}' has invalid dimensions ({width}x{height}).");
        }

        return new ImageHeaderInfo("image/webp", width, height);
    }
}
