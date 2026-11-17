using Microsoft.Data.Sqlite;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;
using PgMonitor.Core.Transactions;
using PgMonitor.Infrastructure.Runtime;
using PgMonitor.Infrastructure.Storage;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

/// <summary>Long transactions: listed and recorded whether or not they block anyone (no incident, no mail).</summary>
public sealed class LongTransactionTests : IDisposable
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChangeEvery = TimeSpan.FromMinutes(1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgmonitor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A session whose transaction started at <paramref name="xactStart"/>; idle in transaction since <paramref name="idleSince"/>.</summary>
    private static SessionRow Tx(int pid, DateTimeOffset xactStart, DateTimeOffset idleSince, string query = "UPDATE x SET y = $1") =>
        Idle(pid) with
        {
            State = "idle in transaction",
            WaitEventType = "Client",
            WaitEvent = "ClientRead",
            TransactionStart = xactStart,
            QueryStart = idleSince.AddMilliseconds(-50),
            StateChange = idleSince,
            Query = query,
        };

    [Fact]
    public void Below_threshold_is_not_listed()
    {
        var tracker = new LongTransactionTracker();
        var events = tracker.Update(Sample(T0, Tx(1, T0.AddMinutes(-9), T0.AddMinutes(-9))), Threshold, ChangeEvery);
        Assert.Empty(events);
        Assert.Empty(tracker.Current);
    }

    [Fact]
    public void Crossing_the_threshold_starts_once_and_shows_all_three_durations()
    {
        var tracker = new LongTransactionTracker();
        var xs = T0.AddMinutes(-12);
        var row = Tx(1, xs, T0.AddMinutes(-7));

        var first = tracker.Update(Sample(T0, row), Threshold, ChangeEvery);
        var second = tracker.Update(Sample(T0.AddSeconds(10), row), Threshold, ChangeEvery);

        var started = Assert.Single(first);
        Assert.Equal(LongTxEventKind.Started, started.Kind);
        Assert.Empty(second);
        var current = Assert.Single(tracker.Current);
        Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(10), current.TransactionAge);
        Assert.Equal(TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(10), current.StateAge);   // idle in transaction for 7 min
        Assert.Equal(TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(10).Add(TimeSpan.FromMilliseconds(50)), current.QueryAge);
    }

    [Fact]
    public void Active_long_running_query_counts_too()
    {
        var tracker = new LongTransactionTracker();
        var running = Idle(2) with { State = "active", TransactionStart = T0.AddMinutes(-15), QueryStart = T0.AddMinutes(-15), StateChange = T0.AddMinutes(-15), Query = "SELECT big()" };
        Assert.Single(tracker.Update(Sample(T0, running), Threshold, ChangeEvery));
    }

    [Fact]
    public void Changes_are_reported_at_most_once_a_minute()
    {
        var tracker = new LongTransactionTracker();
        var xs = T0.AddMinutes(-20);
        tracker.Update(Sample(T0, Tx(1, xs, T0.AddMinutes(-1))), Threshold, ChangeEvery);

        var changes = 0;
        for (var i = 1; i <= 12; i++) // a new statement every 10 s for 2 minutes
        {
            var t = T0.AddSeconds(10 * i);
            changes += tracker.Update(Sample(t, Tx(1, xs, t.AddSeconds(-1))), Threshold, ChangeEvery).Count(e => e.Kind == LongTxEventKind.Changed);
        }
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Vanished_session_and_finished_transaction_end_with_different_reasons()
    {
        var tracker = new LongTransactionTracker();
        tracker.Update(Sample(T0, Tx(1, T0.AddMinutes(-11), T0), Tx(2, T0.AddMinutes(-11), T0)), Threshold, ChangeEvery);

        // pid 1 is gone (terminated or finished); pid 2 is still connected but idle, its transaction finished.
        var events = tracker.Update(Sample(T0.AddSeconds(10), Idle(2)), Threshold, ChangeEvery);

        Assert.Equal(LongTransactionTracker.ReasonSessionGone, events.Single(e => e.Key.Pid == 1).EndReason);
        Assert.Equal(LongTransactionTracker.ReasonTransactionEnded, events.Single(e => e.Key.Pid == 2).EndReason);
        Assert.All(events, e => Assert.Equal(T0, e.LastSeen));
        Assert.Empty(tracker.Current);
    }

    [Fact]
    public void Failed_samples_never_end_a_transaction()
    {
        var tracker = new LongTransactionTracker();
        tracker.Update(Sample(T0, Tx(1, T0.AddMinutes(-11), T0)), Threshold, ChangeEvery);
        Assert.Empty(tracker.Update(ClusterSample.Failed(T0.AddSeconds(10), CollectFailureKind.Disconnected), Threshold, ChangeEvery));
        Assert.Single(tracker.Current);
    }

    [Fact]
    public void Pid_reuse_is_a_different_transaction()
    {
        var tracker = new LongTransactionTracker();
        var old = Tx(1, T0.AddMinutes(-11), T0);
        tracker.Update(Sample(T0, old), Threshold, ChangeEvery);
        var reused = old with { BackendStart = T0.AddMinutes(-30), TransactionStart = T0.AddMinutes(-20) };

        var events = tracker.Update(Sample(T0.AddSeconds(10), reused), Threshold, ChangeEvery);

        Assert.Contains(events, e => e.Kind == LongTxEventKind.Ended && e.Key.BackendStart == old.BackendStart);
        Assert.Contains(events, e => e.Kind == LongTxEventKind.Started && e.Key.BackendStart == reused.BackendStart);
    }

    [Fact]
    public void Redacted_rows_are_ignored()
    {
        var tracker = new LongTransactionTracker();
        var hidden = Tx(1, T0.AddMinutes(-30), T0) with { IsRedacted = true };
        Assert.Empty(tracker.Update(Sample(T0, hidden), Threshold, ChangeEvery));
    }

    // ------------------------------------------------------------------ storage and pipeline

    [Fact]
    public void Records_survive_restart_and_open_ones_are_closed_as_unseen()
    {
        var path = Path.Combine(_dir, "lt.db");
        var tracker = new LongTransactionTracker();
        using (var store = new SqliteMonitorStore(path))
        {
            store.RecordLongTransactions(tracker.Update(Sample(T0, Tx(1, T0.AddMinutes(-11), T0, "UPDATE a SET b = $1"), Tx(2, T0.AddMinutes(-11), T0)), Threshold, ChangeEvery));
            store.RecordLongTransactions(tracker.Update(Sample(T0.AddSeconds(10), Tx(2, T0.AddMinutes(-11), T0)), Threshold, ChangeEvery));
            store.RunMaintenance(T0.AddSeconds(20), new StorageSettings()); // orphan SQL cleanup must keep long-tx SQL
        }
        using (var store = new SqliteMonitorStore(path))
        {
            store.MarkOpenIncidentsInterrupted(T0.AddHours(1));
            var list = store.ListLongTransactions();
            Assert.Equal(2, list.Count);
            var gone = list.Single(x => x.Pid == 1);
            Assert.Equal(LongTransactionTracker.ReasonSessionGone, gone.EndReason);
            Assert.Equal("UPDATE a SET b = $1", gone.Query);
            Assert.Equal("idle in transaction", gone.State);
            Assert.Equal(T0, gone.LastSeen);
            var unseen = list.Single(x => x.Pid == 2);
            Assert.Equal("uygulama kapandı – sonu görülmedi", unseen.EndReason);
        }
    }

    [Fact]
    public async Task S05_root_blocker_is_recorded_as_a_long_transaction_after_ten_minutes()
    {
        var s = Scenarios.S05LockChain;
        using var store = new SqliteMonitorStore(Path.Combine(_dir, "s05.db"));
        var source = new SimulatedSampleSource(s);
        var loop = new MonitorLoop(source, store, new MonitorOptions(), null, null, new ManualTime(s.Start), new ListLogger<MonitorLoop>());
        var maxListed = 0;
        loop.Updated += (_, snap) => maxListed = Math.Max(maxListed, snap.LongTransactions.Count);
        while (!source.IsExhausted)
        {
            await loop.TickAsync(TestContext.Current.CancellationToken);
        }

        // The root's transaction starts at 3 min, so it crosses 10 min at 13 min; the DBA terminates it at 15 min.
        var record = Assert.Single(store.ListLongTransactions());
        Assert.Equal("idle in transaction", record.State);
        Assert.Equal(s.Start + TimeSpan.FromMinutes(13), record.FirstSeen);
        Assert.Equal(s.Start + TimeSpan.FromMinutes(15), record.EndedAt);
        Assert.Equal(LongTransactionTracker.ReasonSessionGone, record.EndReason);
        Assert.Equal(1, maxListed);
        Assert.Empty(loop.Latest.LongTransactions);
    }

    [Fact]
    public void Threshold_must_be_at_least_one_minute()
    {
        var s = new AppSettings();
        Assert.Equal(TimeSpan.FromMinutes(10), s.Incident.LongTransactionThreshold);
        Assert.NotEmpty((s with { Incident = s.Incident with { LongTransactionThreshold = TimeSpan.FromSeconds(30) } }).Validate());
    }
}
