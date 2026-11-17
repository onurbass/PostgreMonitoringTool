using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PgMonitor.Core.Aggregation;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;
using PgMonitor.Infrastructure.Notifications;
using PgMonitor.Infrastructure.Postgres;
using PgMonitor.Infrastructure.Runtime;
using PgMonitor.Infrastructure.Security;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.Tests;

/// <summary>Logger that keeps every formatted line, so tests can check what would reach the log file.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>Store whose writes fail until <see cref="Broken"/> is cleared (disk full, file locked...).</summary>
internal sealed class FlakyStore(IMonitorStore inner) : IMonitorStore
{
    public bool Broken { get; set; } = true;

    private void Check()
    {
        if (Broken)
        {
            throw new IOException("disk full");
        }
    }

    public void RecordLockWaits(IReadOnlyList<PgMonitor.Core.Locks.LockWaitEvent> events) { Check(); inner.RecordLockWaits(events); }
    public IReadOnlyList<StoredLockWait> ListLockWaits(int limit = 1000) => inner.ListLockWaits(limit);
    public IReadOnlyList<StoredEvent> ListRecentEvents(int limit = 50) => inner.ListRecentEvents(limit);
    public void RecordLongTransactions(IReadOnlyList<PgMonitor.Core.Transactions.LongTxEvent> events) { Check(); inner.RecordLongTransactions(events); }
    public IReadOnlyList<StoredLongTransaction> ListLongTransactions(int limit = 500) => inner.ListLongTransactions(limit);
    public bool EnqueueMail(OutboxMail mail) => inner.EnqueueMail(mail);
    public IReadOnlyList<OutboxMail> DueMails(DateTimeOffset now, int limit = 10) => inner.DueMails(now, limit);
    public void MarkMailSent(long id, DateTimeOffset now) => inner.MarkMailSent(id, now);
    public void MarkMailFailed(long id, DateTimeOffset? nextAttempt, string safeError) => inner.MarkMailFailed(id, nextAttempt, safeError);
    public DateTimeOffset? LastMailAt(long incidentId, string kind) => inner.LastMailAt(incidentId, kind);
    public OutboxCounts MailCounts() => inner.MailCounts();
    public long MaxIncidentId() => inner.MaxIncidentId();
    public int MarkOpenIncidentsInterrupted(DateTimeOffset now) => inner.MarkOpenIncidentsInterrupted(now);
    public void WriteAggregate(AggregateRow row) { Check(); inner.WriteAggregate(row); }
    public void UpsertIncident(IncidentInfo incident, IncidentStatus status) { Check(); inner.UpsertIncident(incident, status); }
    public void AppendEvents(IReadOnlyList<IncidentEvent> events) { Check(); inner.AppendEvents(events); }
    public void WriteSamples(long incidentId, SamplePhase phase, IReadOnlyList<ClusterSample> samples) { Check(); inner.WriteSamples(incidentId, phase, samples); }
    public IReadOnlyList<StoredIncident> ListIncidents(int limit = 200) => inner.ListIncidents(limit);
    public StoredIncidentDetail? GetIncident(long id) => inner.GetIncident(id);
    public IReadOnlyList<AggregateRow> ReadAggregates(DateTimeOffset from, DateTimeOffset to) => inner.ReadAggregates(from, to);
    public MaintenanceResult RunMaintenance(DateTimeOffset now, StorageSettings settings) => inner.RunMaintenance(now, settings);
    public StorageReport Report() => inner.Report();
    public void Dispose() => inner.Dispose();
}

/// <summary>End-to-end pipeline on simulated data (TEST_PLAN 5) plus loop resilience and the canary check.</summary>
public sealed class RuntimeTests : IDisposable
{
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

