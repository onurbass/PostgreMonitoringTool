namespace PgMonitor.Core.Buffers;

/// <summary>
/// Bounded in-memory history. Keeps at most <c>capacity</c> items and at most <c>maxBytes</c> estimated bytes;
/// the oldest items are dropped first. Not thread-safe: owned by the single monitor loop.
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly LinkedList<(T Item, long Bytes)> _items = new();
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly Func<T, long> _sizeOf;

    public RingBuffer(int capacity, long maxBytes, Func<T, long> sizeOf)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentNullException.ThrowIfNull(sizeOf);
        _capacity = capacity;
        _maxBytes = maxBytes;
        _sizeOf = sizeOf;
    }

    public int Count => _items.Count;
    public long TotalBytes { get; private set; }

    public void Add(T item)
    {
        var bytes = Math.Max(0, _sizeOf(item));
        _items.AddLast((item, bytes));
        TotalBytes += bytes;
        // Always keep the newest item, even if it alone exceeds the byte budget.
        while (_items.Count > 1 && (_items.Count > _capacity || TotalBytes > _maxBytes))
        {
            TotalBytes -= _items.First!.Value.Bytes;
            _items.RemoveFirst();
        }
    }

    /// <summary>Oldest-first copy of the current content.</summary>
    public IReadOnlyList<T> Snapshot() => _items.Select(x => x.Item).ToList();

    public void Clear()
    {
        _items.Clear();
        TotalBytes = 0;
    }
}
