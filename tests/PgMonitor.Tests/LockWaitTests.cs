using Microsoft.Data.Sqlite;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Locks;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;
using PgMonitor.Infrastructure.Runtime;
using PgMonitor.Infrastructure.Storage;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

/// <summary>Every lock wait of at least a few seconds is recorded (no incident, no mail), and events persist.</summary>
public sealed class LockWaitTests : IDisposable
{
    private static readonly TimeSpan Min = TimeSpan.FromSeconds(5);
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

    private static IReadOnlyList<LockWaitEvent> Update(LockWaitTracker tracker, ClusterSample s) =>
        tracker.Update(s, LockGraph.Analyze(s.Sessions, s.SampledAt), Min, ChangeEvery);

    [Fact]
    public void Waits_shorter_than_the_minimum_are_not_recorded()
    {
        var tracker = new LockWaitTracker();
        Assert.Empty(Update(tracker, Blocked(T0, TimeSpan.FromSeconds(4))));
    }

    [Fact]
    public void A_short_wait_is_recorded_once_with_blocker_root_and_end()
    {
        var tracker = new LockWaitTracker();
        var started = Update(tracker, Blocked(T0, TimeSpan.FromSeconds(6)));
        Assert.Empty(Update(tracker, Blocked(T0.AddSeconds(5), TimeSpan.FromSeconds(11))));
        var ended = Update(tracker, Sample(T0.AddSeconds(10), IdleInTx(100, T0.AddSeconds(10), TimeSpan.FromMinutes(2)), Idle(200) with { State = "active" }));

        var s = Assert.Single(started);
        Assert.Equal(LockWaitEventKind.Started, s.Kind);
        Assert.Equal([100], s.Blockers);
        Assert.Equal(100, s.RootPid);
        Assert.Equal("idle in transaction", s.Root!.State);

        var e = Assert.Single(ended);
        Assert.Equal(LockWaitEventKind.Ended, e.Kind);
        Assert.Equal(LockWaitTracker.ReasonWaitEnded, e.EndReason);
        Assert.Equal(T0.AddSeconds(5), e.LastSeen);
    }

    [Fact]
    public void Root_is_followed_through_a_two_level_chain()
    {
        var tracker = new LockWaitTracker();
        var s = Sample(T0,
            IdleInTx(100, T0, TimeSpan.FromMinutes(5)),
            LockWaiter(200, T0, TimeSpan.FromSeconds(20), blockedBy: 100),
            LockWaiter(300, T0, TimeSpan.FromSeconds(10), blockedBy: 200));

        var events = Update(tracker, s);

        var deep = events.Single(e => e.Key.Pid == 300);
        Assert.Equal([200], deep.Blockers);
        Assert.Equal(100, deep.RootPid);
    }

    [Fact]
    public void Root_search_is_cycle_safe_and_handles_prepared_transactions()
    {
        var cycle = new Dictionary<int, IReadOnlyList<int>> { [1] = [2], [2] = [1] };
        Assert.NotNull(LockWaitTracker.FindRoot(1, cycle));
        var prepared = new Dictionary<int, IReadOnlyList<int>> { [5] = [0] };
        Assert.Equal(0, LockWaitTracker.FindRoot(5, prepared));
        Assert.Null(LockWaitTracker.FindRoot(9, prepared));
    }

    [Fact]
    public void Vanished_waiter_and_failed_samples()
    {
        var tracker = new LockWaitTracker();
        Update(tracker, Blocked(T0, TimeSpan.FromSeconds(6)));
        Assert.Empty(tracker.Update(ClusterSample.Failed(T0.AddSeconds(5), CollectFailureKind.Timeout), LockGraph.Analyze([], T0), Min, ChangeEvery));
        var ended = Update(tracker, Sample(T0.AddSeconds(10), IdleInTx(100, T0.AddSeconds(10), TimeSpan.FromMinutes(2))));
        Assert.Equal(LockWaitTracker.ReasonSessionGone, Assert.Single(ended).EndReason);
    }

