using System.Globalization;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Locks;
using PgMonitor.Core.Model;

namespace PgMonitor.App.ViewModels;

/// <summary>Display helpers: local time, short durations, database with mapped application name.</summary>
public static class Fmt
{
    public static string Time(DateTimeOffset? t) =>
        t is null ? "-" : t.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Clock(DateTimeOffset? t) =>
        t is null ? "-" : t.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Age(DateTimeOffset now, DateTimeOffset? since) =>
        since is null ? "-" : IncidentEngine.Format(now - since.Value);

    public static string Db(string? db, IReadOnlyDictionary<string, string> apps) =>
        db is null ? "-" : apps.TryGetValue(db, out var a) && !string.IsNullOrWhiteSpace(a) ? $"{db} ({a})" : db;

    public static string OneLine(string? sql, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "";
        }
        var s = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > max ? s[..max] + " …" : s;
    }

    public static string Severity(Severity s) => s switch
    {
        Core.Incidents.Severity.Warning => "Uyarı",
        Core.Incidents.Severity.High => "Yüksek",
        Core.Incidents.Severity.Critical => "Kritik",
        _ => "-",
    };

    public static string Event(IncidentEventKind k) => k switch
    {
        IncidentEventKind.Opened => "Olay açıldı",
        IncidentEventKind.Reopened => "Olay yeniden açıldı",
        IncidentEventKind.SeverityEscalated => "Önem arttı",
        IncidentEventKind.DatabaseAffected => "Yeni veritabanı etkilendi",
        IncidentEventKind.RecoveryStarted => "Düzelme başladı",
        IncidentEventKind.Reactivated => "Tekrar aktif",
        IncidentEventKind.Closed => "Olay kapandı",
        IncidentEventKind.GapStarted => "Veri alınamıyor",
        IncidentEventKind.GapEnded => "Veri alımı sürüyor",
        IncidentEventKind.ServerChanged => "Sunucu yeniden başladı / değişti",
        _ => k.ToString(),
    };

    public static string Visibility(Visibility v) => v switch
    {
        Core.Model.Visibility.Full => "Tam",
        Core.Model.Visibility.Partial => "KISMİ",
        Core.Model.Visibility.OwnOnly => "Sadece kendi",
        _ => "Bilinmiyor",
    };
}

public sealed record BlockerRow(string Pid, string Database, string User, string Client, string State, string TxAge, string StateAge, int Blocked, int Depth, string Sql, string FullSql)
{
    public static BlockerRow From(RootBlocker r, DateTimeOffset now, IReadOnlyDictionary<string, string> apps)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.IsPreparedTransaction)
        {
            return new("0", "-", "-", "-", "prepared transaction", "-", "-", r.BlockedCount, r.ChainDepth, "", "");
        }
        var s = r.Session;
        return new(
            r.Pid.ToString(CultureInfo.InvariantCulture),
            Fmt.Db(s?.Database, apps),
            s?.UserName ?? "-",
            s?.ClientAddress ?? "-",
            s?.State ?? (s is null ? "örnekte yok" : "-"),
            Fmt.Age(now, s?.TransactionStart),
            Fmt.Age(now, s?.StateChange),
            r.BlockedCount,
            r.ChainDepth,
            Fmt.OneLine(s?.Query),
            s?.Query ?? "");
    }
}

public sealed record SessionRowView(string Pid, string Database, string User, string Client, string State, string Wait, string TxAge, string StateAge, string QueryAge, string BlockedBy, string Sql, string FullSql, bool IsLockWaiter)
{
    public static SessionRowView From(SessionRow r, DateTimeOffset now, IReadOnlyDictionary<string, string> apps)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(
            r.Pid.ToString(CultureInfo.InvariantCulture),
            Fmt.Db(r.Database, apps),
            r.UserName ?? "-",
            r.ClientAddress ?? "-",
            r.IsRedacted ? "(gizli)" : r.State ?? "-",
            r.WaitEventType is null ? "" : $"{r.WaitEventType}:{r.WaitEvent}",
            Fmt.Age(now, r.TransactionStart),
            Fmt.Age(now, r.StateChange),
            Fmt.Age(now, r.QueryStart),
            string.Join(",", r.BlockingPids.Distinct()),
            Fmt.OneLine(r.Query),
            r.Query ?? "",
            r.IsWaitingOnLock);
    }
}

/// <summary>A transaction over the long-transaction threshold, live or from history.</summary>
public sealed record LongTxRow(
    string Pid, string Database, string User, string Client, string State, string Wait,
    string TxAge, string StateAge, string QueryAge, string FirstSeen, string End, string Sql, string FullSql)
{
    public static LongTxRow From(Core.Transactions.LongTransaction t, DateTimeOffset now, IReadOnlyDictionary<string, string> apps)
    {
        ArgumentNullException.ThrowIfNull(t);
        var r = t.Session;
        return new(
            r.Pid.ToString(CultureInfo.InvariantCulture), Fmt.Db(r.Database, apps), r.UserName ?? "-", r.ClientAddress ?? "-",
            r.State ?? "-", r.WaitEventType is null ? "" : $"{r.WaitEventType}:{r.WaitEvent}",
            IncidentEngine.Format(t.TransactionAge), Fmt.Age(now, r.StateChange), Fmt.Age(now, r.QueryStart),
            Fmt.Time(t.FirstSeen), "sürüyor", Fmt.OneLine(r.Query), r.Query ?? "");
    }

    /// <summary>Durations are as of the last time the transaction was seen.</summary>
    public static LongTxRow From(Infrastructure.Storage.StoredLongTransaction s, IReadOnlyDictionary<string, string> apps)
    {
        ArgumentNullException.ThrowIfNull(s);
        var at = s.LastSeen;
        return new(
            s.Pid.ToString(CultureInfo.InvariantCulture), Fmt.Db(s.Database, apps), s.UserName ?? "-", s.ClientAddress ?? "-",
            s.State ?? "-", s.WaitEventType is null ? "" : $"{s.WaitEventType}:{s.WaitEvent}",
            Fmt.Age(at, s.TransactionStart), Fmt.Age(at, s.StateChange), Fmt.Age(at, s.QueryStart),
            Fmt.Time(s.FirstSeen),
            s.EndedAt is null ? "sürüyor" : $"{Fmt.Time(s.LastSeen)} son görüldü – {s.EndReason}",
            Fmt.OneLine(s.Query), s.Query ?? "");
    }
}

public sealed record DatabaseRow(string Database, int Sessions, int Active, int IdleInTransaction, int LockWaiting);

public sealed record EventRow(string Time, string Kind, string Severity, string Incident, string Detail)
{
    public static EventRow From(IncidentEvent e) => new(
        Fmt.Time(e.At),
        Fmt.Event(e.Kind),
        Fmt.Severity(e.Severity),
        e.IncidentId > 0 ? "#" + e.IncidentId.ToString(CultureInfo.InvariantCulture) : "",
        e.Detail);
}
