# Read-only query catalog

All statements are SELECT/SHOW. `$n` are Npgsql parameters. Adapt column lists only by adding, never `SELECT *`. Run each optional query only if its capability flag is true.

## Q1 - Capabilities and identity (once per connect, and after reconnect)

```sql
SELECT current_setting('server_version_num')::int      AS ver,
       current_setting('track_activities')              AS track_activities,
       current_setting('track_activity_query_size')::int AS query_size,
       current_setting('default_transaction_read_only') AS read_only,
       pg_postmaster_start_time()                       AS postmaster_start,
       pg_is_in_recovery()                              AS in_recovery,
       pg_has_role(current_user, 'pg_read_all_stats', 'MEMBER') AS has_read_all_stats;
```
`read_only` must be `on` (set by connection options). If `in_recovery` is true, the node is a standby: warn that counters/behaviour differ.

Optional context (once): `SELECT name, setting FROM pg_settings WHERE name IN ('subtransaction_buffers','shared_buffers','max_connections','idle_in_transaction_session_timeout','compute_query_id');` (missing rows are normal on older versions).

## Q2 - Per-database summary (every tick, normal mode)

```sql
SELECT now()                                   AS server_now,
       coalesce(datname, '(none)')              AS datname,
       count(*)                                 AS sessions,
       count(*) FILTER (WHERE state = 'active') AS active,
       count(*) FILTER (WHERE state LIKE 'idle in transaction%') AS idle_in_tx,
       count(*) FILTER (WHERE wait_event_type = 'Lock')   AS lock_waits,
       count(*) FILTER (WHERE wait_event_type = 'LWLock') AS lwlock_waits,
       count(*) FILTER (WHERE wait_event_type = 'LWLock'
                          AND wait_event ILIKE 'subtrans%') AS subtrans_waits,
       count(*) FILTER (WHERE state IS NULL)    AS invisible,
       max(EXTRACT(EPOCH FROM (now() - xact_start))) AS max_xact_age_s,
       max(EXTRACT(EPOCH FROM (now() - state_change)))
           FILTER (WHERE state LIKE 'idle in transaction%') AS max_idle_in_tx_s
FROM pg_stat_activity
WHERE backend_type = 'client backend' AND pid <> pg_backend_pid()
GROUP BY datname;
```

## Q3 - Relevant session detail (ring buffer tick and incident mode)

```sql
SELECT pid, backend_start, datname, usename, application_name,
       host(client_addr) AS client_ip, state, wait_event_type, wait_event,
       xact_start, query_start, state_change,
       EXTRACT(EPOCH FROM (now() - xact_start))   AS xact_age_s,
       EXTRACT(EPOCH FROM (now() - state_change)) AS state_age_s,
       CASE WHEN state = 'active'
            THEN EXTRACT(EPOCH FROM (now() - query_start)) END AS running_query_age_s,
       age(backend_xmin)                          AS xmin_age,
       left(query, $1)                            AS query
FROM pg_stat_activity
WHERE backend_type = 'client backend'
  AND pid <> pg_backend_pid()
  AND application_name <> $2
  AND (xact_start IS NOT NULL OR state IS DISTINCT FROM 'idle')
ORDER BY xact_start ASC NULLS LAST
LIMIT $3;
```
`$1` snippet length (e.g. 512 buffered / 4096 in incident), `$2` the monitor's own application name, `$3` row cap (e.g. 500). `state IS NULL` rows are kept so invisibility stays measurable.

## Q4 - Lock blocking (only when lock_waits > 0)

```sql
SELECT w.pid AS blocked_pid, b.blocker_pid
FROM pg_stat_activity w
CROSS JOIN LATERAL unnest(pg_blocking_pids(w.pid)) AS b(blocker_pid)
WHERE w.wait_event_type = 'Lock' AND w.backend_type = 'client backend';
```
If blockers are not in the Q3 result, fetch them with Q3's column list and `WHERE pid = ANY($1)`. Record chains (blocker may itself be blocked) and the root blocker(s).

## Q5 - Subtrans SLRU counters (every tick or every N ticks; cumulative)

```sql
SELECT name, blks_zeroed, blks_hit, blks_read, blks_written, blks_exists,
       flushes, truncates, stats_reset
FROM pg_stat_slru
WHERE name ILIKE 'sub%';
```
Compute per-second deltas; re-baseline on decrease or `stats_reset` change. `blks_read` growth means SLRU pages are being read from disk (cache too small / contention) - a stronger signal than `blks_hit`.

## Q6 - Per-backend subtransaction state (PG 17+, normal mode: skip; incident mode: run)

```sql
SELECT a.pid, s.subxact_count, s.subxact_overflow
FROM pg_stat_activity a
CROSS JOIN LATERAL pg_stat_get_backend_subxact(a.pid) AS s
WHERE a.backend_type = 'client backend' AND a.xact_start IS NOT NULL
  AND a.pid <> pg_backend_pid();
```
Zero rows or NULLs = "unavailable", not "no subtransactions". Join to Q3 by `pid` within the same tick.

## Notes on the manual queries in `sql.txt`

- Reuse their *intent*, not their shape: `split_part(...::text)` durations and `ILIKE` query-type classification are display conveniences; the tool stores numeric seconds and classifies the statement type in C# (ordinal, culture-invariant).
- The blocker join runs `pg_blocking_pids()` for every row; Q4 restricts it to `Lock` waiters.
- `ORDER BY ... DESC` on a text alias in the third query orders by the alias value; Q3 orders by `xact_start` to surface the oldest transactions first.