    private static async Task RunToEnd(MonitorLoop loop, SimulatedSampleSource source)
    {
        while (!source.IsExhausted)
        {
            await loop.TickAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Demo_pipeline_records_the_incident_and_drops_three_mails()
    {
        var paths = new AppPaths(_dir);
        var scenario = Scenarios.S05LockChain;
        var time = new ManualTime(scenario.Start);
        using var store = new SqliteMonitorStore(paths.Database(DataMode.Demo));
        var source = new SimulatedSampleSource(scenario);
        var email = NotificationTests.Email;
        var notifier = new MailNotifier(store, email, new MailComposer(email, NotificationTests.Apps), simulated: true);
        var outbox = new OutboxWorker(store, new FileDropEmailSender(paths.MailDrop), email, time);
        var loop = new MonitorLoop(source, store, new MonitorOptions(), notifier, outbox, time, new ListLogger<MonitorLoop>());

        var snapshots = 0;
        loop.Updated += (_, _) => snapshots++;
        await RunToEnd(loop, source);
        time.Now = DateTimeOffset.MaxValue.AddDays(-2);
        await outbox.ProcessAsync(TestContext.Current.CancellationToken);

        Assert.Equal(300, snapshots);
        var incident = Assert.Single(store.ListIncidents());
        Assert.Equal(IncidentStatus.Closed, incident.Status);
        Assert.True(incident.SampleCount > 200);
        var files = Directory.GetFiles(paths.MailDrop, "*.eml");
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.StartsWith("[SİMÜLASYON] [PgMonitor] Olay #1", MimeKit.MimeMessage.Load(f, TestContext.Current.CancellationToken).Subject, StringComparison.Ordinal));
        Assert.Equal(SourceKind.Simulated, loop.Latest.Source);
        Assert.Equal(IncidentPhase.Healthy, loop.Latest.State.Phase);
    }

    [Fact]
    public async Task Storage_failure_does_not_stop_sampling_and_recovers()
    {
        using var inner = new SqliteMonitorStore(Path.Combine(_dir, "flaky.db"));
        var store = new FlakyStore(inner);
        var source = new SimulatedSampleSource(Scenarios.S05LockChain);
        var logger = new ListLogger<MonitorLoop>();
        var loop = new MonitorLoop(source, store, new MonitorOptions(), null, null, new ManualTime(Scenarios.S05LockChain.Start), logger);

        for (var i = 0; i < 100; i++)
        {
            await loop.TickAsync(TestContext.Current.CancellationToken);
        }
        Assert.True(loop.Latest.StorageDegraded);
        Assert.Equal("IOException", loop.Latest.StorageError);
        Assert.Contains(logger.Lines, l => l.Contains("Storage failure: IOException", StringComparison.Ordinal));

        store.Broken = false;
        await RunToEnd(loop, source);
        Assert.False(loop.Latest.StorageDegraded);
        Assert.Equal(IncidentPhase.Healthy, loop.Latest.State.Phase);
        Assert.NotEmpty(inner.ListIncidents()); // the incident itself was recorded once storage came back
    }

    [Fact]
    public async Task Delay_is_shorter_during_an_incident_and_backs_off_while_failing()
    {
        using var store = new SqliteMonitorStore(Path.Combine(_dir, "d.db"));
        var options = new MonitorOptions();
        var outage = Scenarios.S07DisconnectAndSleep;
        var source = new SimulatedSampleSource(outage);
        var loop = new MonitorLoop(source, store, options, null, null, new ManualTime(outage.Start), new ListLogger<MonitorLoop>());

        Assert.Equal(options.NormalInterval, loop.NextDelay());
        var delays = new List<TimeSpan>();
        while (!source.IsExhausted)
        {
            await loop.TickAsync(TestContext.Current.CancellationToken);
            delays.Add(loop.NextDelay());
        }
        Assert.Contains(options.IncidentInterval, delays);
        Assert.Contains(TimeSpan.FromSeconds(10), delays); // second failure in a row
        Assert.Contains(options.MaxBackoff, delays);
        Assert.True(delays.Max() <= options.MaxBackoff);
    }

    [Fact]
    public async Task Run_stops_cleanly_on_cancellation()
    {
        using var store = new SqliteMonitorStore(Path.Combine(_dir, "r.db"));
        var source = new SimulatedSampleSource(Scenarios.S01HealthyDay, loop: true);
        var loop = new MonitorLoop(source, store, new MonitorOptions { NormalInterval = TimeSpan.FromMilliseconds(5), MailCheckEvery = TimeSpan.FromMilliseconds(5) },
            null, null, TimeProvider.System, new ListLogger<MonitorLoop>());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        await loop.RunAsync(cts.Token);

        Assert.NotNull(loop.Latest.Sample);
        Assert.True(store.Report().AggregateRows >= 1); // the open window was flushed on shutdown
    }

    [Fact]
    public async Task Canary_password_never_reaches_disk_mail_or_log()
    {
        const string canary = NotificationTests.Canary;
        var paths = new AppPaths(_dir);
        var scenario = Scenarios.S05LockChain;
        var credentials = new InMemoryCredentialStore();
        credentials.Write(CredentialTargets.Smtp("svc_mail", "smtp.example.test"), "svc_mail", canary);
        credentials.Write(CredentialTargets.Postgres("svc_monitor", "127.0.0.1", 1), "svc_monitor", canary);

        var settings = new AppSettings
        {
            Mode = DataMode.Live,
            Postgres = new PostgresConnectionSettings { Host = "127.0.0.1", Port = 1, Username = "svc_monitor" },
            Email = NotificationTests.Email,
        };
        SettingsFile.Save(paths.Settings, settings);

        var logger = new ListLogger<MonitorLoop>();
        var time = new ManualTime(scenario.Start);
        using (var store = new SqliteMonitorStore(paths.Database(DataMode.Demo)))
        {
            var email = NotificationTests.Email;
            var notifier = new MailNotifier(store, email, new MailComposer(email), simulated: true);
            var outbox = new OutboxWorker(store, new FailingSender("auth failed for " + canary), email, time);
            var source = new SimulatedSampleSource(scenario);
            var loop = new MonitorLoop(source, store, new MonitorOptions(), notifier, outbox, time, logger);
            await RunToEnd(loop, source);
            await outbox.ProcessAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(canary, outbox.LastError ?? "", StringComparison.Ordinal);

            // A real (failing) live connection with the canary as password.
            await using var live = new PostgresSampleSource(settings.Postgres!, _ => ValueTask.FromResult(credentials.Read(CredentialTargets.Postgres("svc_monitor", "127.0.0.1", 1))));
            var liveLoop = new MonitorLoop(live, store, new MonitorOptions(), null, null, time, logger);
            await liveLoop.TickAsync(TestContext.Current.CancellationToken);
            Assert.True(liveLoop.Latest.ConsecutiveFailures > 0);
            Assert.DoesNotContain(canary, liveLoop.Latest.CollectError ?? "", StringComparison.Ordinal);
        }
        SqliteConnection.ClearAllPools();

        var bytes = Encoding.UTF8.GetBytes(canary);
        var utf16 = Encoding.Unicode.GetBytes(canary);
        foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var content = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            Assert.False(content.AsSpan().IndexOf(bytes) >= 0 || content.AsSpan().IndexOf(utf16) >= 0, $"canary found in {Path.GetFileName(file)}");
        }
        Assert.DoesNotContain(logger.Lines, l => l.Contains(canary, StringComparison.Ordinal));
        Assert.DoesNotContain("password", File.ReadAllText(paths.Settings), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Settings_round_trip_and_validation()
    {
        var path = Path.Combine(_dir, "settings.json");
        Assert.Equal(DataMode.Demo, SettingsFile.Load(path).Mode); // missing file → safe defaults (demo)

        var s = new AppSettings
        {
            Mode = DataMode.Live,
            Postgres = new PostgresConnectionSettings { Host = "db.example.test", Username = "svc_monitor" },
            DatabaseApps = new(StringComparer.Ordinal) { ["app_orchid_db"] = "Orchid" },
            Incident = new IncidentSettings { LockWaitThreshold = TimeSpan.FromSeconds(45) },
        };
        SettingsFile.Save(path, s);
        var loaded = SettingsFile.Load(path);
        Assert.Equal(DataMode.Live, loaded.Mode);
        Assert.Equal("db.example.test", loaded.Postgres!.Host);
        Assert.Equal(TimeSpan.FromSeconds(45), loaded.Incident.LockWaitThreshold);
        Assert.Equal("Orchid", loaded.DatabaseApps["app_orchid_db"]);
        Assert.Contains("\"Live\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(loaded.Validate());

        var bad = s with
        {
            Postgres = s.Postgres with { Port = 0 },
            Email = new EmailSettings { Enabled = true, From = "not-an-address", To = ["ok@example.test", "bad"] },
        };
        var errors = bad.Validate();
        Assert.Contains(errors, e => e.Contains("port", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("SMTP", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Gönderen", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("bad", StringComparison.Ordinal));
    }

    [Fact]
    public void Sampling_defaults_are_conservative_and_limits_are_enforced()
    {
        var s = new AppSettings();
        Assert.Equal(10, s.Sampling.NormalIntervalSeconds);
        Assert.Equal(5, s.Sampling.IncidentIntervalSeconds);
        Assert.Empty(s.Validate());

        Assert.NotEmpty((s with { Sampling = new SamplingSettings { NormalIntervalSeconds = 4 } }).Validate());
        Assert.NotEmpty((s with { Sampling = new SamplingSettings { NormalIntervalSeconds = 10, IncidentIntervalSeconds = 1 } }).Validate());
        Assert.NotEmpty((s with { Sampling = new SamplingSettings { NormalIntervalSeconds = 10, IncidentIntervalSeconds = 20 } }).Validate());
        Assert.Empty((s with { Sampling = new SamplingSettings { NormalIntervalSeconds = 5, IncidentIntervalSeconds = 2 } }).Validate());
    }

    [Theory]
    [InlineData(10, 5, 30, 36)]
    [InlineData(30, 10, 90, 18)]
    [InlineData(5, 2, 30, 90)]
    public void Sampling_options_follow_the_interval(int normal, int incident, int expectedGapSeconds, int expectedPreSamples)
    {
        var app = new AppSettings { Sampling = new SamplingSettings { NormalIntervalSeconds = normal, IncidentIntervalSeconds = incident } };
        var o = app.Sampling.ToOptions(app);
        Assert.Equal(TimeSpan.FromSeconds(normal), o.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(incident), o.IncidentInterval);
        Assert.Equal(TimeSpan.FromSeconds(expectedGapSeconds), o.Incident.MaxSampleGap); // no false "sleep" gaps
        Assert.Equal(expectedPreSamples, o.Recorder.PreIncidentSamples);                 // ~3 minutes before the incident
        Assert.True(o.MaxBackoff >= o.NormalInterval);
    }

    [Fact]
    public async Task Ten_second_sampling_still_detects_and_closes_an_incident_without_false_gaps()
    {
        var app = new AppSettings();
        var options = app.Sampling.ToOptions(app);
        var scenario = Scenarios.S05LockChain with { Step = TimeSpan.FromSeconds(10) };
        using var store = new SqliteMonitorStore(Path.Combine(_dir, "ten.db"));
        var source = new SimulatedSampleSource(scenario);
        var loop = new MonitorLoop(source, store, options, null, null, new ManualTime(scenario.Start), new ListLogger<MonitorLoop>());
        var events = new List<IncidentEvent>();
        loop.Updated += (_, s) => events.AddRange(s.RecentEvents.Where(e => e.At == s.Sample!.SampledAt));
        await RunToEnd(loop, source);

        Assert.Single(events, e => e.Kind == IncidentEventKind.Opened);
        Assert.Single(events, e => e.Kind == IncidentEventKind.Closed);
        Assert.DoesNotContain(events, e => e.Kind == IncidentEventKind.GapStarted);
        var detail = store.GetIncident(1)!;
        Assert.Contains(detail.Samples, x => x.Phase == SamplePhase.Pre);
    }

    [Fact]
    public void Demo_and_live_use_separate_database_files()
    {
        var p = new AppPaths(_dir);
        Assert.NotEqual(p.Database(DataMode.Demo), p.Database(DataMode.Live));
    }

    [Fact]
    public async Task Looping_demo_source_keeps_time_moving_forward()
    {
        var short1 = Scenarios.S02SingleBlip with { Duration = TimeSpan.FromMinutes(1) };
        var source = new SimulatedSampleSource(short1, loop: true);
        DateTimeOffset last = default;
        for (var i = 0; i < 30; i++)
        {
            var s = await source.CollectAsync(TestContext.Current.CancellationToken);
            Assert.True(s.SampledAt > last);
            last = s.SampledAt;
        }
        Assert.False(source.IsExhausted);
    }
}
