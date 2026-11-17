# TECHNICAL_DECISIONS.md

Each entry has the decision, why, and a source. Dates are when the decision was made.

## D1 - Slim scope: 4 projects instead of 9 (2026-11-17)
**Decision.** Use `Core`, `Infrastructure`, `App` and `Tests`. Drop Replay mode, the separate SimRunner, the Presentation
project, the per-mode databases (only a separate demo file) and the 12-scenario catalog (8 scenarios kept).
**Why.** The product owner asked for a tool that solves the problem without unnecessary complexity. Interfaces keep the same
testability.

## D2 - Target PostgreSQL 15 first; detect capabilities at runtime (2026-11-17)
**Decision.** The company cluster runs PostgreSQL 15. Code is written and tested for 15. On 16+ the tool auto-enables
`pg_stat_get_backend_subxact`. It also matches the `pg_stat_slru` row by both its PG15 name `Subtrans` and its PG17+ name
`subtransaction`.
**Facts.**
- PG15 has the LWLock events `SubtransBuffer` and `SubtransSLRU`. The IO event is `SLRURead` in 15 and `SlruRead` in 17;
  we store the raw string and normalize case-insensitively.
- `pg_stat_get_backend_subxact(backend_id)` exists only from 16. Its argument is the backend ID from
  `pg_stat_get_backend_idset()`, not a PID.
- 15 is supported until 2027-11-11 (latest minor: 15.20).

**Sources.**
- https://www.postgresql.org/docs/15/monitoring-stats.html
- https://www.postgresql.org/docs/17/monitoring-stats.html
- https://www.postgresql.org/docs/17/release-17.html
- https://www.postgresql.org/support/versioning/

## D3 - pg_blocking_pids only for Lock waiters (2026-11-17)
**Decision.** Evaluate it inside the activity query only `WHEN wait_event_type = 'Lock'`. De-duplicate the result. Treat
`0` as "prepared transaction".
**Why.** The docs warn that frequent calls take the lock manager's shared state exclusively for a short time. They also note
duplicates under parallel query and `0` for prepared transactions.
**Source.** https://www.postgresql.org/docs/15/functions-info.html

## D4 - Read-only enforced at connection level (2026-11-17)
**Decision.**
- Npgsql connection string `Options=-c default_transaction_read_only=on`.
- After connect, `SHOW default_transaction_read_only` must return `on`, otherwise refuse to monitor.
- Every statement is a constant in `SqlCatalog`, guarded by a test.
- There is no free-SQL feature.

**Why.** Starting a statement with `SELECT`/`WITH` alone is not a guarantee, because a `WITH` clause can contain
data-modifying statements. A read-only transaction rejects writes server-side.

**Sources.**
- https://www.npgsql.org/doc/connection-string-parameters.html
- https://www.postgresql.org/docs/15/runtime-config-client.html
- https://www.postgresql.org/docs/15/queries-with.html

## D5 - Session identity is (pid, backend_start) (2026-11-17)
**Why.** PIDs are reused by the OS. `backend_start` changes for a new backend.

## D6 - Visibility classification (2026-11-17)
**Decision.**
- **Full**: `rolsuper` or a member of `pg_read_all_stats`.
- **Partial**: otherwise, if any row is redacted (`query = '<insufficient privilege>'` or NULL state for other users).

We report visible and redacted row counts and never guess hidden ones.

**Source.** https://www.postgresql.org/docs/15/monitoring-stats.html (other users' rows show many NULL columns unless the
caller is a superuser or a member of `pg_read_all_stats`).

## D7 - Cumulative SLRU counters → rates with re-baseline (2026-11-17)
**Decision.** A rate is computed only between two samples that have the same `stats_reset`, the same
`pg_postmaster_start_time()`, the same server address, and non-decreasing counters. Otherwise the sample becomes the new
baseline and an event is recorded. The tool never calls `pg_stat_reset*`.

## D8 - One cluster incident, separate signals (2026-11-17)
**Decision.** Subtrans, Lock and other-LWLock are evaluated separately. A single open incident collects all signals that fire
while it is open, with the order in which they fired.
**Why.** The cluster is the shared resource, and the real screenshot shows Subtrans and Lock waits together in one event. Two
parallel incidents would produce two mails for one outage. The signals stay separate inside the incident, so they are not
reduced to one number.

## D9 - SQL text in e-mail (2026-11-17)
**Decision.** Include the SQL of the top suspects, truncated to 500 characters (configurable, can be set to 0). This is the
product owner's decision. SQL is stored in SQLite only for incident samples and is never written to log files.

## D10 - .NET 10 LTS with 10.0.x Microsoft packages (2026-11-17)
**Decision.** Use net10.0 / net10.0-windows and pin the Microsoft.* packages to 10.0.15.
**Why.** .NET 11 (Nov 2026) is STS. 11.x packages exist, but staying on the LTS line avoids pulling newer runtime
dependencies.
**Source.** https://dotnet.microsoft.com/platform/support/policy/dotnet-core

## D11 - Soft storage cap (2026-11-17)
**Decision.** Check `db + wal` size after writes, checkpoint the WAL, delete the oldest closed incidents, and never delete
open ones (thin them out instead).
**Why.** SQLite with WAL cannot guarantee a byte-exact hard cap.

## D13 - Lock detection first, Subtrans second (2026-11-17)
**Decision.** M1-M8 deliver lock detection end to end: sampling, chains, root blocker, incident, storage, mail, UI, guide.
Subtrans signals come in M9. The engine keeps signal families separate, so adding Subtrans later does not change the lock logic.
**Why.** Product owner priority.
**Lock wait time.** We use `now() - query_start` for a session whose `wait_event_type = 'Lock'`. This is the time since the
statement started, which is an upper bound of the lock wait. PG15 has no per-lock wait start in `pg_stat_activity`. The
`pg_locks.waitstart` column (PG14+) gives the exact time, but it requires a join on `pg_locks`, so it is deferred.
**Source.** https://www.postgresql.org/docs/15/view-pg-locks.html

## D14 - SQLite layout (2026-11-17)
**Decision.**
- Tables: `aggregate`, `incident`, `event`, `sample`, `session`, `sql_text`.
- Times are stored as Unix milliseconds (UTC). Enums are stored by name, so the file is readable in any SQLite browser.
- `PRAGMA auto_vacuum = INCREMENTAL` is set at creation, so retention deletes give space back. After deletes we run
  `incremental_vacuum` + `wal_checkpoint(TRUNCATE)`.
- Only sessions with a transaction or a non-idle state are stored per sample.

**Why.** Space comes back without a full `VACUUM`, and the files are easy to inspect by hand. Idle sessions are most of the
rows but carry no lock evidence.
**Source.** https://www.sqlite.org/pragma.html#pragma_auto_vacuum

## D12 - Laptop operation (2026-11-17)
**Decision.**
- Gaps from sleep or network loss are recorded and never treated as recovery.
- Optionally the app keeps the PC awake on AC power via `SetThreadExecutionState`. This is a process-scoped request; no
  system settings change.
