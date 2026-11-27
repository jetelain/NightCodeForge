using System.Text;
using NightCodeForge.Core.Security;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class BinaryContentDetectorTests
{
    [Fact]
    public void InspectBytes_EmptyBuffer_IsNotBinary()
    {
        var result = BinaryContentDetector.InspectBytes([]);

        Assert.False(result.IsBinary);
        Assert.Equal(BinaryDetectionReason.None, result.Reason);
    }

    [Fact]
    public void InspectText_EmptyAndWhitespace_IsNotBinary()
    {
        Assert.False(BinaryContentDetector.InspectText("").IsBinary);
        Assert.False(BinaryContentDetector.InspectText("   \r\n\t  ").IsBinary);
    }

    [Fact]
    public void InspectBytes_ValidMultilingualUtf8AndSplitMultiByteAtSampleBoundary_IsNotBinary()
    {
        // 3-byte CJK + 4-byte emoji placed across an 8-byte sample boundary
        var text = "Hello café 日本語 🚀✨!";
        var bytes = Encoding.UTF8.GetBytes(text);

        Assert.False(BinaryContentDetector.IsBinary(bytes, maxSampleBytes: 8));
        Assert.False(BinaryContentDetector.IsBinary(bytes, maxSampleBytes: 12));
        Assert.False(BinaryContentDetector.IsBinary(bytes, maxSampleBytes: 14));
        Assert.True(BinaryContentDetector.TryDecodeText(bytes, out var decoded, out var inspection));
        Assert.False(inspection.IsBinary);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void InspectBytes_Utf16AndUtf32BomValidText_AreNotClassifiedAsBinaryDueToNullBytes()
    {
        var sampleText = "public class Hello { /* café 日本語 */ }\n";

        var utf16Le = WithBom([0xFF, 0xFE], Encoding.Unicode.GetBytes(sampleText));
        var utf16Be = WithBom([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes(sampleText));
        var utf32Le = WithBom([0xFF, 0xFE, 0x00, 0x00], Encoding.UTF32.GetBytes(sampleText));
        var utf32Be = WithBom([0x00, 0x00, 0xFE, 0xFF], new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes(sampleText));

        foreach (var payload in new[] { utf16Le, utf16Be, utf32Le, utf32Be })
        {
            Assert.True(BinaryContentDetector.TryDecodeText(payload, out var decoded, out var inspection));
            Assert.False(inspection.IsBinary);
            Assert.Equal(sampleText, decoded);
        }
    }

    [Fact]
    public void InspectBytes_Utf32LeWithEmbeddedNullCodePoint_IsDetectedAsBinary()
    {
        var corrupted = WithBom([0xFF, 0xFE, 0x00, 0x00], [0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var result = BinaryContentDetector.InspectBytes(corrupted);

        Assert.True(result.IsBinary);
        Assert.Equal(BinaryDetectionReason.NulByte, result.Reason);
    }

    [Fact]
    public void InspectBytes_RawNulByte_TriggersNulByteBinaryDetection()
    {
        var bytes = "ELF\0\x01\x02\x03"u8.ToArray();
        var result = BinaryContentDetector.InspectBytes(bytes);

        Assert.True(result.IsBinary);
        Assert.Equal(BinaryDetectionReason.NulByte, result.Reason);
        Assert.False(BinaryContentDetector.TryDecodeText(bytes, out _, out _));
    }

    [Fact]
    public void InspectText_AnsiColoredLog_IsNotBinary()
    {
        var log = "\x1B[32mPASS\x1B[0m \x1B]8;;https://example.test\x07link\x1B]8;;\x07\nAll 42 tests passed.\n";
        var result = BinaryContentDetector.InspectText(log, stripAnsiSequences: true);

        Assert.False(result.IsBinary);
    }

    [Fact]
    public void InspectText_IsolatedStrayReplacementOrControlChar_DoesNotFalsePositive()
    {
        var validTextWithStray = new string('a', 200) + "\uFFFD" + new string('b', 200) + "\x01";
        var result = BinaryContentDetector.InspectText(validTextWithStray);

        Assert.False(result.IsBinary);
    }

    [Fact]
    public void InspectBytes_HighControlCharacterDensity_IsBinary()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, (byte)'A', (byte)'B' };
        var result = BinaryContentDetector.InspectBytes(bytes);

        Assert.True(result.IsBinary);
        Assert.Equal(BinaryDetectionReason.HighControlCharacterDensity, result.Reason);
    }

    [Fact]
    public void InspectBytes_Utf8EncodedReplacementSequences_IsDetectedAsBinary()
    {
        // U+FFFD encoded in UTF-8 is EF BF BD (what SystemProcessRunner produces when capturing invalid UTF-8)
        var bytes = new List<byte>();
        for (var i = 0; i < 10; i++)
        {
            bytes.AddRange([0xEF, 0xBF, 0xBD]);
        }
        bytes.AddRange("abc"u8.ToArray());

        var result = BinaryContentDetector.InspectBytes(bytes.ToArray());

        Assert.True(result.IsBinary);
        Assert.Equal(BinaryDetectionReason.HighReplacementCharacterDensity, result.Reason);
    }

    [Fact]
    public void TryDecodeText_StripsUtf8Bom()
    {
        var bytes = WithBom([0xEF, 0xBB, 0xBF], "hello\nworld"u8.ToArray());
        Assert.True(BinaryContentDetector.TryDecodeText(bytes, out var decoded, out _));
        Assert.Equal("hello\nworld", decoded);
    }

    [Fact]
    public void TryDecodeTextPreview_DecodesUtf16BomAndRespectsUtf8MultiByteBoundaries()
    {
        var utf16Payload = WithBom([0xFF, 0xFE], Encoding.Unicode.GetBytes("Hello World\nLine 2"));
        Assert.True(BinaryContentDetector.TryDecodeTextPreview(utf16Payload, maxSampleBytes: 15, out var utf16Preview, out var utf16Info));
        Assert.False(utf16Info.IsBinary);
        Assert.Equal("Hello ", utf16Preview);

        var utf8EmojiBytes = Encoding.UTF8.GetBytes("Hi 🚀!");
        Assert.True(BinaryContentDetector.TryDecodeTextPreview(utf8EmojiBytes, maxSampleBytes: 5, out var utf8Preview, out var utf8Info));
        Assert.False(utf8Info.IsBinary);
        Assert.Equal("Hi ", utf8Preview);

        var binaryBytes = "ELF\0\x01\x02"u8.ToArray();
        Assert.False(BinaryContentDetector.TryDecodeTextPreview(binaryBytes, maxSampleBytes: 16, out var binPreview, out var binInfo));
        Assert.True(binInfo.IsBinary);
        Assert.Equal(string.Empty, binPreview);
    }

    [Fact]
    public void InspectBytes_InvalidUtf8OverlongLeadByteAtSampleBoundary_IsNotSilentlyTrimmed()
    {
        var bytes = new byte[] { (byte)'a', 0xC0, 0xC0, (byte)'z' };
        var result = BinaryContentDetector.InspectBytes(bytes, maxSampleBytes: 3);

        Assert.True(result.IsBinary);
        Assert.Equal(BinaryDetectionReason.HighReplacementCharacterDensity, result.Reason);
    }

    private static byte[] WithBom(byte[] bom, byte[] body)
    {
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }
}
