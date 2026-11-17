using System.Globalization;
using MimeKit;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.Infrastructure.Notifications;

/// <summary>
/// Decides which incident events become e-mails and queues them in the outbox (never sends directly, so a slow or
/// broken SMTP server cannot block sampling). Opened/Reopened and Closed are always queued; escalations respect a
/// cooldown. Each mail has a dedupe key, so re-processing the same event never queues it twice.
/// </summary>
public sealed class MailNotifier(IMonitorStore store, EmailSettings settings, MailComposer composer, bool simulated)
{
    private long _peakIncident;
    private LockEvaluation? _peak;
    private DateTimeOffset? _peakAt;

    /// <summary>Returns the number of mails queued.</summary>
    public int OnResult(ClusterSample sample, EngineResult result)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(result);
        var incident = result.State.Incident;
        TrackPeak(incident, result.Lock, sample.SampledAt);

        if (!settings.Enabled || incident is null)
        {
            return 0;
        }

        var queued = 0;
        foreach (var e in result.Events.Where(e => e.IncidentId == incident.Id))
        {
            var kind = e.Kind switch
            {
                IncidentEventKind.Opened or IncidentEventKind.Reopened => "Opened",
                IncidentEventKind.SeverityEscalated when settings.SendOnEscalation => "Escalated",
                IncidentEventKind.Closed => "Closed",
                _ => null,
            };
            if (kind is null)
            {
                continue;
            }
            if (kind == "Escalated" && store.LastMailAt(incident.Id, kind) is { } last && e.At - last < settings.EscalationCooldown)
            {
                continue;
            }

            var ctx = new MailContext(e, incident, result.Lock is { Triggered: true } ? result.Lock : null, _peak, _peakAt, sample.Visibility, simulated);
            var message = composer.Compose(ctx);
            using var ms = new MemoryStream();
            message.WriteTo(ms);
            var key = string.Create(CultureInfo.InvariantCulture, $"{incident.Id}:{e.Kind}:{e.At.ToUnixTimeMilliseconds()}");
            if (store.EnqueueMail(new OutboxMail(key, incident.Id, kind, e.At, message.Subject ?? "", ms.ToArray())))
            {
                queued++;
            }
        }
        return queued;
    }

    /// <summary>Remembers the worst moment of the current incident for the closing mail.</summary>
    private void TrackPeak(IncidentInfo? incident, LockEvaluation? eval, DateTimeOffset at)
    {
        if (incident is null)
        {
            return;
        }
        if (incident.Id != _peakIncident)
        {
            _peakIncident = incident.Id;
            _peak = null;
            _peakAt = null;
        }
        if (eval is { Triggered: true } && (_peak is null || eval.Analysis.WaiterCount > _peak.Analysis.WaiterCount))
        {
            _peak = eval;
            _peakAt = at;
        }
    }
}

/// <summary>Sends queued mails with retry and exponential backoff (1, 2, 4 … 60 min), giving up after a limit.</summary>
public sealed class OutboxWorker(IMonitorStore store, IEmailSender sender, EmailSettings settings, TimeProvider time)
{
    public string? LastError { get; private set; }

    /// <summary>Sends what is due. Never throws for send failures. Returns how many were sent.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var mail in store.DueMails(time.GetUtcNow()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var ms = new MemoryStream(mail.Mime);
                var message = await MimeMessage.LoadAsync(ms, cancellationToken).ConfigureAwait(false);
                await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
                store.MarkMailSent(mail.Id, time.GetUtcNow());
                LastError = null;
                sent++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A failing SMTP server must never stop monitoring.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var now = time.GetUtcNow();
                var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, mail.Attempts)));
                DateTimeOffset? next = now + delay - mail.CreatedAt > settings.GiveUpAfter ? null : now + delay;
                LastError = ex.GetType().Name; // the message may contain server names; keep only the type
                store.MarkMailFailed(mail.Id, next, LastError);
            }
        }
        return sent;
    }
}
