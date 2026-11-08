namespace NightCodeForge.Web.Services;

/// <summary>
/// Fixed-capacity ring buffer and hash set for O(1) deduplication of dispatched notification signatures.
/// Prevents duplicate dispatches across reconnects, route navigations, and telemetry updates.
/// </summary>
public class RingBufferDeduplicator
{
    public const int DefaultCapacity = 100;

    private readonly int _capacity;
    private readonly Queue<string> _queue;
    private readonly HashSet<string> _set;
    private readonly object _lock = new();

    public RingBufferDeduplicator()
        : this(DefaultCapacity)
    {
    }

    public RingBufferDeduplicator(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        _capacity = capacity;
        _queue = new Queue<string>(capacity);
        _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to record the signature. Returns true if newly added; returns false if already present.
    /// </summary>
    public bool TryAdd(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        lock (_lock)
        {
            if (_set.Contains(signature))
            {
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                var oldest = _queue.Dequeue();
                _set.Remove(oldest);
            }

            _queue.Enqueue(signature);
            _set.Add(signature);
            return true;
        }
    }

    /// <summary>
    /// Checks whether the signature is already recorded in the buffer.
    /// </summary>
    public bool Contains(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        lock (_lock)
        {
            return _set.Contains(signature);
        }
    }

    /// <summary>
    /// Clears all recorded signatures.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _set.Clear();
        }
    }
}