    [Fact]
    public void Records_are_stored_with_both_statements_and_closed_on_restart()
    {
        var path = Path.Combine(_dir, "lw.db");
        var tracker = new LockWaitTracker();
        using (var store = new SqliteMonitorStore(path))
        {
            var first = Blocked(T0, TimeSpan.FromSeconds(6), waiters: 2);
            store.RecordLockWaits(Update(tracker, first));
            store.RecordLockWaits(Update(tracker, Blocked(T0.AddSeconds(10), TimeSpan.FromSeconds(16), waiters: 1)));
            store.RecordLockWaits(Update(tracker, Blocked(T0.AddSeconds(70), TimeSpan.FromSeconds(76), waiters: 1))); // periodic update
            store.RunMaintenance(T0.AddMinutes(2), new StorageSettings()); // must keep SQL referenced by lock waits
        }
        using (var store = new SqliteMonitorStore(path))
        {
            store.MarkOpenIncidentsInterrupted(T0.AddHours(1));
            var list = store.ListLockWaits();
            Assert.Equal(2, list.Count);
            var ended = list.Single(w => w.Pid == 201);
            Assert.Equal(LockWaitTracker.ReasonSessionGone, ended.EndReason);
            Assert.Equal(TimeSpan.FromSeconds(6), ended.MaxWait);
            Assert.Equal([100], ended.Blockers);
            Assert.Equal(100, ended.RootPid);
            Assert.Equal("idle in transaction", ended.RootState);
            Assert.StartsWith("UPDATE", ended.Query, StringComparison.Ordinal);
            Assert.Equal("SELECT 1", ended.RootQuery);
            var open = list.Single(w => w.Pid == 200);
            Assert.Equal("uygulama kapandı – sonu görülmedi", open.EndReason);
            Assert.Equal(TimeSpan.FromSeconds(76), open.MaxWait); // kept up to date while it lasted, not stuck at 6 s
        }
    }

    [Fact]
    public async Task S05_records_every_chain_waiter_with_the_root_and_events_survive_restart()
    {
        var s = Scenarios.S05LockChain;
        var path = Path.Combine(_dir, "s05.db");
        int rootPid;
        using (var store = new SqliteMonitorStore(path))
        {
            var source = new SimulatedSampleSource(s);
            var loop = new MonitorLoop(source, store, new MonitorOptions(), null, null, new ManualTime(s.Start), new ListLogger<MonitorLoop>());
            var changes = 0;
            loop.Updated += (_, snap) => changes += snap.LockWaitChanges;
            while (!source.IsExhausted)
            {
                await loop.TickAsync(TestContext.Current.CancellationToken);
            }
            Assert.True(changes > 0);
            rootPid = store.ListLongTransactions().Single().Pid; // S05's root also becomes a long transaction
        }

        using (var store = new SqliteMonitorStore(path))
        {
            var chain = store.ListLockWaits().Where(w => w.RootPid == rootPid).ToList();
            Assert.Equal(8, chain.Count);
            Assert.All(chain, w => Assert.Equal(LockWaitTracker.ReasonSessionGone, w.EndReason)); // DBA terminated them
            Assert.Contains(chain, w => w.MaxWait >= TimeSpan.FromMinutes(9));
            // short noise waits (5-8.5 s) that never opened an incident are recorded too
            Assert.Contains(store.ListLockWaits(), w => w.RootPid != rootPid && w.MaxWait < TimeSpan.FromSeconds(10));

            // "Son olaylar" now comes from SQLite: still there after a restart, newest first
            var events = store.ListRecentEvents();
            Assert.Equal(IncidentEventKind.Closed, events[0].Kind);
            Assert.Contains(events, e => e.Kind == IncidentEventKind.Opened);
        }
    }

    [Fact]
    public void Events_outside_incidents_are_listed_too()
    {
        using var store = new SqliteMonitorStore(Path.Combine(_dir, "ev.db"));
        store.AppendEvents([new IncidentEvent(IncidentEventKind.GapStarted, 0, T0, Severity.None, "Veri alınamadı: Timeout")]);
        var e = Assert.Single(store.ListRecentEvents());
        Assert.Null(e.IncidentId);
        Assert.Equal(IncidentEventKind.GapStarted, e.Kind);
    }

    [Fact]
    public void Version_3_database_gets_the_lock_wait_table()
    {
        var path = Path.Combine(_dir, "v3.db");
        using (new SqliteMonitorStore(path))
        {
        }
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE lock_wait; PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }
        using (var store = new SqliteMonitorStore(path))
        {
            Assert.Empty(store.ListLockWaits());
        }
    }

    [Fact]
    public void Minimum_recorded_wait_is_validated()
    {
        var s = new AppSettings();
        Assert.Equal(TimeSpan.FromSeconds(5), s.Incident.RecordLockWaitsFrom);
        Assert.NotEmpty((s with { Incident = s.Incident with { RecordLockWaitsFrom = TimeSpan.Zero } }).Validate());
    }
}
