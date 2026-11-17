using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PgMonitor.Core.Aggregation;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Core.Transactions;

namespace PgMonitor.Infrastructure.Storage;

/// <summary>
/// SQLite implementation of <see cref="IMonitorStore"/>. WAL mode, one connection, all calls serialized.
/// Times are stored as Unix milliseconds (UTC); enums as their names so the file is readable with any SQLite browser.
/// </summary>
public sealed class SqliteMonitorStore : IMonitorStore
{
    private const int SchemaVersion = 3;

    private readonly SqliteConnection _db;
    private readonly string _path;
    private readonly Lock _gate = new();

    public SqliteMonitorStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        _db.Open();
        Migrate();
    }

    public string FilePath => _path;

    // ------------------------------------------------------------------ schema

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema {version} is newer than this application ({SchemaVersion}).");
        }
        if (version == 0)
        {
            // auto_vacuum must be chosen before the first table exists; it lets deletes give space back to the OS.
            Exec("PRAGMA auto_vacuum = INCREMENTAL");
        }
        Exec("PRAGMA journal_mode = WAL");
        Exec("PRAGMA synchronous = NORMAL");
        Exec("PRAGMA foreign_keys = ON");
        Exec("PRAGMA busy_timeout = 5000");

        if (version == 0)
        {
            using var tx = _db.BeginTransaction();
            Exec("""
                CREATE TABLE aggregate (
                    window_start      INTEGER PRIMARY KEY,
                    samples           INTEGER NOT NULL,
                    failed_samples    INTEGER NOT NULL,
                    max_sessions      INTEGER NOT NULL,
                    max_active        INTEGER NOT NULL,
                    max_idle_in_tx    INTEGER NOT NULL,
                    max_lock_waiters  INTEGER NOT NULL,
                    max_lock_over     INTEGER NOT NULL,
                    max_lock_wait_s   REAL    NOT NULL,
                    max_xact_age_s    REAL    NOT NULL,
                    worst_visibility  TEXT    NOT NULL,
                    sessions_by_db    TEXT    NOT NULL
                );
                CREATE TABLE incident (
                    id          INTEGER PRIMARY KEY,
                    opened_at   INTEGER NOT NULL,
                    closed_at   INTEGER,
                    status      TEXT    NOT NULL,
                    severity    TEXT    NOT NULL,
                    databases   TEXT    NOT NULL,
                    signals     TEXT    NOT NULL,
                    updated_at  INTEGER NOT NULL
                );
                CREATE TABLE event (
                    id          INTEGER PRIMARY KEY,
                    incident_id INTEGER REFERENCES incident(id) ON DELETE CASCADE,
                    at          INTEGER NOT NULL,
                    kind        TEXT    NOT NULL,
                    severity    TEXT    NOT NULL,
                    detail      TEXT    NOT NULL
                );
                CREATE INDEX ix_event_incident ON event(incident_id, at);
                CREATE INDEX ix_event_at ON event(at);
                CREATE TABLE sample (
                    id            INTEGER PRIMARY KEY,
                    incident_id   INTEGER NOT NULL REFERENCES incident(id) ON DELETE CASCADE,
                    at            INTEGER NOT NULL,
                    phase         TEXT    NOT NULL,
                    visibility    TEXT    NOT NULL,
                    failure       TEXT,
                    redacted_rows INTEGER NOT NULL,
                    UNIQUE (incident_id, at)
                );
                CREATE TABLE session (
                    sample_id     INTEGER NOT NULL REFERENCES sample(id) ON DELETE CASCADE,
                    pid           INTEGER NOT NULL,
                    backend_start INTEGER,
                    db            TEXT,
                    usr           TEXT,
                    app           TEXT,
                    client        TEXT,
                    state         TEXT,
                    wait_type     TEXT,
                    wait_event    TEXT,
                    xact_start    INTEGER,
                    query_start   INTEGER,
                    state_change  INTEGER,
                    backend_xid   TEXT,
                    backend_xmin  TEXT,
                    blocking_pids TEXT,
                    sql_hash      TEXT
                );
                CREATE INDEX ix_session_sample ON session(sample_id);
                CREATE INDEX ix_session_sql ON session(sql_hash);
                CREATE TABLE sql_text (
                    hash TEXT PRIMARY KEY,
                    text TEXT NOT NULL
                );
                """);
            Exec("PRAGMA user_version = 1");
            tx.Commit();
            version = 1;
        }

        if (version == 1)
        {
            using var tx = _db.BeginTransaction();
            Exec("""
                CREATE TABLE outbox (
                    id              INTEGER PRIMARY KEY,
                    dedupe_key      TEXT    NOT NULL UNIQUE,
                    incident_id     INTEGER REFERENCES incident(id) ON DELETE CASCADE,
                    kind            TEXT    NOT NULL,
                    created_at      INTEGER NOT NULL,
                    subject         TEXT    NOT NULL,
                    mime            BLOB    NOT NULL,
                    status          TEXT    NOT NULL,
                    attempts        INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at INTEGER NOT NULL,
                    last_error      TEXT,
                    sent_at         INTEGER
                );
                CREATE INDEX ix_outbox_due ON outbox(status, next_attempt_at);
                """);
            Exec("PRAGMA user_version = 2");
            tx.Commit();
            version = 2;
        }

        if (version == 2)
        {
            using var tx = _db.BeginTransaction();
            Exec("""
                CREATE TABLE long_tx (
                    id            INTEGER PRIMARY KEY,
                    tx_key        TEXT    NOT NULL UNIQUE,
                    pid           INTEGER NOT NULL,
                    backend_start INTEGER,
                    xact_start    INTEGER NOT NULL,
                    db            TEXT,
                    usr           TEXT,
                    app           TEXT,
                    client        TEXT,
                    first_seen    INTEGER NOT NULL,
                    last_seen     INTEGER NOT NULL,
                    ended_at      INTEGER,
                    end_reason    TEXT,
                    state         TEXT,
                    wait_type     TEXT,
                    wait_event    TEXT,
                    query_start   INTEGER,
                    state_change  INTEGER,
                    sql_hash      TEXT
                );
                CREATE INDEX ix_long_tx_seen ON long_tx(first_seen);
                """);
            Exec("PRAGMA user_version = 3");
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ long transactions

    public void RecordLongTransactions(IReadOnlyList<LongTxEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var sqlCmd = Cmd("INSERT OR IGNORE INTO sql_text (hash, text) VALUES ($h, $t)");
            using var cmd = Cmd("""
                INSERT INTO long_tx (tx_key, pid, backend_start, xact_start, db, usr, app, client, first_seen, last_seen,
                                     ended_at, end_reason, state, wait_type, wait_event, query_start, state_change, sql_hash)
                VALUES ($k, $pid, $bs, $xs, $db, $u, $app, $cl, $fs, $ls, $ea, $er, $st, $wt, $we, $qs, $sc, $h)
                ON CONFLICT (tx_key) DO UPDATE SET
                    last_seen = excluded.last_seen, ended_at = excluded.ended_at, end_reason = excluded.end_reason,
                    state = excluded.state, wait_type = excluded.wait_type, wait_event = excluded.wait_event,
                    query_start = excluded.query_start, state_change = excluded.state_change, sql_hash = excluded.sql_hash
                """);
            foreach (var e in events)
            {
                var r = e.Session;
                string? hash = null;
                if (!string.IsNullOrEmpty(r.Query))
                {
                    hash = Hash(r.Query);
                    sqlCmd.Parameters.Clear();
                    sqlCmd.Parameters.AddWithValue("$h", hash);
                    sqlCmd.Parameters.AddWithValue("$t", r.Query);
                    sqlCmd.ExecuteNonQuery();
                }
                var ended = e.Kind == LongTxEventKind.Ended;
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$k", e.Key.Text);
                cmd.Parameters.AddWithValue("$pid", r.Pid);
                cmd.Parameters.AddWithValue("$bs", Db(Ms(r.BackendStart)));
                cmd.Parameters.AddWithValue("$xs", Ms(e.Key.TransactionStart));
                cmd.Parameters.AddWithValue("$db", Db(r.Database));
                cmd.Parameters.AddWithValue("$u", Db(r.UserName));
                cmd.Parameters.AddWithValue("$app", Db(r.ApplicationName));
                cmd.Parameters.AddWithValue("$cl", Db(r.ClientAddress));
                cmd.Parameters.AddWithValue("$fs", Ms(e.FirstSeen));
                cmd.Parameters.AddWithValue("$ls", Ms(e.LastSeen));
                cmd.Parameters.AddWithValue("$ea", ended ? Ms(e.At) : DBNull.Value);
                cmd.Parameters.AddWithValue("$er", Db(e.EndReason));
                cmd.Parameters.AddWithValue("$st", Db(r.State));
                cmd.Parameters.AddWithValue("$wt", Db(r.WaitEventType));
                cmd.Parameters.AddWithValue("$we", Db(r.WaitEvent));
                cmd.Parameters.AddWithValue("$qs", Db(Ms(r.QueryStart)));
                cmd.Parameters.AddWithValue("$sc", Db(Ms(r.StateChange)));
                cmd.Parameters.AddWithValue("$h", Db(hash));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<StoredLongTransaction> ListLongTransactions(int limit = 500)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT l.pid, l.backend_start, l.xact_start, l.db, l.usr, l.app, l.client, l.first_seen, l.last_seen,
                       l.ended_at, l.end_reason, l.state, l.wait_type, l.wait_event, l.query_start, l.state_change, q.text
                FROM long_tx l LEFT JOIN sql_text q ON q.hash = l.sql_hash
                ORDER BY l.first_seen DESC LIMIT $n
                """);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<StoredLongTransaction>();
            while (r.Read())
            {
                list.Add(new StoredLongTransaction(
                    r.GetInt32(0), OptMs(r, 1), FromMs(r.GetInt64(2)), OptStr(r, 3), OptStr(r, 4), OptStr(r, 5), OptStr(r, 6),
                    FromMs(r.GetInt64(7)), FromMs(r.GetInt64(8)), OptMs(r, 9), OptStr(r, 10), OptStr(r, 11), OptStr(r, 12),
                    OptStr(r, 13), OptMs(r, 14), OptMs(r, 15), OptStr(r, 16)));
            }
            return list;
        }
    }

    // ------------------------------------------------------------------ outbox

    public bool EnqueueMail(OutboxMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        lock (_gate)
        {
            using var cmd = Cmd("""
                INSERT INTO outbox (dedupe_key, incident_id, kind, created_at, subject, mime, status, next_attempt_at)
                VALUES ($k, $i, $kind, $c, $s, $m, 'Pending', $c)
                ON CONFLICT (dedupe_key) DO NOTHING
                """);
            cmd.Parameters.AddWithValue("$k", mail.DedupeKey);
            cmd.Parameters.AddWithValue("$i", mail.IncidentId is { } id && IncidentExists(id) ? id : DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", mail.Kind);
            cmd.Parameters.AddWithValue("$c", Ms(mail.CreatedAt));
            cmd.Parameters.AddWithValue("$s", mail.Subject);
            cmd.Parameters.AddWithValue("$m", mail.Mime);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<OutboxMail> DueMails(DateTimeOffset now, int limit = 10)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT id, dedupe_key, incident_id, kind, created_at, subject, mime, attempts
                FROM outbox WHERE status = 'Pending' AND next_attempt_at <= $now
                ORDER BY next_attempt_at, id LIMIT $n
                """);
            cmd.Parameters.AddWithValue("$now", Ms(now));
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<OutboxMail>();
            while (r.Read())
            {
                list.Add(new OutboxMail(r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3), FromMs(r.GetInt64(4)),
                    r.GetString(5), (byte[])r.GetValue(6))
                {
                    Id = r.GetInt64(0),
                    Attempts = r.GetInt32(7),
                });
            }
            return list;
        }
    }

    public void MarkMailSent(long id, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var cmd = Cmd("UPDATE outbox SET status = 'Sent', sent_at = $now, attempts = attempts + 1, last_error = NULL WHERE id = $id");
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Ms(now));
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkMailFailed(long id, DateTimeOffset? nextAttempt, string safeError)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                UPDATE outbox SET attempts = attempts + 1, last_error = $e,
                    status = CASE WHEN $next IS NULL THEN 'Failed' ELSE 'Pending' END,
                    next_attempt_at = COALESCE($next, next_attempt_at)
                WHERE id = $id
                """);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$e", safeError);
            cmd.Parameters.AddWithValue("$next", nextAttempt is null ? DBNull.Value : Ms(nextAttempt.Value));
            cmd.ExecuteNonQuery();
        }
    }

    public DateTimeOffset? LastMailAt(long incidentId, string kind)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT MAX(created_at) FROM outbox WHERE incident_id = $i AND kind = $k");
            cmd.Parameters.AddWithValue("$i", incidentId);
            cmd.Parameters.AddWithValue("$k", kind);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? null : FromMs(Convert.ToInt64(v, CultureInfo.InvariantCulture));
        }
    }

    public OutboxCounts MailCounts()
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT COALESCE(SUM(status = 'Pending'), 0), COALESCE(SUM(status = 'Sent'), 0), COALESCE(SUM(status = 'Failed'), 0) FROM outbox
                """);
            using var r = cmd.ExecuteReader();
            r.Read();
            return new OutboxCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
    }

    // ------------------------------------------------------------------ writes

    public long MaxIncidentId()
    {
        lock (_gate)
        {
            return Convert.ToInt64(Scalar("SELECT COALESCE(MAX(id), 0) FROM incident"), CultureInfo.InvariantCulture);
        }
    }

    public int MarkOpenIncidentsInterrupted(DateTimeOffset now)
    {
        lock (_gate)
        {
            using (var lt = Cmd("UPDATE long_tx SET ended_at = last_seen, end_reason = $r WHERE ended_at IS NULL"))
            {
                lt.Parameters.AddWithValue("$r", "uygulama kapandı – sonu görülmedi");
                lt.ExecuteNonQuery();
            }
            using var cmd = Cmd("UPDATE incident SET status = $s, updated_at = $now WHERE status IN ('Active', 'Recovering')");
            cmd.Parameters.AddWithValue("$s", nameof(IncidentStatus.Interrupted));
            cmd.Parameters.AddWithValue("$now", Ms(now));
            return cmd.ExecuteNonQuery();
        }
    }

    public void WriteAggregate(AggregateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            using var cmd = Cmd("""
                INSERT OR REPLACE INTO aggregate VALUES ($w, $n, $f, $s, $a, $i, $l, $lo, $lw, $x, $v, $d)
                """);
            cmd.Parameters.AddWithValue("$w", Ms(row.WindowStart));
            cmd.Parameters.AddWithValue("$n", row.Samples);
            cmd.Parameters.AddWithValue("$f", row.FailedSamples);
            cmd.Parameters.AddWithValue("$s", row.MaxSessions);
            cmd.Parameters.AddWithValue("$a", row.MaxActive);
            cmd.Parameters.AddWithValue("$i", row.MaxIdleInTransaction);
            cmd.Parameters.AddWithValue("$l", row.MaxLockWaiters);
            cmd.Parameters.AddWithValue("$lo", row.MaxLockWaitersOverThreshold);
            cmd.Parameters.AddWithValue("$lw", row.MaxLockWaitSeconds);
            cmd.Parameters.AddWithValue("$x", row.MaxTransactionAgeSeconds);
            cmd.Parameters.AddWithValue("$v", row.WorstVisibility.ToString());
            cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(row.MaxSessionsByDatabase));
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertIncident(IncidentInfo incident, IncidentStatus status)
    {
        ArgumentNullException.ThrowIfNull(incident);
        lock (_gate)
        {
            using var cmd = Cmd("""
                INSERT INTO incident (id, opened_at, closed_at, status, severity, databases, signals, updated_at)
                VALUES ($id, $o, $c, $st, $sev, $dbs, $sig, $u)
                ON CONFLICT(id) DO UPDATE SET
                    closed_at = excluded.closed_at, status = excluded.status, severity = excluded.severity,
                    databases = excluded.databases, signals = excluded.signals, updated_at = excluded.updated_at
                """);
            cmd.Parameters.AddWithValue("$id", incident.Id);
            cmd.Parameters.AddWithValue("$o", Ms(incident.OpenedAt));
            cmd.Parameters.AddWithValue("$c", (object?)Ms(incident.ClosedAt) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", status.ToString());
            cmd.Parameters.AddWithValue("$sev", incident.Severity.ToString());
            cmd.Parameters.AddWithValue("$dbs", JsonSerializer.Serialize(incident.Databases.ToArray()));
            cmd.Parameters.AddWithValue("$sig", JsonSerializer.Serialize(incident.Signals.Select(s => s.ToString()).ToArray()));
            cmd.Parameters.AddWithValue("$u", Ms(incident.ClosedAt ?? incident.OpenedAt));
            cmd.ExecuteNonQuery();
        }
    }

    public void AppendEvents(IReadOnlyList<IncidentEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Cmd("INSERT INTO event (incident_id, at, kind, severity, detail) VALUES ($i, $at, $k, $s, $d)");
            var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
            var pat = cmd.Parameters.Add("$at", SqliteType.Integer);
            var pk = cmd.Parameters.Add("$k", SqliteType.Text);
            var ps = cmd.Parameters.Add("$s", SqliteType.Text);
            var pd = cmd.Parameters.Add("$d", SqliteType.Text);
            foreach (var e in events)
            {
                // Events outside an incident (id 0), e.g. gaps while healthy, are kept with a NULL incident.
                pi.Value = e.IncidentId > 0 && IncidentExists(e.IncidentId) ? e.IncidentId : DBNull.Value;
                pat.Value = Ms(e.At);
                pk.Value = e.Kind.ToString();
                ps.Value = e.Severity.ToString();
                pd.Value = e.Detail;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void WriteSamples(long incidentId, SamplePhase phase, IReadOnlyList<ClusterSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return;
        }
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var sampleCmd = Cmd("""
                INSERT INTO sample (incident_id, at, phase, visibility, failure, redacted_rows)
                VALUES ($i, $at, $p, $v, $f, $r)
                ON CONFLICT (incident_id, at) DO NOTHING
                RETURNING id
                """);
            using var sqlCmd = Cmd("INSERT OR IGNORE INTO sql_text (hash, text) VALUES ($h, $t)");
            using var sessionCmd = Cmd("""
                INSERT INTO session VALUES ($sid, $pid, $bs, $db, $u, $app, $cl, $st, $wt, $we, $xs, $qs, $sc, $xid, $xmin, $bp, $h)
                """);

            foreach (var s in samples)
            {
                sampleCmd.Parameters.Clear();
                sampleCmd.Parameters.AddWithValue("$i", incidentId);
                sampleCmd.Parameters.AddWithValue("$at", Ms(s.SampledAt));
                sampleCmd.Parameters.AddWithValue("$p", phase.ToString());
                sampleCmd.Parameters.AddWithValue("$v", s.Visibility.ToString());
                sampleCmd.Parameters.AddWithValue("$f", (object?)s.Failure?.ToString() ?? DBNull.Value);
                sampleCmd.Parameters.AddWithValue("$r", s.Sessions.Count(r => r.IsRedacted));
                var id = sampleCmd.ExecuteScalar();
                if (id is null)
                {
                    continue; // already stored (e.g. pre-incident flush overlapping a live write)
                }
                var sampleId = Convert.ToInt64(id, CultureInfo.InvariantCulture);

                foreach (var r in s.Sessions.Where(ShouldStore))
                {
                    string? hash = null;
                    if (!string.IsNullOrEmpty(r.Query))
                    {
                        hash = Hash(r.Query);
                        sqlCmd.Parameters.Clear();
                        sqlCmd.Parameters.AddWithValue("$h", hash);
                        sqlCmd.Parameters.AddWithValue("$t", r.Query);
                        sqlCmd.ExecuteNonQuery();
                    }

                    sessionCmd.Parameters.Clear();
                    sessionCmd.Parameters.AddWithValue("$sid", sampleId);
                    sessionCmd.Parameters.AddWithValue("$pid", r.Pid);
                    sessionCmd.Parameters.AddWithValue("$bs", Db(Ms(r.BackendStart)));
                    sessionCmd.Parameters.AddWithValue("$db", Db(r.Database));
                    sessionCmd.Parameters.AddWithValue("$u", Db(r.UserName));
                    sessionCmd.Parameters.AddWithValue("$app", Db(r.ApplicationName));
                    sessionCmd.Parameters.AddWithValue("$cl", Db(r.ClientAddress));
                    sessionCmd.Parameters.AddWithValue("$st", Db(r.State));
                    sessionCmd.Parameters.AddWithValue("$wt", Db(r.WaitEventType));
                    sessionCmd.Parameters.AddWithValue("$we", Db(r.WaitEvent));
                    sessionCmd.Parameters.AddWithValue("$xs", Db(Ms(r.TransactionStart)));
                    sessionCmd.Parameters.AddWithValue("$qs", Db(Ms(r.QueryStart)));
                    sessionCmd.Parameters.AddWithValue("$sc", Db(Ms(r.StateChange)));
                    sessionCmd.Parameters.AddWithValue("$xid", Db(r.BackendXid));
                    sessionCmd.Parameters.AddWithValue("$xmin", Db(r.BackendXmin));
                    sessionCmd.Parameters.AddWithValue("$bp", r.BlockingPids.Count == 0 ? DBNull.Value : string.Join(',', r.BlockingPids));
                    sessionCmd.Parameters.AddWithValue("$h", Db(hash));
                    sessionCmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Plain idle sessions hold no transaction and no locks, so they add nothing to the evidence.
    /// Redacted rows are counted on the sample instead of stored.
    /// </summary>
    internal static bool ShouldStore(SessionRow r) =>
        !r.IsRedacted && !(string.Equals(r.State, "idle", StringComparison.Ordinal) && r.TransactionStart is null);

    // ------------------------------------------------------------------ reads

    public IReadOnlyList<StoredIncident> ListIncidents(int limit = 200)
    {
        lock (_gate)
        {
            using var cmd = Cmd("""
                SELECT i.id, i.opened_at, i.closed_at, i.status, i.severity, i.databases,
                       (SELECT COUNT(*) FROM sample s WHERE s.incident_id = i.id)
                FROM incident i ORDER BY i.opened_at DESC LIMIT $n
                """);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<StoredIncident>();
            while (r.Read())
            {
                list.Add(ReadIncident(r));
            }
            return list;
        }
    }

    public StoredIncidentDetail? GetIncident(long id)
    {
        lock (_gate)
        {
            StoredIncident incident;
            using (var cmd = Cmd("""
                SELECT i.id, i.opened_at, i.closed_at, i.status, i.severity, i.databases,
                       (SELECT COUNT(*) FROM sample s WHERE s.incident_id = i.id)
                FROM incident i WHERE i.id = $id
                """))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    return null;
                }
                incident = ReadIncident(r);
            }

            var events = new List<StoredEvent>();
            using (var cmd = Cmd("SELECT incident_id, at, kind, severity, detail FROM event WHERE incident_id = $id ORDER BY at, id"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    events.Add(new StoredEvent(
                        r.IsDBNull(0) ? null : r.GetInt64(0),
                        FromMs(r.GetInt64(1)),
                        Enum.Parse<IncidentEventKind>(r.GetString(2)),
                        Enum.Parse<Severity>(r.GetString(3)),
                        r.GetString(4)));
                }
            }

            var samples = new List<(long Id, DateTimeOffset At, SamplePhase Phase, Visibility Vis, CollectFailureKind? Fail, int Redacted)>();
            using (var cmd = Cmd("SELECT id, at, phase, visibility, failure, redacted_rows FROM sample WHERE incident_id = $id ORDER BY at"))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    samples.Add((r.GetInt64(0), FromMs(r.GetInt64(1)), Enum.Parse<SamplePhase>(r.GetString(2)),
                        Enum.Parse<Visibility>(r.GetString(3)), r.IsDBNull(4) ? null : Enum.Parse<CollectFailureKind>(r.GetString(4)), r.GetInt32(5)));
                }
            }

            var sessions = new Dictionary<long, List<SessionRow>>();
            using (var cmd = Cmd("""
                SELECT se.sample_id, se.pid, se.backend_start, se.db, se.usr, se.app, se.client, se.state, se.wait_type, se.wait_event,
                       se.xact_start, se.query_start, se.state_change, se.backend_xid, se.backend_xmin, se.blocking_pids, q.text
                FROM session se JOIN sample sa ON sa.id = se.sample_id
                LEFT JOIN sql_text q ON q.hash = se.sql_hash
                WHERE sa.incident_id = $id ORDER BY se.sample_id, se.pid
                """))
            {
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var sampleId = r.GetInt64(0);
                    if (!sessions.TryGetValue(sampleId, out var list))
                    {
                        sessions[sampleId] = list = [];
                    }
                    list.Add(new SessionRow
                    {
                        Pid = r.GetInt32(1),
                        BackendStart = OptMs(r, 2),
                        Database = OptStr(r, 3),
                        UserName = OptStr(r, 4),
                        ApplicationName = OptStr(r, 5),
                        ClientAddress = OptStr(r, 6),
                        State = OptStr(r, 7),
                        WaitEventType = OptStr(r, 8),
                        WaitEvent = OptStr(r, 9),
                        TransactionStart = OptMs(r, 10),
                        QueryStart = OptMs(r, 11),
                        StateChange = OptMs(r, 12),
                        BackendXid = OptStr(r, 13),
                        BackendXmin = OptStr(r, 14),
                        BlockingPids = r.IsDBNull(15) ? [] : r.GetString(15).Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToList(),
                        Query = OptStr(r, 16),
                    });
                }
            }

            return new StoredIncidentDetail(
                incident,
                events,
                samples.Select(s => new StoredSample(s.Id, id, s.At, s.Phase, s.Vis, s.Fail, s.Redacted, sessions.GetValueOrDefault(s.Id) ?? [])).ToList());
        }
    }

    public IReadOnlyList<AggregateRow> ReadAggregates(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using var cmd = Cmd("SELECT * FROM aggregate WHERE window_start >= $f AND window_start < $t ORDER BY window_start");
            cmd.Parameters.AddWithValue("$f", Ms(from));
            cmd.Parameters.AddWithValue("$t", Ms(to));
            using var r = cmd.ExecuteReader();
            var list = new List<AggregateRow>();
            while (r.Read())
            {
                list.Add(new AggregateRow
                {
                    WindowStart = FromMs(r.GetInt64(0)),
                    Samples = r.GetInt32(1),
                    FailedSamples = r.GetInt32(2),
                    MaxSessions = r.GetInt32(3),
                    MaxActive = r.GetInt32(4),
                    MaxIdleInTransaction = r.GetInt32(5),
                    MaxLockWaiters = r.GetInt32(6),
                    MaxLockWaitersOverThreshold = r.GetInt32(7),
                    MaxLockWaitSeconds = r.GetDouble(8),
                    MaxTransactionAgeSeconds = r.GetDouble(9),
                    WorstVisibility = Enum.Parse<Visibility>(r.GetString(10)),
                    MaxSessionsByDatabase = JsonSerializer.Deserialize<SortedDictionary<string, int>>(r.GetString(11)) ?? [],
                });
            }
            return list;
        }
    }

    // ------------------------------------------------------------------ maintenance

    public MaintenanceResult RunMaintenance(DateTimeOffset now, StorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            // 1. Expired aggregates, in batches.
            var aggregates = 0;
            using (var cmd = Cmd("DELETE FROM aggregate WHERE window_start IN (SELECT window_start FROM aggregate WHERE window_start < $c LIMIT $n)"))
            {
                cmd.Parameters.AddWithValue("$c", Ms(now - settings.AggregateRetention));
                cmd.Parameters.AddWithValue("$n", settings.DeleteBatchSize);
                int n;
                while ((n = cmd.ExecuteNonQuery()) > 0)
                {
                    aggregates += n;
                }
            }

            // 2. Expired finished incidents (never Active / Recovering).
            var incidents = 0;
            foreach (var id in FinishedIncidentIds(olderThan: now - settings.IncidentRetention))
            {
                DeleteIncident(id);
                incidents++;
            }
            using (var cmd = Cmd("DELETE FROM outbox WHERE status <> 'Pending' AND created_at < $c"))
            {
                cmd.Parameters.AddWithValue("$c", Ms(now - settings.IncidentRetention));
                cmd.ExecuteNonQuery();
            }
            using (var cmd = Cmd("DELETE FROM long_tx WHERE ended_at IS NOT NULL AND ended_at < $c"))
            {
                cmd.Parameters.AddWithValue("$c", Ms(now - settings.IncidentRetention));
                cmd.ExecuteNonQuery();
            }
            DeleteOrphanSql();
            Compact();

            // 3. Size cap: oldest finished incidents first, then thin open incidents, then report.
            var thinned = 0;
            var overCap = false;
            if (MeasureUnlocked().TotalBytes > settings.MaxBytes)
            {
                foreach (var id in FinishedIncidentIds(olderThan: null))
                {
                    DeleteIncident(id);
                    incidents++;
                    DeleteOrphanSql();
                    Compact();
                    if (MeasureUnlocked().TotalBytes <= settings.MaxBytes)
                    {
                        break;
                    }
                }

                if (MeasureUnlocked().TotalBytes > settings.MaxBytes)
                {
                    // Keep every 5th sample of open incidents (and all pre-incident samples, the hardest to get back).
                    using var cmd = Cmd("""
                        DELETE FROM sample WHERE phase <> 'Pre' AND (id % 5) <> 0
                          AND incident_id IN (SELECT id FROM incident WHERE status IN ('Active', 'Recovering'))
                        """);
                    thinned = cmd.ExecuteNonQuery();
                    DeleteOrphanSql();
                    Compact();
                    overCap = MeasureUnlocked().TotalBytes > settings.MaxBytes;
                }
            }

            return new MaintenanceResult(aggregates, incidents, thinned, overCap, MeasureUnlocked());
        }
    }

    public StorageReport Report()
    {
        lock (_gate)
        {
            return MeasureUnlocked();
        }
    }

    private StorageReport MeasureUnlocked()
    {
        long Size(string p) => File.Exists(p) ? new FileInfo(p).Length : 0;
        int Count(string sql) => Convert.ToInt32(Scalar(sql), CultureInfo.InvariantCulture);
        return new StorageReport(
            Size(_path),
            Size(_path + "-wal"),
            Count("SELECT COUNT(*) FROM incident"),
            Count("SELECT COUNT(*) FROM incident WHERE status IN ('Active', 'Recovering')"),
            Count("SELECT COUNT(*) FROM aggregate"),
            Count("SELECT COUNT(*) FROM sample"));
    }

    private List<long> FinishedIncidentIds(DateTimeOffset? olderThan)
    {
        using var cmd = Cmd("""
            SELECT id FROM incident
            WHERE status IN ('Closed', 'Interrupted') AND ($c IS NULL OR updated_at < $c)
            ORDER BY opened_at
            """);
        cmd.Parameters.AddWithValue("$c", olderThan is null ? DBNull.Value : Ms(olderThan.Value));
        using var r = cmd.ExecuteReader();
        var ids = new List<long>();
        while (r.Read())
        {
            ids.Add(r.GetInt64(0));
        }
        return ids;
    }

    private void DeleteIncident(long id)
    {
        using var cmd = Cmd("DELETE FROM incident WHERE id = $id"); // cascades to events, samples, sessions
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void DeleteOrphanSql() =>
        Exec("""
            DELETE FROM sql_text
            WHERE NOT EXISTS (SELECT 1 FROM session s WHERE s.sql_hash = sql_text.hash)
              AND NOT EXISTS (SELECT 1 FROM long_tx l WHERE l.sql_hash = sql_text.hash)
            """);

    /// <summary>Gives freed pages back to the OS and truncates the WAL.</summary>
    private void Compact()
    {
        Exec("PRAGMA incremental_vacuum");
        Exec("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    // ------------------------------------------------------------------ helpers

    private bool IncidentExists(long id)
    {
        using var cmd = Cmd("SELECT 1 FROM incident WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static StoredIncident ReadIncident(SqliteDataReader r) => new(
        r.GetInt64(0),
        FromMs(r.GetInt64(1)),
        r.IsDBNull(2) ? null : FromMs(r.GetInt64(2)),
        Enum.Parse<IncidentStatus>(r.GetString(3)),
        Enum.Parse<Severity>(r.GetString(4)),
        JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [],
        r.GetInt32(6));

    private SqliteCommand Cmd(string sql)
    {
        var cmd = _db.CreateCommand();
#pragma warning disable CA2100 // All SQL in this class is constant text; values always go through parameters.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Cmd(sql);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = Cmd(sql);
        return cmd.ExecuteScalar();
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private static long? Ms(DateTimeOffset? t) => t?.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string? OptStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTimeOffset? OptMs(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : FromMs(r.GetInt64(i));

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
