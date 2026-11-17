using PgMonitor.Core.Buffers;

namespace PgMonitor.Tests;

public class RingBufferTests
{
    [Fact]
    public void Drops_oldest_when_count_exceeds_capacity()
    {
        var buffer = new RingBuffer<int>(3, 1_000, _ => 1);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }
        Assert.Equal([3, 4, 5], buffer.Snapshot());
        Assert.Equal(3, buffer.TotalBytes);
    }

    [Fact]
    public void Drops_oldest_when_bytes_exceed_budget()
    {
        var buffer = new RingBuffer<string>(100, 10, s => s.Length);
        buffer.Add("aaaa");
        buffer.Add("bbbb");
        buffer.Add("cccc");
        Assert.Equal(["bbbb", "cccc"], buffer.Snapshot());
        Assert.Equal(8, buffer.TotalBytes);
    }

    [Fact]
    public void Keeps_newest_item_even_if_larger_than_budget()
    {
        var buffer = new RingBuffer<string>(10, 5, s => s.Length);
        buffer.Add("ab");
        buffer.Add("0123456789");
        Assert.Equal(["0123456789"], buffer.Snapshot());
    }

    [Fact]
    public void Snapshot_is_a_copy()
    {
        var buffer = new RingBuffer<int>(3, 100, _ => 1);
        buffer.Add(1);
        var snap = buffer.Snapshot();
        buffer.Add(2);
        Assert.Single(snap);
    }
}
