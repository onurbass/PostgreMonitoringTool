using System.Data.Common;
using System.Globalization;
using PgMonitor.Core.Model;

namespace PgMonitor.Infrastructure.Postgres;

/// <summary>What the monitoring account may see, read once per connection.</summary>
public sealed record ServerCapabilities(
    int VersionNum,
    bool IsSuperuser,
    bool ReadAllStats,
    bool ReadOnlyEnforced,
    int QueryTextLimitBytes)
{
    public bool CanSeeAllSessions => IsSuperuser || ReadAllStats;

    /// <summary>pg_stat_get_backend_subxact() exists from PostgreSQL 16 (TECHNICAL_DECISIONS D2).</summary>
    public bool HasBackendSubxact => VersionNum >= 160000;

    public string VersionText => string.Create(CultureInfo.InvariantCulture, $"{VersionNum / 10000}.{VersionNum % 10000}");
}

/// <summary>
/// Turns PostgreSQL result rows into domain objects. Works on any <see cref="DbDataReader"/>, so it is unit-tested
/// with in-memory DataTable readers. Robust to NULLs (privileges, unix sockets) and to DateTime/DateTimeOffset.
/// </summary>
public static class PgRowMapper
{
    /// <summary>PostgreSQL's placeholder for another user's query text without pg_read_all_stats.</summary>
    public const string InsufficientPrivilege = "<insufficient privilege>";

    public static SessionRow MapSession(DbDataReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var query = Str(r, "query");
        var state = Str(r, "state");
        return new SessionRow
        {
            Pid = Convert.ToInt32(r["pid"], CultureInfo.InvariantCulture),
            BackendStart = Time(r, "backend_start"),
            Database = Str(r, "datname"),
            UserName = Str(r, "usename"),
            ApplicationName = Str(r, "application_name"),
            ClientAddress = Str(r, "client_addr"),
            State = state,
            WaitEventType = Str(r, "wait_event_type"),
            WaitEvent = Str(r, "wait_event"),
            TransactionStart = Time(r, "xact_start"),
            QueryStart = Time(r, "query_start"),
            StateChange = Time(r, "state_change"),
            BackendXid = Str(r, "backend_xid"),
            BackendXmin = Str(r, "backend_xmin"),
            Query = string.Equals(query, InsufficientPrivilege, StringComparison.Ordinal) ? null : query,
            BlockingPids = Pids(r, "blocking_pids"),
            // Other users' rows: the placeholder query and/or a NULL state for a client backend.
            IsRedacted = string.Equals(query, InsufficientPrivilege, StringComparison.Ordinal) || state is null,
        };
    }

    public static ServerIdentity MapIdentity(DbDataReader r, out DateTimeOffset sampledAt)
    {
        ArgumentNullException.ThrowIfNull(r);
        sampledAt = Time(r, "sampled_at") ?? throw new InvalidOperationException("now() returned NULL");
        return new ServerIdentity(
            Convert.ToInt32(r["version_num"], CultureInfo.InvariantCulture),
            Time(r, "postmaster_start") ?? DateTimeOffset.MinValue,
            Str(r, "server_addr"),
            Convert.ToBoolean(r["in_recovery"], CultureInfo.InvariantCulture));
    }

    public static ServerCapabilities MapCapabilities(DbDataReader r, string readOnlySetting)
    {
        ArgumentNullException.ThrowIfNull(r);
        _ = int.TryParse(Str(r, "query_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var querySize);
        return new ServerCapabilities(
            Convert.ToInt32(r["version_num"], CultureInfo.InvariantCulture),
            Convert.ToBoolean(r["is_superuser"], CultureInfo.InvariantCulture),
            Convert.ToBoolean(r["read_all_stats"], CultureInfo.InvariantCulture),
            string.Equals(readOnlySetting, "on", StringComparison.OrdinalIgnoreCase),
            querySize);
    }

    /// <summary>
    /// Partial as soon as any row came back redacted, Full otherwise. We report counts and never guess how many
    /// sessions are hidden (TECHNICAL_DECISIONS D6).
    /// </summary>
    public static Visibility Classify(IReadOnlyList<SessionRow> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.Any(s => s.IsRedacted) ? Visibility.Partial : Visibility.Full;
    }

    private static string? Str(DbDataReader r, string name)
    {
        var o = r[name];
        return o is null or DBNull ? null : Convert.ToString(o, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? Time(DbDataReader r, string name) => r[name] switch
    {
        null or DBNull => null,
        DateTimeOffset dto => dto,
        // Npgsql returns timestamptz as DateTime with Kind=Utc.
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)),
        var other => throw new InvalidCastException($"Column '{name}' has unexpected type {other.GetType().Name}"),
    };

    private static IReadOnlyList<int> Pids(DbDataReader r, string name) => r[name] switch
    {
        null or DBNull => [],
        int[] a => a,
        IEnumerable<int> e => e.ToList(),
        var other => throw new InvalidCastException($"Column '{name}' has unexpected type {other.GetType().Name}"),
    };
}
