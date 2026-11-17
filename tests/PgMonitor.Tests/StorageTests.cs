using Microsoft.Data.Sqlite;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;
using PgMonitor.Infrastructure.Storage;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

/// <summary>SQLite store and evidence recorder on temp files (TEST_PLAN 4).</summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgmonitor-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "monitor-test.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Runs a scenario through engine + recorder into the store, optionally stopping early.</summary>
    private static void Record(IMonitorStore store, Scenario scenario, TimeSpan? stopAfter = null)
    {
        var recorder = new EvidenceRecorder(store);
        var sim = new Simulator(scenario);
        var state = EngineState.Initial with { LastIncidentId = store.MaxIncidentId() };
        var settings = new IncidentSettings();
        while (sim.HasNext)
        {
            var sample = sim.Next();
            if (stopAfter is { } stop && sample.SampledAt >= scenario.Start + stop)
            {
                return; // simulates the app being closed
            }
            var result = IncidentEngine.Process(state, sample, settings);
            state = result.State;
            recorder.Handle(sample, result);
        }
        recorder.Flush();
    }

    private long QueryLong(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False;Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void New_database_gets_current_schema_and_wal_mode()
    {
        using (new SqliteMonitorStore(DbPath))
        {
        }
        Assert.Equal(1, QueryLong("PRAGMA user_version"));
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Newer_schema_is_refused()
    {
        using (new SqliteMonitorStore(DbPath))
        {
        }
        using (var c = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new SqliteMonitorStore(DbPath));
    }

    [Fact]
    public void Healthy_run_stores_only_aggregates()
    {
        using var store = new SqliteMonitorStore(DbPath);
        Record(store, Scenarios.S01HealthyDay with { Duration = TimeSpan.FromHours(2) });

        var report = store.Report();
        Assert.Equal(0, report.IncidentCount);
        Assert.Equal(0, report.SampleRows);
        Assert.Equal(120, report.AggregateRows);
        Assert.Equal(0, QueryLong("SELECT COUNT(*) FROM session"));
        Assert.Equal(0, QueryLong("SELECT COUNT(*) FROM sql_text"));

        var rows = store.ReadAggregates(Scenarios.S01HealthyDay.Start, Scenarios.S01HealthyDay.Start.AddHours(2));
        Assert.Equal(120, rows.Count);
        Assert.All(rows, r => Assert.Equal(12, r.Samples));
        Assert.All(rows, r => Assert.Equal(3, r.MaxSessionsByDatabase.Count));
        Assert.All(rows, r => Assert.Equal(Visibility.Full, r.WorstVisibility));
    }

    [Fact]
    public void Incident_keeps_pre_incident_buffer_and_detail_until_close()
    {
        var s = Scenarios.S05LockChain;
        using var store = new SqliteMonitorStore(DbPath);
        Record(store, s);

        var incident = Assert.Single(store.ListIncidents());
        Assert.Equal(IncidentStatus.Closed, incident.Status);
        Assert.Equal(Severity.High, incident.Severity);
        Assert.Equal(["app_orchid_db"], incident.Databases);

        var detail = store.GetIncident(incident.Id)!;
        var pre = detail.Samples.Where(x => x.Phase == SamplePhase.Pre).ToList();
        Assert.InRange(pre.Count, 30, 90);
        Assert.True(pre.Max(x => x.At) < incident.OpenedAt);
        Assert.Contains(detail.Samples, x => x.Phase == SamplePhase.Active);
        Assert.Contains(detail.Samples, x => x.Phase == SamplePhase.Recovering);
        Assert.Equal(detail.Samples.Count, detail.Samples.Select(x => x.At).Distinct().Count());

        Assert.Equal(IncidentEventKind.Opened, detail.Events[0].Kind);
        Assert.Equal(IncidentEventKind.Closed, detail.Events[^1].Kind);

        // The evidence the DBA's cleanup would have destroyed: the root blocker with its SQL, and the chain.
        var atPeak = detail.Samples.Single(x => x.At == s.Start + TimeSpan.FromMinutes(10));
        var root = atPeak.Sessions.Single(r => r.State == "idle in transaction" && r.BackendXid == "731002");
        Assert.StartsWith("UPDATE \"orchid$customer\"", root.Query, StringComparison.Ordinal);
        Assert.Equal(8, atPeak.Sessions.Count(r => r.IsWaitingOnLock && r.Database == "app_orchid_db" && r.BlockingPids.Count > 0
            && (r.BlockingPids[0] == root.Pid || atPeak.Sessions.Any(b => b.Pid == r.BlockingPids[0] && b.BlockingPids.Contains(root.Pid)))));

        // Plain idle sessions are not stored.
        Assert.DoesNotContain(detail.Samples.SelectMany(x => x.Sessions), r => r.State == "idle");
    }

    [Fact]
    public void Sql_text_is_stored_once_per_distinct_statement()
    {
        using var store = new SqliteMonitorStore(DbPath);
        Record(store, Scenarios.S05LockChain);
        Assert.True(QueryLong("SELECT COUNT(*) FROM session WHERE sql_hash IS NOT NULL") > 100);
        Assert.InRange(QueryLong("SELECT COUNT(*) FROM sql_text"), 1, 6);
    }

    [Fact]
    public void Restart_mid_incident_keeps_evidence_and_marks_it_interrupted()
    {
        var s = Scenarios.S05LockChain;
        using (var store = new SqliteMonitorStore(DbPath))
        {
            Record(store, s, stopAfter: TimeSpan.FromMinutes(10));
            Assert.Equal(IncidentStatus.Active, Assert.Single(store.ListIncidents()).Status);
        }

        using (var store = new SqliteMonitorStore(DbPath))
        {
            Assert.Equal(1, store.MarkOpenIncidentsInterrupted(s.Start + TimeSpan.FromMinutes(11)));
            var incident = Assert.Single(store.ListIncidents());
            Assert.Equal(IncidentStatus.Interrupted, incident.Status);
            Assert.True(incident.SampleCount > 50);
            Assert.NotEmpty(store.GetIncident(incident.Id)!.Samples.SelectMany(x => x.Sessions));
            Assert.Equal(1, store.MaxIncidentId());

            // The next incident gets a new id, not 1 again.
            Record(store, s with { Start = s.Start.AddHours(1) });
            Assert.Equal(2, store.MaxIncidentId());
        }
    }

    [Fact]
    public void Gap_events_outside_an_incident_are_kept_without_incident()
    {
        using var store = new SqliteMonitorStore(DbPath);
        store.AppendEvents([new IncidentEvent(IncidentEventKind.GapStarted, 0, T0, Severity.None, "Sampling failed: Timeout")]);
        Assert.Equal(1, QueryLong("SELECT COUNT(*) FROM event WHERE incident_id IS NULL"));
    }

    // ------------------------------------------------------------------ retention and size

    private static IncidentInfo Incident(long id, DateTimeOffset opened, DateTimeOffset? closed) => new()
    {
        Id = id,
        OpenedAt = opened,
        Severity = Severity.Warning,
        ClosedAt = closed,
        Signals = [SignalKind.Lock],
        Databases = System.Collections.Immutable.ImmutableSortedSet.Create(StringComparer.Ordinal, "app_orchid_db"),
    };

    private static void AddSamples(IMonitorStore store, long incidentId, DateTimeOffset from, int count)
    {
        var samples = Enumerable.Range(0, count)
            .Select(i => Blocked(from.AddSeconds(i * 2), TimeSpan.FromSeconds(45), waiters: 10))
            .Select((x, i) => x with
            {
                Sessions = x.Sessions.Select(r => r with { Query = r.Query + " /* " + new string('x', 400) + i + " */" }).ToList(),
            })
            .ToList();
        store.WriteSamples(incidentId, SamplePhase.Active, samples);
    }

    [Fact]
    public void Retention_removes_expired_data_but_never_open_incidents()
    {
        var now = T0.AddDays(400);
        using var store = new SqliteMonitorStore(DbPath);
        foreach (var t in new[] { now.AddDays(-40), now.AddDays(-31), now.AddDays(-29), now.AddHours(-1) })
        {
            store.WriteAggregate(new()
            {
                WindowStart = t, Samples = 12, FailedSamples = 0, MaxSessions = 1, MaxActive = 0, MaxIdleInTransaction = 0,
                MaxLockWaiters = 0, MaxLockWaitersOverThreshold = 0, MaxLockWaitSeconds = 0, MaxTransactionAgeSeconds = 0,
                WorstVisibility = Visibility.Full, MaxSessionsByDatabase = new Dictionary<string, int>(),
            });
        }
        store.UpsertIncident(Incident(1, now.AddDays(-200), now.AddDays(-199)), IncidentStatus.Closed);
        store.UpsertIncident(Incident(2, now.AddDays(-250), null), IncidentStatus.Active);
        store.UpsertIncident(Incident(3, now.AddDays(-10), now.AddDays(-10)), IncidentStatus.Closed);
        AddSamples(store, 1, now.AddDays(-200), 5);
        AddSamples(store, 2, now.AddDays(-250), 5);

        var result = store.RunMaintenance(now, new StorageSettings { DeleteBatchSize = 1 });

        Assert.Equal(2, result.AggregatesDeleted);
        Assert.Equal(1, result.IncidentsDeleted);
        Assert.Equal([3, 2], store.ListIncidents().Select(i => i.Id));
        Assert.Equal(0, QueryLong("SELECT COUNT(*) FROM sample WHERE incident_id = 1"));
        Assert.Equal(0, QueryLong("SELECT COUNT(*) FROM sql_text WHERE hash NOT IN (SELECT sql_hash FROM session WHERE sql_hash IS NOT NULL)"));
    }

    [Fact]
    public void Size_cap_deletes_the_oldest_finished_incident_first()
    {
        var now = T0;
        using var store = new SqliteMonitorStore(DbPath);
        store.UpsertIncident(Incident(1, now.AddDays(-3), now.AddDays(-3)), IncidentStatus.Closed);
        store.UpsertIncident(Incident(2, now.AddDays(-2), now.AddDays(-2)), IncidentStatus.Interrupted);
        store.UpsertIncident(Incident(3, now.AddDays(-1), null), IncidentStatus.Active);
        AddSamples(store, 1, now.AddDays(-3), 300);
        AddSamples(store, 2, now.AddDays(-2), 5);
        AddSamples(store, 3, now.AddDays(-1), 5);
        store.RunMaintenance(now, new StorageSettings()); // settle WAL so sizes are comparable
        var before = store.Report().TotalBytes;

        var result = store.RunMaintenance(now, new StorageSettings { MaxBytes = before - 1 });

        Assert.Equal([3, 2], store.ListIncidents().Select(i => i.Id));
        Assert.False(result.OverCapWithOpenIncidents);
        Assert.True(result.Report.TotalBytes < before);
    }

    [Fact]
    public void Over_cap_with_only_open_incidents_thins_but_keeps_them()
    {
        using var store = new SqliteMonitorStore(DbPath);
        store.UpsertIncident(Incident(1, T0, null), IncidentStatus.Active);
        AddSamples(store, 1, T0, 50);
        store.WriteSamples(1, SamplePhase.Pre, [Blocked(T0.AddMinutes(-1), TimeSpan.FromSeconds(5))]);

        var result = store.RunMaintenance(T0, new StorageSettings { MaxBytes = 1 });

        Assert.True(result.OverCapWithOpenIncidents);
        Assert.InRange(result.SamplesThinned, 35, 45);
        var detail = store.GetIncident(1)!;
        Assert.Equal(IncidentStatus.Active, detail.Incident.Status);
        Assert.Single(detail.Samples, x => x.Phase == SamplePhase.Pre);
    }
}
