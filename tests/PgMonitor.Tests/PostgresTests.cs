using System.Data;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;
using PgMonitor.Core.Model;
using PgMonitor.Infrastructure.Postgres;

namespace PgMonitor.Tests;

/// <summary>Read-only guard, connection string, row mapping and error mapping, without a PostgreSQL server (TEST_PLAN 4).</summary>
public partial class PostgresTests
{
    private const string Canary = "CANARY-pw-123";

    // ------------------------------------------------------------------ SQL guard

    private static readonly string[] Forbidden =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "REVOKE", "COPY", "DO", "CALL",
        "SET", "RESET", "LOCK", "VACUUM", "ANALYZE", "CLUSTER", "REINDEX", "LISTEN", "NOTIFY", "PREPARE", "EXECUTE",
        "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "set_config", "pg_stat_reset", "pg_advisory",
        "pg_sleep", "lo_", "dblink", "pg_read_file", "pg_ls_dir", "nextval", "setval", "txid_current", "pg_switch_wal",
    ];

    public static TheoryData<string> CatalogNames() => new(SqlCatalog.All.Keys);

    [Theory]
    [MemberData(nameof(CatalogNames))]
    public void Catalog_statement_is_read_only(string name)
    {
        var full = SqlCatalog.All[name];
        var first = WordRegex().Match(full).Value.ToUpperInvariant();
        Assert.Contains(first, new[] { "SELECT", "SHOW" });
        Assert.DoesNotContain(";", full, StringComparison.Ordinal);

        // String literals such as 'Lock' or 'pg_read_all_stats' are data, not commands: scan the SQL without them.
        var sql = LiteralRegex().Replace(full, "''");
        foreach (var word in Forbidden)
        {
            var pattern = word.EndsWith('_') ? $@"\b{Regex.Escape(word)}" : $@"\b{Regex.Escape(word)}\b";
            Assert.False(Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase), $"{name} contains forbidden token '{word}'");
        }
    }

    [Fact]
    public void Every_catalog_constant_is_covered_by_the_guard()
    {
        var constants = typeof(SqlCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(constants, SqlCatalog.All.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Blocking_pids_is_only_evaluated_for_the_oldest_lock_waiters()
    {
        var sql = SqlCatalog.Activity;
        // Rank exists only for Lock waiters, ordered by how long they have waited ...
        Assert.Matches(@"CASE\s+WHEN\s+a\.wait_event_type\s*=\s*'Lock'\s+THEN\s+row_number\(\)\s+OVER\s+\(PARTITION BY a\.wait_event_type = 'Lock' ORDER BY a\.query_start, a\.pid\)", sql);
        // ... and pg_blocking_pids is called only for rank <= the limit (NULL rank = not a Lock waiter = no call).
        Assert.Matches($@"CASE\s+WHEN\s+s\.lock_rank\s*<=\s*{SqlCatalog.MaxBlockingLookups}\s+THEN\s+pg_blocking_pids\(s\.pid\)", sql);
        Assert.Single(Regex.Matches(sql, "pg_blocking_pids"));
        Assert.Equal(50, SqlCatalog.MaxBlockingLookups);
    }

    [Fact]
    public void Live_connection_uses_a_single_pooled_connection()
    {
        var b = new NpgsqlConnectionStringBuilder(Settings.BuildConnectionString(Canary));
        Assert.Equal(1, b.MaxPoolSize);
    }

    [Theory]
    [InlineData(CollectFailureKind.AuthFailed, true)]
    [InlineData(CollectFailureKind.PermissionDenied, true)]
    [InlineData(CollectFailureKind.Timeout, false)]
    [InlineData(CollectFailureKind.Disconnected, false)]
    [InlineData(CollectFailureKind.Unknown, false)]
    public void Only_password_and_permission_errors_stop_retrying(CollectFailureKind kind, bool fatal)
    {
        Assert.Equal(fatal, PostgresSampleSource.IsFatal(kind));
    }

    [Fact]
    public async Task Halted_source_never_contacts_the_server_until_reset()
    {
        // A listener that counts connection attempts: after a halt there must be none.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var attempts = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var accept = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref attempts);
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            var settings = new PostgresConnectionSettings { Host = "127.0.0.1", Port = port, Username = "svc_monitor", ConnectTimeoutSeconds = 2 };
            await using var source = new PostgresSampleSource(settings, _ => ValueTask.FromResult<string?>(Canary));
            source.Halt(CollectFailureKind.AuthFailed, "PostgreSQL error SQLSTATE 28P01");

            for (var i = 0; i < 5; i++)
            {
                var sample = await source.CollectAsync(TestContext.Current.CancellationToken);
                Assert.Equal(CollectFailureKind.AuthFailed, sample.Failure);
            }
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref attempts));
            Assert.Contains("tekrar bağlanılmıyor", source.LastError, StringComparison.Ordinal);

            await source.ResetCredentialsAsync();
            Assert.Null(source.HaltedBecause);
            await source.CollectAsync(TestContext.Current.CancellationToken); // now it does try (and fails: not a real server)
            Assert.True(Volatile.Read(ref attempts) >= 1);
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            try
            {
                await accept;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // expected when the listener stops
            }
        }
    }

    [GeneratedRegex(@"^\s*\w+")]
    private static partial Regex WordRegex();

    [GeneratedRegex("'(?:[^']|'')*'")]
    private static partial Regex LiteralRegex();

    [Fact]
    public void Guard_would_catch_a_write_hidden_after_a_literal()
    {
        var sql = LiteralRegex().Replace("SELECT 'Lock' FROM x WHERE pg_terminate_backend(1)", "''");
        Assert.Matches(@"\bpg_terminate_backend\b", sql);
        Assert.DoesNotMatch(@"\bLock\b", sql);
    }

    // ------------------------------------------------------------------ connection settings

    private static readonly PostgresConnectionSettings Settings = new() { Host = "db.example.test", Username = "svc_monitor" };

    [Fact]
    public void Connection_forces_read_only_and_server_timeout()
    {
        var b = new NpgsqlConnectionStringBuilder(Settings.BuildConnectionString(Canary));
        Assert.Contains("-c default_transaction_read_only=on", b.Options, StringComparison.Ordinal);
        Assert.Contains("-c statement_timeout=3000", b.Options, StringComparison.Ordinal);
        Assert.Equal(4, b.CommandTimeout);
        Assert.Equal("PgMonitor (read-only)", b.ApplicationName);
        Assert.False(b.IncludeErrorDetail);
        Assert.False(b.PersistSecurityInfo);
    }

    [Fact]
    public void Settings_never_expose_the_password()
    {
        Assert.DoesNotContain(Canary, Settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, Settings.Describe(), StringComparison.Ordinal);
        Assert.Equal("svc_monitor@db.example.test:5432/postgres (ssl=Prefer)", Settings.Describe());
        Assert.DoesNotContain(typeof(PostgresConnectionSettings).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ row mapping

    private static DataTable ActivityTable()
    {
        var t = new DataTable();
        foreach (var c in new[] { "pid", "backend_start", "datname", "usename", "application_name", "client_addr", "state", "wait_event_type",
                     "wait_event", "xact_start", "query_start", "state_change", "backend_xid", "backend_xmin", "query", "blocking_pids" })
        {
            t.Columns.Add(c, typeof(object));
        }
        return t;
    }

    private static T Map<T>(DataTable t, Func<IDataReader, T> map)
    {
        using var r = t.CreateDataReader();
        Assert.True(r.Read());
        return map(r);
    }

    [Fact]
    public void Maps_a_full_lock_waiter_row()
    {
        var t = ActivityTable();
        var now = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        t.Rows.Add(4711, now.AddHours(-1), "app_orchid_db", "svc_orchid", "PostgreSQL JDBC Driver", "192.0.2.10", "active", "Lock",
            "transactionid", now.AddSeconds(-50), now.AddSeconds(-45), now.AddSeconds(-45), null, "731002",
            "UPDATE \"orchid$müşteri\" SET \"ığüşöç\" = $1 WHERE \"id\" = $2", new[] { 100, 100 });

        var row = Map(t, r => PgRowMapper.MapSession((System.Data.Common.DbDataReader)r));

        Assert.Equal(4711, row.Pid);
        Assert.Equal(new DateTimeOffset(now.AddHours(-1)), row.BackendStart);
        Assert.Equal(TimeSpan.Zero, row.QueryStart!.Value.Offset);
        Assert.True(row.IsWaitingOnLock);
        Assert.Equal([100, 100], row.BlockingPids); // de-duplication happens in LockGraph
        Assert.Contains("ığüşöç", row.Query, StringComparison.Ordinal);
        Assert.Null(row.BackendXid);
        Assert.False(row.IsRedacted);
    }

    [Fact]
    public void Maps_nulls_everywhere_without_failing()
    {
        var t = ActivityTable();
        t.Rows.Add(4712, null, null, null, null, null, "idle", null, null, null, null, null, null, null, null, null);
        var row = Map(t, r => PgRowMapper.MapSession((System.Data.Common.DbDataReader)r));
        Assert.Null(row.BackendStart);
        Assert.Null(row.ClientAddress); // unix-socket clients have no address
        Assert.Empty(row.BlockingPids);
        Assert.False(row.IsRedacted);
    }

    [Fact]
    public void Insufficient_privilege_row_is_redacted_and_its_placeholder_is_not_kept_as_sql()
    {
        var t = ActivityTable();
        t.Rows.Add(4713, null, "app_lotus_db", "svc_lotus", null, null, null, null, null, null, null, null, null, null, "<insufficient privilege>", null);
        var row = Map(t, r => PgRowMapper.MapSession((System.Data.Common.DbDataReader)r));
        Assert.True(row.IsRedacted);
        Assert.Null(row.Query);
        Assert.Equal("app_lotus_db", row.Database);
        Assert.Equal(Visibility.Partial, PgRowMapper.Classify([row]));
    }

    [Fact]
    public void Cut_query_text_is_kept_as_is()
    {
        var t = ActivityTable();
        var sql = "SELECT " + new string('x', 1017); // 1024 bytes, as cut by track_activity_query_size
        t.Rows.Add(4714, DateTimeOffset.UnixEpoch, "app_orchid_db", "svc_orchid", "", "192.0.2.10", "active", null, null, null, null, null, null, null, sql, null);
        var row = Map(t, r => PgRowMapper.MapSession((System.Data.Common.DbDataReader)r));
        Assert.Equal(1024, row.Query!.Length);
        Assert.Equal(DateTimeOffset.UnixEpoch, row.BackendStart);
    }

    [Fact]
    public void Maps_capabilities_and_identity()
    {
        var caps = new DataTable();
        caps.Columns.Add("version_num", typeof(int));
        caps.Columns.Add("query_size", typeof(string));
        caps.Columns.Add("is_superuser", typeof(bool));
        caps.Columns.Add("read_all_stats", typeof(bool));
        caps.Rows.Add(150010, "1024", false, true);
        var c = Map(caps, r => PgRowMapper.MapCapabilities((System.Data.Common.DbDataReader)r, "on"));
        Assert.True(c.ReadOnlyEnforced);
        Assert.True(c.CanSeeAllSessions);
        Assert.False(c.HasBackendSubxact);
        Assert.Equal("15.10", c.VersionText);
        Assert.Equal(1024, c.QueryTextLimitBytes);
        Assert.False(Map(caps, r => PgRowMapper.MapCapabilities((System.Data.Common.DbDataReader)r, "off")).ReadOnlyEnforced);

        var id = new DataTable();
        foreach (var col in new[] { "sampled_at", "version_num", "postmaster_start", "server_addr", "in_recovery" })
        {
            id.Columns.Add(col, typeof(object));
        }
        var now = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        id.Rows.Add(now, 150010, now.AddDays(-3), null, false);
        DateTimeOffset at = default;
        var identity = Map(id, r => PgRowMapper.MapIdentity((System.Data.Common.DbDataReader)r, out at));
        Assert.Equal(new DateTimeOffset(now), at);
        Assert.Null(identity.ServerAddress);
        Assert.False(identity.InRecovery);
    }

    // ------------------------------------------------------------------ error mapping

    [Theory]
    [InlineData("57014", CollectFailureKind.Timeout)]
    [InlineData("28P01", CollectFailureKind.AuthFailed)]
    [InlineData("28000", CollectFailureKind.AuthFailed)]
    [InlineData("42501", CollectFailureKind.PermissionDenied)]
    [InlineData("57P01", CollectFailureKind.Disconnected)]
    [InlineData("08006", CollectFailureKind.Disconnected)]
    [InlineData("53300", CollectFailureKind.Disconnected)]
    [InlineData("XX000", CollectFailureKind.Unknown)]
    public void Postgres_errors_map_to_kinds_without_message_text(string sqlState, CollectFailureKind expected)
    {
        var ex = new PostgresException($"password {Canary} for host db.example.test", "FATAL", "FATAL", sqlState);
        var (kind, description) = PgErrorMapper.Map(ex);
        Assert.Equal(expected, kind);
        Assert.DoesNotContain(Canary, description, StringComparison.Ordinal);
        Assert.DoesNotContain("example", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Driver_and_network_errors_map_to_kinds()
    {
        Assert.Equal(CollectFailureKind.Timeout, PgErrorMapper.Map(new TimeoutException()).Kind);
        Assert.Equal(CollectFailureKind.Timeout, PgErrorMapper.Map(new NpgsqlException("x", new TimeoutException())).Kind);
        Assert.Equal(CollectFailureKind.Disconnected, PgErrorMapper.Map(new NpgsqlException("x", new SocketException(10061))).Kind);
        Assert.Equal(CollectFailureKind.PermissionDenied, PgErrorMapper.Map(new ReadOnlyNotEnforcedException()).Kind);
        Assert.Equal(CollectFailureKind.Unknown, PgErrorMapper.Map(new InvalidOperationException(Canary)).Kind);
        Assert.DoesNotContain(Canary, PgErrorMapper.Map(new InvalidOperationException(Canary)).SafeDescription, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ real driver, closed local port (no external network)

    [Fact]
    public async Task Unreachable_server_gives_a_failed_sample_not_an_exception()
    {
        var settings = new PostgresConnectionSettings { Host = "127.0.0.1", Port = FreeLocalPort(), Username = "svc_monitor", ConnectTimeoutSeconds = 2 };
        await using var source = new PostgresSampleSource(settings, _ => ValueTask.FromResult<string?>(Canary));

        var sample = await source.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(sample.Succeeded);
        // Linux refuses at once (Disconnected); Windows retries a refused loopback connect until the timeout.
        Assert.Contains(sample.Failure!.Value, new[] { CollectFailureKind.Disconnected, CollectFailureKind.Timeout });
        Assert.Empty(sample.Sessions);
        Assert.NotNull(source.LastError);
        Assert.DoesNotContain(Canary, source.LastError, StringComparison.Ordinal);
        Assert.Equal(SourceKind.Live, source.Kind);
    }

    private static int FreeLocalPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port; // nothing listens here any more
    }
}
