using Microsoft.Data.Sqlite;
using MimeKit;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;
using PgMonitor.Infrastructure.Notifications;
using PgMonitor.Infrastructure.Security;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FailingSender(string message) : IEmailSender
{
    public int Calls { get; private set; }

    public Task SendAsync(MimeMessage message1, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException(message);
    }
}

internal sealed class CollectingSender : IEmailSender
{
    public List<MimeMessage> Sent { get; } = [];

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class NotificationTests : IDisposable
{
    internal const string Canary = "CANARY-pw-123";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgmonitor-tests", Guid.NewGuid().ToString("N"));

    internal static readonly EmailSettings Email = new()
    {
        Enabled = true,
        SmtpHost = "smtp.example.test",
        SmtpUser = "svc_mail",
        From = "pgmonitor@example.test",
        To = ["dba@example.test", "team@example.test"],
        SqlMaxChars = 40,
    };

    internal static readonly Dictionary<string, string> Apps = new(StringComparer.Ordinal) { ["app_orchid_db"] = "Orchid" };

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

    private SqliteMonitorStore NewStore() => new(Path.Combine(_dir, "t.db"));

    /// <summary>Runs a scenario with a notifier; returns all queued mails in order.</summary>
    private static List<OutboxMail> Notify(IMonitorStore store, Scenario scenario, EmailSettings? email = null)
    {
        email ??= Email;
        var notifier = new MailNotifier(store, email, new MailComposer(email, Apps, TimeZoneInfo.Utc), simulated: false);
        var recorder = new EvidenceRecorder(store);
        var state = EngineState.Initial;
        var sim = new Simulator(scenario);
        while (sim.HasNext)
        {
            var sample = sim.Next();
            var result = IncidentEngine.Process(state, sample, new IncidentSettings());
            state = result.State;
            recorder.Handle(sample, result);
            notifier.OnResult(sample, result);
        }
        return store.DueMails(DateTimeOffset.MaxValue.AddDays(-1), 100).ToList();
    }

    private static string Text(OutboxMail m)
    {
        using var ms = new MemoryStream(m.Mime);
        var body = (TextPart)MimeMessage.Load(ms, TestContext.Current.CancellationToken).Body!;
        return body.Text ?? "";
    }

    // ------------------------------------------------------------------ composition

    [Fact]
    public void S05_mails_open_escalate_and_close_with_root_blocker_and_short_sql()
    {
        using var store = NewStore();
        var mails = Notify(store, Scenarios.S05LockChain);

        Assert.Equal(["Opened", "Escalated", "Closed"], mails.Select(m => m.Kind));
        // At opening all 8 waiters have joined, but only the first ones are over the 30 s threshold: Warning.
        Assert.Equal("[PgMonitor] Olay #1 AÇILDI – Uyarı – app_orchid_db (Orchid) – 8 lock bekleyen", mails[0].Subject);
        Assert.StartsWith("[PgMonitor] Olay #1 KAPANDI – Yüksek – app_orchid_db (Orchid)", mails[2].Subject, StringComparison.Ordinal);

        var opened = Text(mails[0]);
        Assert.Contains("KÖK BLOKLAYICILAR (şüpheli)", opened, StringComparison.Ordinal);
        Assert.Contains("idle in transaction", opened, StringComparison.Ordinal);
        var sqlLine = opened.Split('\n').First(l => l.Contains("SQL: UPDATE", StringComparison.Ordinal)).Trim();
        Assert.Equal("SQL: " + "UPDATE \"orchid$customer\" SET \"changeddate\" = $1"[..40] + " …(kısaltıldı)", sqlLine);
        Assert.Contains("kesin değildir", opened, StringComparison.Ordinal);

        var closed = Text(mails[2]);
        Assert.Contains("EN KÖTÜ AN", closed, StringComparison.Ordinal);
        Assert.Contains("8 session bekliyor (zincir derinliği 2)", closed, StringComparison.Ordinal);
        Assert.Contains("Bitiş", closed, StringComparison.Ordinal);

        using var ms = new MemoryStream(mails[0].Mime);
        var msg = MimeMessage.Load(ms, TestContext.Current.CancellationToken);
        Assert.Equal(["dba@example.test", "team@example.test"], msg.To.Mailboxes.Select(m => m.Address));
    }

    [Fact]
    public void Sql_can_be_left_out_of_mails()
    {
        using var store = NewStore();
        var mails = Notify(store, Scenarios.S05LockChain, Email with { SqlMaxChars = 0 });
        Assert.All(mails, m => Assert.DoesNotContain("SQL:", Text(m), StringComparison.Ordinal));
    }

    [Fact]
    public void Simulated_mails_are_labelled()
    {
        var composer = new MailComposer(Email, Apps, TimeZoneInfo.Utc);
        var incident = new IncidentInfo { Id = 3, OpenedAt = TestData.T0, Severity = Severity.Warning };
        var ctx = new MailContext(new IncidentEvent(IncidentEventKind.Opened, 3, TestData.T0, Severity.Warning, "x"), incident, null, null, null, Visibility.Full, Simulated: true);
        Assert.StartsWith("[SİMÜLASYON] ", composer.Subject(ctx), StringComparison.Ordinal);
        Assert.Contains("SİMÜLASYON VERİSİ", composer.Body(ctx), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ policy

    [Fact]
    public void Flapping_sends_one_open_and_one_close()
    {
        using var store = NewStore();
        var mails = Notify(store, Scenarios.S04Flapping);
        Assert.Equal(["Opened", "Closed"], mails.Select(m => m.Kind));
    }

    [Fact]
    public void Healthy_day_sends_nothing_and_disabled_mail_queues_nothing()
    {
        using var store = NewStore();
        Assert.Empty(Notify(store, Scenarios.S01HealthyDay with { Duration = TimeSpan.FromHours(3) }));
        using var store2 = new SqliteMonitorStore(Path.Combine(_dir, "t2.db"));
        Assert.Empty(Notify(store2, Scenarios.S05LockChain, Email with { Enabled = false }));
    }

    [Fact]
    public void Same_event_is_queued_once()
    {
        using var store = NewStore();
        var notifier = new MailNotifier(store, Email, new MailComposer(Email), simulated: false);
        var incident = new IncidentInfo { Id = 1, OpenedAt = TestData.T0, Severity = Severity.Warning };
        store.UpsertIncident(incident, IncidentStatus.Active);
        var result = new EngineResult(EngineState.Initial with { Incident = incident, Phase = IncidentPhase.Active },
            [new IncidentEvent(IncidentEventKind.Opened, 1, TestData.T0, Severity.Warning, "x")], null);

        Assert.Equal(1, notifier.OnResult(TestData.Healthy(TestData.T0), result));
        Assert.Equal(0, notifier.OnResult(TestData.Healthy(TestData.T0), result));
        Assert.Single(store.DueMails(TestData.T0.AddDays(1)));
    }

    [Fact]
    public void Escalations_respect_the_cooldown()
    {
        using var store = NewStore();
        var notifier = new MailNotifier(store, Email, new MailComposer(Email), simulated: false);
        var incident = new IncidentInfo { Id = 1, OpenedAt = TestData.T0, Severity = Severity.High };
        store.UpsertIncident(incident, IncidentStatus.Active);
        EngineResult Esc(DateTimeOffset at) => new(EngineState.Initial with { Incident = incident, Phase = IncidentPhase.Active },
            [new IncidentEvent(IncidentEventKind.SeverityEscalated, 1, at, Severity.High, "x")], null);

        Assert.Equal(1, notifier.OnResult(TestData.Healthy(TestData.T0), Esc(TestData.T0)));
        Assert.Equal(0, notifier.OnResult(TestData.Healthy(TestData.T0), Esc(TestData.T0.AddMinutes(5))));
        Assert.Equal(1, notifier.OnResult(TestData.Healthy(TestData.T0), Esc(TestData.T0.AddMinutes(11))));
    }

    // ------------------------------------------------------------------ outbox

    [Fact]
    public async Task Failed_send_retries_with_backoff_then_gives_up()
    {
        using var store = NewStore();
        Notify(store, Scenarios.S04Flapping);
        var time = new ManualTime(Scenarios.S04Flapping.Start.AddHours(1));
        var sender = new FailingSender("smtp.example.test refused " + Canary);
        var worker = new OutboxWorker(store, sender, Email with { GiveUpAfter = TimeSpan.FromHours(2) }, time);

        Assert.Equal(0, await worker.ProcessAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, sender.Calls);
        Assert.Equal("InvalidOperationException", worker.LastError);
        Assert.Equal(new OutboxCounts(2, 0, 0), store.MailCounts());

        Assert.Equal(0, await worker.ProcessAsync(TestContext.Current.CancellationToken)); // not due yet (1 min backoff)
        Assert.Equal(2, sender.Calls);

        for (var i = 0; i < 20; i++)
        {
            time.Now += TimeSpan.FromHours(1);
            await worker.ProcessAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(new OutboxCounts(0, 0, 2), store.MailCounts());
    }

    [Fact]
    public async Task Due_mails_are_sent_once()
    {
        using var store = NewStore();
        Notify(store, Scenarios.S05LockChain);
        var sender = new CollectingSender();
        var worker = new OutboxWorker(store, sender, Email, new ManualTime(DateTimeOffset.MaxValue.AddDays(-2)));

        Assert.Equal(3, await worker.ProcessAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await worker.ProcessAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new OutboxCounts(0, 3, 0), store.MailCounts());
        Assert.StartsWith("[PgMonitor] Olay #1 AÇILDI", sender.Sent[0].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_drop_writes_eml_files_without_network()
    {
        var folder = Path.Combine(_dir, "drop");
        var sender = new FileDropEmailSender(folder);
        var msg = new MailComposer(Email).Compose(new MailContext(
            new IncidentEvent(IncidentEventKind.Opened, 1, TestData.T0, Severity.Warning, "x"),
            new IncidentInfo { Id = 1, OpenedAt = TestData.T0, Severity = Severity.Warning }, null, null, null, Visibility.Full, true));
        await sender.SendAsync(msg, TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(folder, "*.eml"));
        Assert.Contains("SİMÜLASYON", MimeMessage.Load(file, TestContext.Current.CancellationToken).Subject, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ credentials

    [Fact]
    public void Layered_store_prefers_session_secret_and_saves_to_the_saved_store()
    {
        var session = new InMemoryCredentialStore();
        var saved = new InMemoryCredentialStore();
        var store = new LayeredCredentialStore(session, saved);
        var target = CredentialTargets.Postgres("svc_monitor", "db.example.test", 5432);
        Assert.Equal("PgMonitor:postgres:svc_monitor@db.example.test:5432", target);

        store.Write(target, "svc_monitor", "saved-secret");
        session.Write(target, "svc_monitor", "session-secret");
        Assert.Equal("session-secret", store.Read(target));
        Assert.True(store.Delete(target));
        Assert.Null(store.Read(target));
    }

    [Fact]
    public void Windows_credential_manager_round_trip_is_opt_in()
    {
        if (Environment.GetEnvironmentVariable("PGMONITOR_WINCRED_TEST") != "1" || !OperatingSystem.IsWindows())
        {
            Assert.Skip("Set PGMONITOR_WINCRED_TEST=1 on Windows to run (writes and deletes one test credential).");
            return;
        }
        WindowsRoundTrip();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsRoundTrip()
    {
        var store = new WindowsCredentialStore();
        var target = "PgMonitor:test:" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(store.Read(target));
            store.Write(target, "svc_test", Canary + "-ığüşöç");
            Assert.Equal(Canary + "-ığüşöç", store.Read(target));
        }
        finally
        {
            Assert.True(store.Delete(target));
        }
        Assert.Null(store.Read(target));
        Assert.False(store.Delete(target));
    }
}
