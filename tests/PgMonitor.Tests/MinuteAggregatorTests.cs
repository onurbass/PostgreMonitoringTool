using PgMonitor.Core.Aggregation;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

public class MinuteAggregatorTests
{
    private static readonly IncidentSettings Settings = new();

    private static AggregateRow? Add(MinuteAggregator a, ClusterSample s) =>
        a.Add(s, s.Succeeded ? IncidentEngine.EvaluateLock(s, Settings) : null);

    [Fact]
    public void Emits_a_row_when_the_next_window_starts()
    {
        var a = new MinuteAggregator();
        for (var i = 0; i < 12; i++)
        {
            Assert.Null(Add(a, Healthy(T0.AddSeconds(i * 5))));
        }
        var row = Add(a, Healthy(T0.AddMinutes(1)));
        Assert.NotNull(row);
        Assert.Equal(T0, row.WindowStart);
        Assert.Equal(12, row.Samples);
        Assert.Equal(2, row.MaxSessions);
    }

    [Fact]
    public void Keeps_maxima_and_counts_failures()
    {
        var a = new MinuteAggregator();
        Add(a, Healthy(T0));
        Add(a, Blocked(T0.AddSeconds(5), TimeSpan.FromSeconds(50), waiters: 4));
        Add(a, ClusterSample.Failed(T0.AddSeconds(10), CollectFailureKind.Timeout));
        Add(a, Healthy(T0.AddSeconds(15)) with { Visibility = Visibility.Partial });

        var row = a.Flush()!;
        Assert.Equal(4, row.Samples);
        Assert.Equal(1, row.FailedSamples);
        Assert.Equal(4, row.MaxLockWaiters);
        Assert.Equal(4, row.MaxLockWaitersOverThreshold);
        Assert.Equal(50, row.MaxLockWaitSeconds);
        Assert.Equal(1, row.MaxIdleInTransaction);
        Assert.Equal(Visibility.Partial, row.WorstVisibility);
        Assert.Null(a.Flush());
    }

    [Fact]
    public void All_failed_window_has_unknown_visibility()
    {
        var a = new MinuteAggregator();
        Add(a, ClusterSample.Failed(T0, CollectFailureKind.Disconnected));
        Assert.Equal(Visibility.Unknown, a.Flush()!.WorstVisibility);
    }
}
