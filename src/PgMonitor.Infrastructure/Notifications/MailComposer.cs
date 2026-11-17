using System.Globalization;
using System.Text;
using MimeKit;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Locks;
using PgMonitor.Core.Model;

namespace PgMonitor.Infrastructure.Notifications;

/// <summary>What a mail is about.</summary>
public sealed record MailContext(
    IncidentEvent Trigger,
    IncidentInfo Incident,
    LockEvaluation? Current,
    LockEvaluation? Peak,
    DateTimeOffset? PeakAt,
    Visibility Visibility,
    bool Simulated);

/// <summary>
/// Builds the incident e-mail (Turkish, plain text). Labels evidence as observed or suspected; never claims a root cause.
/// Contains SQL text up to <see cref="EmailSettings.SqlMaxChars"/> (product owner decision D9). Never contains secrets.
/// </summary>
public sealed class MailComposer(EmailSettings settings, IReadOnlyDictionary<string, string>? appNames = null, TimeZoneInfo? timeZone = null)
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private readonly IReadOnlyDictionary<string, string> _apps = appNames ?? new Dictionary<string, string>();
    private readonly TimeZoneInfo _tz = timeZone ?? TimeZoneInfo.Local;

    public static string KindText(IncidentEventKind kind) => kind switch
    {
        IncidentEventKind.Opened => "AÇILDI",
        IncidentEventKind.Reopened => "YENİDEN AÇILDI",
        IncidentEventKind.SeverityEscalated => "BÜYÜDÜ",
        IncidentEventKind.Closed => "KAPANDI",
        _ => kind.ToString().ToUpperInvariant(),
    };

    public static string SeverityText(Severity s) => s switch
    {
        Severity.Warning => "Uyarı",
        Severity.High => "Yüksek",
        Severity.Critical => "Kritik",
        _ => "-",
    };

    public MimeMessage Compose(MailContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var msg = new MimeMessage();
        if (!string.IsNullOrWhiteSpace(settings.From))
        {
            msg.From.Add(MailboxAddress.Parse(settings.From));
        }
        foreach (var to in settings.To)
        {
            msg.To.Add(MailboxAddress.Parse(to));
        }
        msg.Subject = Subject(ctx);
        msg.Date = ctx.Trigger.At;
        msg.Body = new TextPart("plain") { Text = Body(ctx) };
        return msg;
    }

    public string Subject(MailContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var eval = ctx.Current ?? ctx.Peak;
        var dbs = string.Join(", ", ctx.Incident.Databases.Select(Db));
        var waiters = eval is null ? "" : string.Create(Tr, $" – {eval.Analysis.WaiterCount} lock bekleyen");
        var prefix = ctx.Simulated ? "[SİMÜLASYON] " : "";
        return string.Create(Tr,
            $"{prefix}[PgMonitor] Olay #{ctx.Incident.Id} {KindText(ctx.Trigger.Kind)} – {SeverityText(ctx.Incident.Severity)} – {dbs}{waiters}");
    }

    public string Body(MailContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sb = new StringBuilder();
        var inc = ctx.Incident;
        if (ctx.Simulated)
        {
            sb.AppendLine("*** SİMÜLASYON VERİSİ – gerçek bir veritabanından gelmiyor ***").AppendLine();
        }

        sb.AppendLine(Tr, $"Olay #{inc.Id} – {KindText(ctx.Trigger.Kind)} – Önem: {SeverityText(inc.Severity)}");
        sb.AppendLine(Tr, $"Olay zamanı     : {Time(ctx.Trigger.At)}");
        sb.AppendLine(Tr, $"Başlangıç       : {Time(inc.OpenedAt)}");
        if (inc.ClosedAt is { } closed)
        {
            sb.AppendLine(Tr, $"Bitiş           : {Time(closed)} (süre {IncidentEngine.Format(closed - inc.OpenedAt)})");
        }
        sb.AppendLine(Tr, $"Veritabanları   : {string.Join(", ", inc.Databases.Select(Db))}");
        sb.AppendLine(Tr, $"Görünürlük      : {VisibilityText(ctx.Visibility)}");
        sb.AppendLine(Tr, $"Açıklama        : {ctx.Trigger.Detail}");
        sb.AppendLine();

        if (ctx.Current is { Triggered: true } current)
        {
            sb.AppendLine("ŞU ANKİ DURUM (gözlem)");
            Section(sb, current);
        }
        if (ctx.Peak is { } peak && (ctx.Current is null || !ctx.Current.Triggered || !ReferenceEquals(peak, ctx.Current)))
        {
            sb.AppendLine(Tr, $"EN KÖTÜ AN: {(ctx.PeakAt is { } p ? Time(p) : "-")} (gözlem)");
            Section(sb, peak);
        }

        sb.AppendLine("Notlar:");
        sb.AppendLine("- \"Kök bloklayıcı\" PostgreSQL'in pg_blocking_pids() ile bildirdiği zincirin başıdır; asıl neden olduğu");
        sb.AppendLine("  kesin değildir (şüpheli). Bekleme süresi, sorgunun başlangıcından ölçülür (üst sınır).");
        sb.AppendLine("- Tüm örnekler yerel SQLite dosyasında saklanır; ayrıntı için PgMonitor uygulamasında bu olayı açın.");
        sb.AppendLine("- PgMonitor veritabanına salt okunur bağlanır; hiçbir session'ı sonlandırmaz.");
        return sb.ToString();
    }

    private void Section(StringBuilder sb, LockEvaluation e)
    {
        var a = e.Analysis;
        sb.AppendLine(Tr, $"  Lock bekleyen: {a.WaiterCount} (eşiği aşan {e.QualifyingWaiters}), en uzun bekleme {IncidentEngine.Format(a.LongestWait)}");
        sb.AppendLine();

        var roots = a.RootBlockers.Take(settings.MaxSuspectsInMail).ToList();
        if (roots.Count > 0)
        {
            sb.AppendLine("  KÖK BLOKLAYICILAR (şüpheli)");
            var i = 1;
            foreach (var r in roots)
            {
                if (r.IsPreparedTransaction)
                {
                    sb.AppendLine(Tr, $"  {i++}. Hazırlanmış (prepared) transaction – {r.BlockedCount} session bekliyor");
                    continue;
                }
                if (r.Session is not { } s)
                {
                    sb.AppendLine(Tr, $"  {i++}. pid {r.Pid} – örnekte görünmüyor – {r.BlockedCount} session bekliyor");
                    continue;
                }
                sb.AppendLine(Tr, $"  {i++}. pid {s.Pid} | {Db(s.Database)} | kullanıcı {s.UserName ?? "-"} | {s.ClientAddress ?? "-"} | {s.State ?? "-"}");
                sb.AppendLine(Tr, $"     transaction yaşı {Age(s.TransactionStart, e)} | {r.BlockedCount} session bekliyor (zincir derinliği {r.ChainDepth})");
                AppendSql(sb, s.Query, "     ");
            }
            sb.AppendLine();
        }

        var waiters = a.Waiters
            .OrderBy(w => w.QueryStart ?? DateTimeOffset.MaxValue)
            .ThenBy(w => w.Pid)
            .Take(settings.MaxSuspectsInMail)
            .ToList();
        if (waiters.Count > 0)
        {
            sb.AppendLine("  EN UZUN BEKLEYENLER");
            foreach (var w in waiters)
            {
                sb.AppendLine(Tr, $"  - pid {w.Pid} | {Db(w.Database)} | {w.WaitEventType}:{w.WaitEvent} | bekleme {Age(w.QueryStart, e)} | bloklayan {string.Join(",", w.BlockingPids.Distinct())}");
                AppendSql(sb, w.Query, "    ");
            }
            sb.AppendLine();
        }
    }

    private void AppendSql(StringBuilder sb, string? sql, string indent)
    {
        if (settings.SqlMaxChars <= 0 || string.IsNullOrWhiteSpace(sql))
        {
            return;
        }
        var oneLine = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var cut = oneLine.Length > settings.SqlMaxChars ? oneLine[..settings.SqlMaxChars] + " …(kısaltıldı)" : oneLine;
        sb.Append(indent).Append("SQL: ").AppendLine(cut);
    }

    private static string Age(DateTimeOffset? since, LockEvaluation e) =>
        since is null ? "-" : IncidentEngine.Format(e.Analysis.SampledAt - since.Value);

    private string Db(string? db) =>
        db is null ? "-" : _apps.TryGetValue(db, out var app) && !string.IsNullOrWhiteSpace(app) ? $"{db} ({app})" : db;

    private string Time(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, _tz).ToString("yyyy-MM-dd HH:mm:ss (zzz)", CultureInfo.InvariantCulture);

    private static string VisibilityText(Visibility v) => v switch
    {
        Visibility.Full => "Tam (tüm session'lar görünüyor)",
        Visibility.Partial => "KISMİ – bazı session'ların ayrıntısı yetki nedeniyle gizli",
        Visibility.OwnOnly => "SADECE KENDİ SESSION'LARIM",
        _ => "Bilinmiyor",
    };
}
