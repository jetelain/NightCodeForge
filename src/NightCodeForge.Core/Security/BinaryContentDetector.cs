using System.Text;

namespace NightCodeForge.Core.Security;

/// <summary>
/// Reason a payload was classified as binary by <see cref="BinaryContentDetector"/>.
/// </summary>
public enum BinaryDetectionReason
{
    None = 0,
    NulByte = 1,
    HighControlCharacterDensity = 2,
    HighReplacementCharacterDensity = 3
}

/// <summary>
/// Result of inspecting a raw-byte or text payload for binary content.
/// </summary>
/// <param name="IsBinary">Whether the payload was classified as binary.</param>
/// <param name="Reason">The detection signal that triggered binary classification, if any.</param>
/// <param name="DetectedEncoding">Detected text encoding (UTF-8, UTF-16, or UTF-32).</param>
public sealed record BinaryContentInspectionResult(
    bool IsBinary,
    BinaryDetectionReason Reason,
    Encoding DetectedEncoding);

/// <summary>
/// Deterministic binary content detector for raw byte buffers and decoded text payloads.
/// Shared by repository file inspection and tool output guards.
/// </summary>
public static class BinaryContentDetector
{
    public const int DefaultRawByteSampleSize = 8 * 1024;
    public const double MaxControlCharacterRatio = 0.05d;
    public const int MinControlCharactersForDensityTrigger = 3;
    public const double ShortPayloadHighDensityRatio = 0.50d;
    public const int MinShortPayloadControlOrReplacementCount = 2;
    public const double MaxReplacementCharacterRatio = 0.05d;
    public const int MinReplacementCharactersForDensityTrigger = 3;
    public const char Utf8ReplacementChar = '\uFFFD';

    private const char EscapeChar = '\x1B';
    private const char BellChar = '\x07';

