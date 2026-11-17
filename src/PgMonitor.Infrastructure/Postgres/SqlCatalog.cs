namespace PgMonitor.Infrastructure.Postgres;

/// <summary>
/// Every SQL statement this application can send to PostgreSQL. Nothing else is ever executed:
/// there is no free-SQL feature. A test (SqlGuardTests) checks every constant in this class for
/// write, DDL and admin keywords. Add a statement only here, and only read-only.
/// </summary>
public static class SqlCatalog
{
    /// <summary>Run once per new connection. Refuse to monitor unless it returns "on".</summary>
    public const string ReadOnlyCheck = "SHOW default_transaction_read_only";

    /// <summary>Run once per new connection: version and what the account may see.</summary>
    public const string Capabilities = """
        SELECT current_setting('server_version_num')::int                        AS version_num,
               current_setting('track_activity_query_size')                      AS query_size,
               r.rolsuper                                                        AS is_superuser,
               pg_has_role(current_user, 'pg_read_all_stats', 'USAGE')           AS read_all_stats
        FROM pg_roles r
        WHERE r.rolname = current_user
        """;

    /// <summary>
    /// Run every sample before <see cref="Activity"/>: server clock and node identity
    /// (a change of postmaster start or address means restart or failover).
    /// </summary>
    public const string ServerIdentity = """
        SELECT now()                                         AS sampled_at,
               current_setting('server_version_num')::int    AS version_num,
               pg_postmaster_start_time()                    AS postmaster_start,
               host(inet_server_addr())                      AS server_addr,
               pg_is_in_recovery()                           AS in_recovery
        """;

    /// <summary>
    /// Run every sample: all client sessions except our own. pg_blocking_pids() is evaluated only for sessions
    /// waiting on a heavyweight lock, because it briefly takes the lock manager's shared state (TECHNICAL_DECISIONS D3).
    /// </summary>
    public const string Activity = """
        SELECT a.pid,
               a.backend_start,
               a.datname,
               a.usename,
               a.application_name,
               host(a.client_addr)                AS client_addr,
               a.state,
               a.wait_event_type,
               a.wait_event,
               a.xact_start,
               a.query_start,
               a.state_change,
               a.backend_xid::text                AS backend_xid,
               a.backend_xmin::text               AS backend_xmin,
               left(a.query, 4000)                AS query,
               CASE WHEN a.wait_event_type = 'Lock'
                    THEN pg_blocking_pids(a.pid) END AS blocking_pids
        FROM pg_stat_activity a
        WHERE a.backend_type = 'client backend'
          AND a.pid <> pg_backend_pid()
        """;

    /// <summary>All statements, for the guard test and the connection-check screen.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(ReadOnlyCheck)] = ReadOnlyCheck,
        [nameof(Capabilities)] = Capabilities,
        [nameof(ServerIdentity)] = ServerIdentity,
        [nameof(Activity)] = Activity,
    };
}
