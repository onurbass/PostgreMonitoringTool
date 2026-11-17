---
name: postgres-diagnostics
description: Read-only PostgreSQL diagnostics for Subtransaction (SubtransSLRU / SubtransBuffer) contention, Lock vs LWLock waits, long transactions and blocking in a shared multi-database cluster. Use when writing, reviewing or interpreting monitoring SQL over pg_stat_activity, pg_stat_slru, pg_blocking_pids or pg_stat_get_backend_subxact, or when handling PostgreSQL version and privilege differences.
compatibility: PostgreSQL 13-18 (capability-probed at runtime). Assumes an ordinary login role; never assumes superuser, pg_monitor or pg_read_all_stats.
---

# postgres-diagnostics

Owns: **which queries to run and what the results mean.** Does not own scheduling/storage (lightweight-monitoring-architecture), incident decisions (incident-analysis-and-alerting) or C# code (dotnet-windows-development). Ready-to-use queries: `references/queries.md`.

## Hard rules

1. **SELECT / SHOW only.** Never `pg_terminate_backend`, `pg_cancel_backend`, `pg_stat_reset*`, DDL, `CREATE EXTENSION`, or any `SET` that is not a session-local timeout. Monitoring observes; the DB team acts.
2. **Existing privileges are the source of truth.** Do not require `pg_monitor` / `pg_read_all_stats`. Detect what is visible and degrade gracefully.
3. **Probe, don't assume.** At connect: read `server_version_num`, then probe each optional source (`pg_stat_slru`, `pg_stat_get_backend_subxact`, `query_id`) with its own autocommit statement. Cache results as capability flags; a failed probe disables only that feature, never the sample loop.
4. **Cheap by construction:** one connection, 1-3 statements per tick, no `SELECT *`, no unbounded text (`left(query, N)`), `LIMIT` on session lists, `statement_timeout` on the session.
5. **No dependency on `pg_stat_statements`** or other extensions; use only if already present and readable, as optional enrichment.

## Visibility probe (do this first, report it in the UI)

Without `pg_read_all_stats`, rows of *other roles'* sessions show NULL in `state`, `wait_event*`, `xact_start`, etc., and `query = '<insufficient privilege>'`. A monitor that counts only visible waits would report "healthy" while blind.

- Count `client backend` rows where `state IS NULL` -> `invisible_sessions`.
- Visibility levels: **Full** (0 invisible), **Partial** (some), **Own-only** (nearly all). Persist it per sample/incident.
- Partial/Own-only: show a persistent banner, and never present zero waits as "healthy"; present as "no waits observed among visible sessions". Cap suspect confidence (see incident-analysis-and-alerting).
- `pg_stat_slru` and `pg_settings` are generally readable by any role and still give cluster-wide Subtrans signal under limited visibility.

## Wait-event semantics

| Observation | Meaning | Right tool |
|---|---|---|
| `Lock` (`transactionid`, `relation`, `tuple`...) | Heavyweight lock wait; PG knows the blocker | `pg_blocking_pids()` |
| `LWLock` `Subtrans*` | Contention/I/O on the sub-transaction SLRU cache (`pg_subtrans`). **No blocker is recorded** | correlate over samples |
| `LWLock` other | Internal shared-memory contention | count and trend only |
| `Client` / `ClientRead` + `idle in transaction` | App holds a transaction open, doing nothing | age via `state_change` |
| `IO`, `IPC`, `Timeout` | Informational | count |

Never treat Lock and LWLock as equivalent, and never call `pg_blocking_pids` expecting it to explain Subtrans waits (it returns an empty array).

### Why Subtrans stalls cross applications

Each backend caches up to 64 subtransaction XIDs. Savepoints, PL/pgSQL `EXCEPTION` blocks and JDBC `autosave` create subtransactions; a transaction exceeding 64 **overflows**, and every other backend that needs visibility of its rows must then consult `pg_subtrans` through a **cluster-wide shared SLRU**. A long transaction in one database can therefore slow sessions in *all* databases. Waiters are victims, not necessarily culprits.

## Version notes (the runtime probe overrides this table)

| Feature | Availability |
|---|---|
| `backend_type` | 10+ |
| `pg_stat_slru` | 13+; Subtrans row is named `Subtrans` (13-16) or `subtransaction` (17+) -> match `name ILIKE 'sub%'` |
| Subtrans wait names | `SubtransControlLock` (<=15), `SubtransSLRU` (16+), `SubtransBuffer` (13+); older `subtrans`. Match `wait_event ILIKE 'subtrans%'` plus a configurable list |
| `query_id` in pg_stat_activity | 14+, needs `compute_query_id` |
| `stats_fetch_consistency` | 15+ |
| `pg_stat_get_backend_subxact(pid)` -> `subxact_count`, `subxact_overflow` | 17+; may return no row/NULL for other roles' backends |
| `subtransaction_buffers` GUC, `pg_wait_events` view | 17+ |

On PG <17, per-session overflow is **unobservable**; rely on wait events, `pg_stat_slru` deltas and transaction age. Say so in the UI rather than inferring.

## Time and duration rules

- Capture `now()` **from the server** in the same statement; compute ages server-side as `EXTRACT(EPOCH FROM now() - x)` (double seconds). Never mix client clock with server timestamps.
- **Transaction age** = `now() - xact_start`. **Running query age** = `now() - query_start` *only when `state = 'active'`*. For idle sessions `query_start` is the *previous* query; use `now() - state_change` as time-in-state. Store these as separate fields.
- `idle in transaction (aborted)` also matches `LIKE 'idle in transaction%'`.
- Also capture `age(backend_xmin)`: a large value marks a session pinning the xmin horizon.

## Session and transaction identity

- Session key = `(pid, backend_start)`; PIDs are reused after restarts/terminations.
- Transaction key = `(pid, backend_start, xact_start)`.
- Server restart detection: `pg_postmaster_start_time()` change -> reset all counter baselines and record an event.

## Cumulative counters

`pg_stat_slru` values are cumulative since `stats_reset`. Store the previous sample and compute deltas; if any counter decreases or `stats_reset` changes, discard the delta and re-baseline. Report deltas per second, never raw totals as "activity".

## Application attribution

`application_name` is often just `PostgreSQL JDBC Driver`. Attribute by `datname` first (user-configured db -> app map), then `usename`, `client_addr`, `application_name`. Show both the raw fields and the mapped name.

## Query-writing checklist

- Lock analysis: call `pg_blocking_pids()` **only** for rows with `wait_event_type = 'Lock'` (it briefly takes lock-manager partition locks; calling it for every row, as in ad-hoc queries, adds load).
- Session list: filter `backend_type = 'client backend' AND pid <> pg_backend_pid()`; also ignore the monitor's own `application_name`.
- Exclude pure idle sessions without a transaction from the detail list; they are counted in the per-database summary.
- Text is truncated at `track_activity_query_size` (default 1024); note truncation in the UI.
- Parameterize every value (`$1`); never concatenate identifiers or values into SQL.
- `pg_stat_activity` is a point-in-time view; two statements are not atomic. Tag both with the same tick id and accept ms skew.