    private static readonly Encoding Utf8NoBomNonThrowing = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16LeNoBomNonThrowing = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16BeNoBomNonThrowing = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf32LeNoBomNonThrowing = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false);
    private static readonly Encoding Utf32BeNoBomNonThrowing = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false);

    private enum BomKind
    {
        None,
        Utf8,
        Utf16Le,
        Utf16Be,
        Utf32Le,
        Utf32Be
    }

    /// <summary>
    /// Inspects raw bytes (optionally bounded to <paramref name="maxSampleBytes"/>) for binary signals.
    /// </summary>
    public static BinaryContentInspectionResult InspectBytes(ReadOnlySpan<byte> rawBytes, int? maxSampleBytes = null) =>
        InspectAndDecodeCore(rawBytes, maxSampleBytes, decodeText: false, stripAnsiSequences: true, out _);

    /// <summary>
    /// Returns true when the raw byte buffer is classified as binary.
    /// </summary>
    public static bool IsBinary(ReadOnlySpan<byte> rawBytes, int? maxSampleBytes = null) =>
        InspectBytes(rawBytes, maxSampleBytes).IsBinary;

    /// <summary>
    /// Inspects decoded text for NUL characters, non-printable control character density, and replacement character density.
    /// </summary>
    public static BinaryContentInspectionResult InspectText(ReadOnlySpan<char> text, bool stripAnsiSequences = true)
    {
        if (text.IsEmpty)
        {
            return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, Utf8NoBomNonThrowing);
        }

        var analyzedLength = 0;
        var controlCount = 0;
        var replacementCount = 0;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\0')
            {
                return new BinaryContentInspectionResult(true, BinaryDetectionReason.NulByte, Utf8NoBomNonThrowing);
            }

            if (stripAnsiSequences && ch == EscapeChar && TrySkipAnsiEscapeSequence(text, i, out var nextIndex))
            {
                i = nextIndex - 1;
                continue;
            }

            analyzedLength++;

            if (ch == Utf8ReplacementChar)
            {
                replacementCount++;
            }
            else if (IsNonPrintableControlChar(ch))
            {
                controlCount++;
            }
        }

        if (analyzedLength == 0)
        {
            return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, Utf8NoBomNonThrowing);
        }

        var controlRatio = (double)controlCount / analyzedLength;
        if ((controlCount >= MinControlCharactersForDensityTrigger && controlRatio >= MaxControlCharacterRatio) ||
            (controlCount >= MinShortPayloadControlOrReplacementCount && controlRatio >= ShortPayloadHighDensityRatio))
        {
            return new BinaryContentInspectionResult(true, BinaryDetectionReason.HighControlCharacterDensity, Utf8NoBomNonThrowing);
        }

        var replacementRatio = (double)replacementCount / analyzedLength;
        if ((replacementCount >= MinReplacementCharactersForDensityTrigger && replacementRatio >= MaxReplacementCharacterRatio) ||
            (replacementCount >= MinShortPayloadControlOrReplacementCount && replacementRatio >= ShortPayloadHighDensityRatio))
        {
            return new BinaryContentInspectionResult(true, BinaryDetectionReason.HighReplacementCharacterDensity, Utf8NoBomNonThrowing);
        }

        return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, Utf8NoBomNonThrowing);
    }

    /// <summary>
    /// Inspects the full byte buffer and decodes it to text (stripping any recognized BOM) in a single pass if it is not binary.
    /// </summary>
    public static bool TryDecodeText(
        ReadOnlySpan<byte> rawBytes,
        out string text,
        out BinaryContentInspectionResult inspection)
    {
        inspection = InspectAndDecodeCore(rawBytes, maxSampleBytes: null, decodeText: true, stripAnsiSequences: true, out text);
        return !inspection.IsBinary;
    }

    /// <summary>
    /// Inspects up to <paramref name="maxSampleBytes"/> of <paramref name="rawBytes"/> (respecting BOM and multi-byte encoding boundaries)
    /// and returns a cleanly decoded preview string when the sampled bytes are non-binary.
    /// </summary>
    public static bool TryDecodeTextPreview(
        ReadOnlySpan<byte> rawBytes,
        int maxSampleBytes,
        out string preview,
        out BinaryContentInspectionResult inspection)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSampleBytes);
        inspection = InspectAndDecodeCore(rawBytes, maxSampleBytes, decodeText: true, stripAnsiSequences: true, out preview);
        return !inspection.IsBinary;
    }

    private static BinaryContentInspectionResult InspectAndDecodeCore(
        ReadOnlySpan<byte> rawBytes,
        int? maxSampleBytes,
        bool decodeText,
        bool stripAnsiSequences,
        out string decodedText)
    {
        decodedText = string.Empty;
        if (rawBytes.IsEmpty)
        {
            return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, Utf8NoBomNonThrowing);
        }

        var (bomKind, bomLength, encoding) = DetectBom(rawBytes);
        var wasTruncated = maxSampleBytes.HasValue && maxSampleBytes.Value > 0 && rawBytes.Length > maxSampleBytes.Value;
        var sample = wasTruncated ? rawBytes[..maxSampleBytes!.Value] : rawBytes;

        if (sample.Length <= bomLength)
        {
            return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, encoding);
        }

        var payload = sample[bomLength..];

        if (bomKind is BomKind.Utf32Le or BomKind.Utf32Be or BomKind.Utf16Le or BomKind.Utf16Be)
        {
            var unitBytes = bomKind is BomKind.Utf32Le or BomKind.Utf32Be ? 4 : 2;
            if (wasTruncated)
            {
                var alignedLength = payload.Length - (payload.Length % unitBytes);
                payload = payload[..alignedLength];
            }

            if (payload.IsEmpty)
            {
                return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, encoding);
            }

            var decodedUnicode = encoding.GetString(payload);
            var textResult = InspectText(decodedUnicode, stripAnsiSequences: stripAnsiSequences) with { DetectedEncoding = encoding };
            if (decodeText && !textResult.IsBinary)
            {
                decodedText = decodedUnicode;
            }

            return textResult;
        }

        if (payload.IndexOf((byte)0) >= 0)
        {
            return new BinaryContentInspectionResult(true, BinaryDetectionReason.NulByte, encoding);
        }

        if (wasTruncated)
        {
            payload = TrimIncompleteTrailingUtf8Sequence(payload);
        }

        if (payload.IsEmpty)
        {
            return new BinaryContentInspectionResult(false, BinaryDetectionReason.None, encoding);
        }

        var decodedUtf8 = Utf8NoBomNonThrowing.GetString(payload);
        var utf8Result = InspectText(decodedUtf8, stripAnsiSequences: stripAnsiSequences);
        if (decodeText && !utf8Result.IsBinary)
        {
            decodedText = decodedUtf8;
        }

        return utf8Result;
    }

    private static (BomKind Kind, int Length, Encoding Encoding) DetectBom(ReadOnlySpan<byte> rawBytes)
    {
        // Check 4-byte UTF-32 BOMs BEFORE 2-byte UTF-16 BOMs because UTF-32 LE (FF FE 00 00) starts with FF FE.
        if (rawBytes.Length >= 4)
        {
            if (rawBytes[0] == 0xFF && rawBytes[1] == 0xFE && rawBytes[2] == 0x00 && rawBytes[3] == 0x00)
            {
                return (BomKind.Utf32Le, 4, Utf32LeNoBomNonThrowing);
            }

            if (rawBytes[0] == 0x00 && rawBytes[1] == 0x00 && rawBytes[2] == 0xFE && rawBytes[3] == 0xFF)
            {
                return (BomKind.Utf32Be, 4, Utf32BeNoBomNonThrowing);
            }
        }

        if (rawBytes.Length >= 3 &&
            rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
        {
            return (BomKind.Utf8, 3, Utf8NoBomNonThrowing);
        }

        if (rawBytes.Length >= 2)
        {
            if (rawBytes[0] == 0xFF && rawBytes[1] == 0xFE)
            {
                return (BomKind.Utf16Le, 2, Utf16LeNoBomNonThrowing);
            }

            if (rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
            {
                return (BomKind.Utf16Be, 2, Utf16BeNoBomNonThrowing);
            }
        }

        return (BomKind.None, 0, Utf8NoBomNonThrowing);
    }

    private static ReadOnlySpan<byte> TrimIncompleteTrailingUtf8Sequence(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return payload;
        }

        var lastByte = payload[^1];
        if (lastByte < 0x80)
        {
            return payload;
        }

        // Walk back up to 3 continuation bytes (10xxxxxx) to locate the leading byte.
        var idx = payload.Length - 1;
        var minIdx = Math.Max(0, payload.Length - 4);
        while (idx > minIdx && (payload[idx] & 0xC0) == 0x80)
        {
            idx--;
        }

        var lead = payload[idx];
        int expectedLength;
        if ((lead & 0x80) == 0)
        {
            expectedLength = 1;
        }
        else if (lead is >= 0xC2 and <= 0xDF)
        {
            expectedLength = 2;
        }
        else if (lead is >= 0xE0 and <= 0xEF)
        {
            expectedLength = 3;
        }
        else if (lead is >= 0xF0 and <= 0xF4)
        {
            expectedLength = 4;
        }
        else
        {
            // Invalid leading/continuation byte (e.g. 0xC0, 0xC1, 0xF5..0xFF, or stray continuation byte);
            // leave intact so inspection catches actual corruption.
            return payload;
        }

        var availableLength = payload.Length - idx;
        return availableLength < expectedLength ? payload[..idx] : payload;
    }

    private static bool IsNonPrintableControlChar(char ch) =>
        (ch < 0x20 && ch != '\t' && ch != '\r' && ch != '\n') || ch == 0x7F;

    private static bool TrySkipAnsiEscapeSequence(ReadOnlySpan<char> text, int escIndex, out int nextIndex)
    {
        nextIndex = escIndex + 1;
        if (nextIndex >= text.Length)
        {
            return false;
        }

        var prefix = text[nextIndex];
        if (prefix == '[')
        {
            // CSI sequence: ESC [ <parameter/intermediate bytes 0x20..0x3F> <final byte 0x40..0x7E>
            var i = nextIndex + 1;
            while (i < text.Length)
            {
                var c = text[i];
                if (c >= 0x40 && c <= 0x7E)
                {
                    nextIndex = i + 1;
                    return true;
                }

                if (c < 0x20 || c > 0x3F)
                {
                    return false;
                }

                i++;
            }

            return false;
        }

        if (prefix == ']')
        {
            // OSC sequence: ESC ] ... (BEL \x07 or ST ESC \)
            var i = nextIndex + 1;
            while (i < text.Length)
            {
                if (text[i] == BellChar)
                {
                    nextIndex = i + 1;
                    return true;
                }

                if (text[i] == EscapeChar && i + 1 < text.Length && text[i + 1] == '\\')
                {
                    nextIndex = i + 2;
                    return true;
                }

                i++;
            }

            return false;
        }

        return false;
    }
}
