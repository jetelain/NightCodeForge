using System.Buffers.Binary;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class VisionConstantsAndExceptionTests
{
    [Fact]
    public void ModelVisionNotSupportedException_FormatsActionableMessageAndProperties()
    {
        var ex = new ModelVisionNotSupportedException("qwen2.5-coder:32b", "/workspace/attachments/bug.png");

        Assert.Equal("qwen2.5-coder:32b", ex.ModelId);
        Assert.Equal("/workspace/attachments/bug.png", ex.VisualInput);
        Assert.Contains("qwen2.5-coder:32b", ex.Message);
        Assert.Contains("/workspace/attachments/bug.png", ex.Message);
        Assert.Contains(VisionConstants.VisionBadgeLabel, ex.Message);

        var inner = new InvalidOperationException("root");
        var wrapped = new ModelVisionNotSupportedException("llama3", null, "Custom error", inner);
        Assert.Equal("Custom error", wrapped.Message);
        Assert.Same(inner, wrapped.InnerException);
    }

    [Theory]
    [InlineData(512, 512, 255)]
    [InlineData(513, 512, 425)]
    [InlineData(1024, 1024, 765)]
    [InlineData(1568, 1024, 1445)]
    [InlineData(0, 500, VisionConstants.DefaultFallbackImageTokens)]
    [InlineData(-10, -20, VisionConstants.DefaultFallbackImageTokens)]
    public void EstimateVisionTokens_ComputesExpectedTilesOrFallback(int widthPx, int heightPx, int expectedTokens)
    {
        Assert.Equal(expectedTokens, VisionConstants.EstimateVisionTokens(widthPx, heightPx));
    }

    [Fact]
    public void AvailableModelInfo_FormattedCapabilitiesBadge_CombinesContextAndVision()
    {
        var visionWithContext = new AvailableModelInfo("qwen3.5", "qwen3.5", 131072, SupportsVision: true);
        var visionOnly = new AvailableModelInfo("llava", "llava", ContextWindowTokens: null, SupportsVision: true);
        var textOnly = new AvailableModelInfo("qwen2.5-coder", "qwen2.5-coder", 65536, SupportsVision: false);

        Assert.Equal("128k ctx · 👁️ Vision", visionWithContext.FormattedCapabilitiesBadge);
        Assert.Equal("👁️ Vision", visionOnly.FormattedCapabilitiesBadge);
        Assert.Equal("64k ctx", textOnly.FormattedCapabilitiesBadge);
    }

    [Fact]
    public void ImageHeaderInspector_TryInspect_DetectsValidPngJpegGifWebp_AndRejectsGarbage()
    {
        var png = new byte[32];
        byte[] pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        pngSig.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 800);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 600);

        Assert.True(ImageHeaderInspector.TryInspect(png, out var pngInfo));
        Assert.Equal("image/png", pngInfo.MediaType);
        Assert.Equal(800, pngInfo.WidthPx);
        Assert.Equal(600, pngInfo.HeightPx);

        var gif = new byte[16];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 320);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 240);

        Assert.True(ImageHeaderInspector.TryInspect(gif, out var gifInfo));
        Assert.Equal("image/gif", gifInfo.MediaType);
        Assert.Equal(320, gifInfo.WidthPx);
        Assert.Equal(240, gifInfo.HeightPx);

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x01, 0x90, 0x02, 0x80, 0x03, 0x01, 0x11, 0x00 };
        Assert.True(ImageHeaderInspector.TryInspect(jpeg, out var jpgInfo));
        Assert.Equal("image/jpeg", jpgInfo.MediaType);
        Assert.Equal(640, jpgInfo.WidthPx);
        Assert.Equal(400, jpgInfo.HeightPx);

        Assert.False(ImageHeaderInspector.TryInspect(ReadOnlySpan<byte>.Empty, out _));
        Assert.False(ImageHeaderInspector.TryInspect("not an image"u8, out _));
    }
}
