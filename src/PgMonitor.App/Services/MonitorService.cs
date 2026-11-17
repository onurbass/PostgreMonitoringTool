using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PgMonitor.Core.Simulation;
using PgMonitor.Infrastructure.Notifications;
using PgMonitor.Infrastructure.Postgres;
using PgMonitor.Infrastructure.Runtime;
using PgMonitor.Infrastructure.Security;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.App.Services;

public sealed record ConnectionTestResult(bool Ok, string Summary);

/// <summary>
/// Owns the running pipeline for the current settings: builds the source (demo or live), store, notifier and loop,
/// and restarts them when settings change. Demo mode opens no network connection and sends no real mail.
/// </summary>
public sealed class MonitorService(AppPaths paths, LayeredCredentialStore credentials, ILoggerFactory loggers, TimeSpan? demoInterval = null) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _run;
    private IAsyncDisposable? _source;

    public AppPaths Paths { get; } = paths;
    public LayeredCredentialStore Credentials { get; } = credentials;
    public AppSettings Settings { get; private set; } = new();
    public SqliteMonitorStore? Store { get; private set; }
    public MonitorLoop? Loop { get; private set; }

    /// <summary>Raised on a background thread after every monitoring cycle.</summary>
    public event EventHandler<MonitorSnapshot>? SnapshotReceived;

    public async Task StartAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await StopAsync().ConfigureAwait(false);
        Settings = settings;

        var store = new SqliteMonitorStore(Paths.Database(settings.Mode));
        var time = TimeProvider.System;
        var demo = settings.Mode == DataMode.Demo;

        SourceHolder holder = demo ? DemoSource(settings) : LiveSource(settings);

        var email = demo
            ? settings.Email with
            {
                Enabled = true,
                From = string.IsNullOrWhiteSpace(settings.Email.From) ? "pgmonitor@example.test" : settings.Email.From,
                To = settings.Email.To.Count > 0 ? settings.Email.To : ["demo@example.test"],
            }
            : settings.Email;

        MailNotifier? notifier = null;
        OutboxWorker? outbox = null;
        if (email.Enabled)
        {
            IEmailSender sender = demo ? new FileDropEmailSender(Paths.MailDrop) : new SmtpEmailSender(email, Credentials);
            notifier = new MailNotifier(store, email, new MailComposer(email, settings.DatabaseApps), simulated: demo);
            outbox = new OutboxWorker(store, sender, email, time);
        }

        var step = demoInterval ?? TimeSpan.FromMilliseconds(500);
        var options = demo
            // Demo runs faster than real time so an incident shows up within a minute; the engine uses sample time.
            ? new MonitorOptions
            {
                Incident = settings.Incident,
                Storage = settings.Storage,
                NormalInterval = step,
                IncidentInterval = step,
                MailCheckEvery = TimeSpan.FromSeconds(2),
            }
            : settings.Sampling.ToOptions(settings);

        var loop = new MonitorLoop(holder.Source, store, options, notifier, outbox, time, loggers.CreateLogger<MonitorLoop>());
        loop.Updated += (_, s) => SnapshotReceived?.Invoke(this, s);

        Store = store;
        Loop = loop;
        _source = holder.Disposable;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _run = Task.Run(() => loop.RunAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_run is not null)
            {
                try
                {
                    await _run.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _cts.Dispose();
            _cts = null;
            _run = null;
        }
        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            _source = null;
        }
        Store?.Dispose();
        Store = null;
        Loop = null;
    }

    /// <summary>Opens one live connection with the given values and reports what the server says. Read-only.</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(PostgresConnectionSettings settings, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        password ??= Credentials.Read(CredentialTargets.Postgres(settings.Username, settings.Host, settings.Port));
        await using var source = new PostgresSampleSource(settings, _ => ValueTask.FromResult(password));
        var sw = Stopwatch.StartNew();
        var sample = await source.CollectAsync(ct).ConfigureAwait(false);
        if (!sample.Succeeded)
        {
            return new(false, $"Bağlanılamadı: {sample.Failure} – {source.LastError}");
        }
        var caps = source.Capabilities!;
        var redacted = sample.Sessions.Count(r => r.IsRedacted);
        var timing = source.LastTimings is { } t ? $"{t.Identity.TotalMilliseconds:0} ms + {t.Activity.TotalMilliseconds:0} ms" : "-";
        return new(true,
            $"Bağlantı başarılı ({sw.Elapsed.TotalSeconds:0.0} sn).\n" +
            $"PostgreSQL {caps.VersionText}\n" +
            $"Salt okunur: {(caps.ReadOnlyEnforced ? "EVET (default_transaction_read_only = on)" : "HAYIR")}\n" +
            $"Yetki: {(caps.IsSuperuser ? "superuser" : caps.ReadAllStats ? "pg_read_all_stats" : "kısıtlı")} – görünürlük {sample.Visibility}\n" +
            $"Görünen session: {sample.Sessions.Count}, ayrıntısı gizli: {redacted}\n" +
            $"Sorgu süreleri: {timing}\n" +
            $"Sorgu metni sınırı: {caps.QueryTextLimitBytes} bayt");
    }

    public async Task SendTestMailAsync(EmailSettings email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        var message = new MimeKit.MimeMessage();
        message.From.Add(MimeKit.MailboxAddress.Parse(email.From));
        foreach (var to in email.To)
        {
            message.To.Add(MimeKit.MailboxAddress.Parse(to));
        }
        message.Subject = "[PgMonitor] Test maili";
        message.Body = new MimeKit.TextPart("plain") { Text = "PgMonitor test maili. Bu mail geldiyse SMTP ayarları çalışıyor." };
        await new SmtpEmailSender(email, Credentials).SendAsync(message, ct).ConfigureAwait(false);
    }

    private static SourceHolder DemoSource(AppSettings settings)
    {
        var scenario = Scenarios.All.FirstOrDefault(s => s.Id == settings.DemoScenario) ?? Scenarios.S05LockChain;
        var now = DateTimeOffset.UtcNow;
        scenario = scenario with { Start = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero) };
        return new SourceHolder(new SimulatedSampleSource(scenario, loop: true), null);
    }

    private SourceHolder LiveSource(AppSettings settings)
    {
        var pg = settings.Postgres ?? throw new InvalidOperationException("PostgreSQL bağlantı ayarları eksik.");
        var target = CredentialTargets.Postgres(pg.Username, pg.Host, pg.Port);
        var source = new PostgresSampleSource(pg, _ => ValueTask.FromResult(Credentials.Read(target)));
        return new SourceHolder(source, source);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed record SourceHolder(Core.Model.ISampleSource Source, IAsyncDisposable? Disposable);
}
