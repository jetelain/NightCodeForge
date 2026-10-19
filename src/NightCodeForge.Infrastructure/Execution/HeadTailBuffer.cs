using System.Text;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// UTF-8 rune-safe bounded accumulation buffer that keeps head and tail when output exceeds limit.
/// </summary>
public class HeadTailBuffer
{
    private readonly int _maxBytes;
    private readonly MemoryStream _totalStream = new();
    private long _totalBytesWritten = 0;
    private readonly object _lock = new();

    public const int DefaultMaxBytes = 32 * 1024; // 32 KB

    public HeadTailBuffer() : this(DefaultMaxBytes)
    {
    }

    public HeadTailBuffer(int maxBytes)
    {
        _maxBytes = Math.Max(1024, maxBytes);
    }

    public int MaxBytes => _maxBytes;
    public long TotalBytesWritten => _totalBytesWritten;
    public bool IsTruncated => _totalBytesWritten > _maxBytes;

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        Append(bytes, 0, bytes.Length);
    }

    public void Append(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_lock)
        {
            _totalBytesWritten += count;
            _totalStream.Write(buffer, offset, count);
        }
    }

    public string GetFormattedString()
    {
        lock (_lock)
        {
            var rawBytes = _totalStream.ToArray();
            if (rawBytes.Length <= _maxBytes)
            {
                return Encoding.UTF8.GetString(rawBytes);
            }

            int half = _maxBytes / 2;
            int headLength = FindRuneSafeEnd(rawBytes, half);
            int tailStart = FindRuneSafeStart(rawBytes, rawBytes.Length - half);
            int tailLength = rawBytes.Length - tailStart;
            long truncatedBytes = _totalBytesWritten - headLength - tailLength;

            var headStr = Encoding.UTF8.GetString(rawBytes, 0, headLength);
            var tailStr = Encoding.UTF8.GetString(rawBytes, tailStart, tailLength);
            var marker = $"\n[... truncated {truncatedBytes} bytes ...]\n";

            return headStr + marker + tailStr;
        }
    }

    private static int FindRuneSafeEnd(byte[] bytes, int targetLength)
    {
        if (targetLength >= bytes.Length) return bytes.Length;
        int index = targetLength;
        // Step back if index lands on a UTF-8 continuation byte (0b10xxxxxx)
        while (index > 0 && (bytes[index] & 0xC0) == 0x80)
        {
            index--;
        }
        return index;
    }

    private static int FindRuneSafeStart(byte[] bytes, int targetStart)
    {
        if (targetStart <= 0) return 0;
        int index = targetStart;
        // Step forward if index lands on a UTF-8 continuation byte (0b10xxxxxx)
        while (index < bytes.Length && (bytes[index] & 0xC0) == 0x80)
        {
            index++;
        }
        return index;
    }
}
