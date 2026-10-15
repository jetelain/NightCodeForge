using System.Text;

namespace NightCodeForge.Agents.Transcripts;

/// <summary>
/// Coalesces streamed deltas (thinking / assistant tokens) into single transcript entries so that a
/// transcript step represents a whole reasoning or answer segment instead of one entry per token chunk.
/// A segment is closed when the role changes, when <see cref="Flush"/> is called (e.g. on a tool call), or at the end of the run.
/// </summary>
public sealed class TranscriptStreamBuffer
{
    private readonly List<TranscriptEntry> _entries;
    private readonly StringBuilder _buffer = new();
    private string? _role;
    private DateTime _startedAt;

    public TranscriptStreamBuffer(List<TranscriptEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
    }

    public void Append(string role, string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;

        if (_role != null && !string.Equals(_role, role, StringComparison.Ordinal))
        {
            Flush();
        }

        if (_role == null)
        {
            _role = role;
            _startedAt = DateTime.UtcNow;
        }

        _buffer.Append(delta);
    }

    public void Flush()
    {
        if (_role != null && _buffer.Length > 0)
        {
            _entries.Add(new TranscriptEntry(_startedAt, _role, _buffer.ToString()));
        }

        _role = null;
        _buffer.Clear();
    }
}
