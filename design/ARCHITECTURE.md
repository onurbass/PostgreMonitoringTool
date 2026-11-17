# ARCHITECTURE.md - PostgreSQL Lock & Subtrans Monitor

Status: **v2, slim scope (2026-11-17).** Replaces the first draft. Decisions and sources: [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.md).

## 1. What the tool is for

Several Mendix applications share one PostgreSQL **15** cluster. Queries get stuck behind row/table locks
(`Lock:transactionid`, `Lock:tuple`, `Lock:relation` ...), and the whole cluster can slow down. Sometimes a long transaction
also overflows its subtransaction cache, and sessions then wait on `LWLock:SubtransSLRU` / `SubtransBuffer`. The DB team fixes
it by terminating sessions, and the evidence disappears.

**Priority (product owner, 2026-11-17):**
1. **Locked queries first.** Detect sessions blocked on locks, who blocks them, and the root blocker.
2. **Subtrans second.** It is added after lock detection works end to end.

The tool has three jobs:

1. **Keep the evidence.** Sample the cluster continuously. When an incident starts, save the last ~3 minutes plus everything
   until recovery to a local SQLite file.
2. **Alert.** E-mail the user/team once when an incident opens and once when it closes. Include the blocking chain, the root
   blocker and the SQL text.
3. **Let me investigate later.** A small Windows UI shows the live state, the incident list and each incident's timeline.

It runs on the user's company laptop, under the user's existing PostgreSQL account, read-only.

## 2. Hard rules (never change without the user)

1. PostgreSQL access is read-only. Only fixed `SELECT`/`SHOW` statements from one `SqlCatalog` class are allowed. The connection
   sets `default_transaction_read_only=on`.
2. The tool never terminates sessions, cancels queries, changes settings, creates roles or grants privileges.
3. Passwords (PostgreSQL, SMTP) are kept only in Windows Credential Manager, or only in memory for the session. They are never
   written to code, JSON, SQLite, logs or e-mail.
4. Nothing leaves the laptop except the PostgreSQL connection and SMTP e-mails. No telemetry, cloud or auto-update.
5. No real company data (names, hosts, SQL text) in the repository or in test fixtures.

## 3. Structure (4 projects)

```
PgMonitor.sln
  src/PgMonitor.Core            net10.0          models, IncidentEngine, SuspectRanker, RingBuffer, SlruRateTracker,
                                                 settings, ISampleSource, Simulator (no package references)
  src/PgMonitor.Infrastructure  net10.0          PostgresSampleSource + SqlCatalog (Npgsql), SQLite store (Microsoft.Data.Sqlite),
                                                 e-mail (MailKit), Windows Credential Manager (P/Invoke), MonitorLoop
  src/PgMonitor.App             net10.0-windows  WPF: dashboard, incident list/detail, settings, tray icon
  tests/PgMonitor.Tests         net10.0          xUnit v3: engine, simulator scenarios, SQLite, SQL guard, mapper, mail text
```

Why 4 and not 9: one person maintains this and there is one deployment target. The interfaces (`ISampleSource`, `IIncidentStore`,
`IEmailSender`, `ICredentialStore`) give the same testability as separate projects, without the extra build plumbing. `Core` stays
free of packages and I/O, so the important logic is tested with plain unit tests.

## 4. One monitoring cycle

```
MonitorLoop (PeriodicTimer: 5 s healthy, 2 s during an incident; never overlapping)
  -> ISampleSource.CollectAsync()          Simulator (demo/tests) or PostgresSampleSource (live)
       returns ClusterSample, or a failed sample (Timeout | Disconnected | AuthFailed | PermissionDenied | Unknown)
  -> RingBuffer.Add(sample)                last ~3 min, in memory, bounded by count and by total query-text bytes
  -> SlruRateTracker                       converts cumulative pg_stat_slru counters to per-second rates
  -> IncidentEngine.Process(state, sample, settings) -> (newState, events)     pure, no I/O, no clock
  -> store: healthy -> 1 aggregate row per 60 s; incident -> flush ring buffer once, then every sample in detail
  -> mail: on Opened / Escalated (optional) / Closed events, via a simple SQLite outbox with retry
  -> UI: latest snapshot + state
```

A failed or blind sample is never "healthy". During a gap the engine freezes the incident state. Recovery requires fresh,
successful, clear samples.

## 5. What we collect (PostgreSQL 15.6 facts, verified)

| Source | Use | Note |
|---|---|---|
| `pg_stat_activity` (client backends) | Sessions, states, waits, `xact_start`, `query_start`, `state_change`, `backend_xid`, `backend_xmin`, `query` | `query` is cut at `track_activity_query_size` (default 1024 bytes) |
| `pg_blocking_pids(pid)` | Lock chains | Called **only** for rows with `wait_event_type = 'Lock'` (it takes the lock-manager lock briefly). `0` = prepared transaction. Duplicate PIDs are possible with parallel query, so de-duplicate them |
| `pg_stat_slru` row `Subtrans` | `blks_hit/read/written`, `flushes`, `truncates`, `stats_reset` | Cumulative counters, so we use deltas. PG17+ renamed the row to `subtransaction`; we match both |
| `pg_postmaster_start_time()`, `inet_server_addr()`, `pg_is_in_recovery()`, `server_version_num` | Detect restart or failover | A change resets the SLRU baseline and is recorded as an event |
| `pg_stat_get_backend_subxact()` | Per-session subxact count / overflow | **Not available on 15.x** (added in 16). We probe for it and use it automatically after an upgrade |

Session identity is `(pid, backend_start)`, because PIDs are reused.

### Visibility

Without superuser or `pg_read_all_stats`, other users' rows show NULLs and `<insufficient privilege>`. At connect time we check:

- `rolsuper`
- `pg_has_role('pg_read_all_stats', 'USAGE')`
- the count of redacted rows in each sample

Visibility is shown as Full / Partial / OwnOnly. Partial or OwnOnly is never shown as healthy. Your screenshot shows other
sessions' SQL, so you probably have Full visibility; the probe will confirm it.

## 6. Incident detection (separate signals, one cluster incident)

Each sample computes these signal families separately:

| Signal | Default trigger | Opens an incident? |
|---|---|---|
| **Lock** (primary) | at least 1 session on `wait_event_type='Lock'` waiting `≥ 30 s` (wait time = `now - query_start` of the waiting statement) | yes |
| **Subtrans** (second phase) | `≥ 3` sessions waiting on `LWLock:SubtransSLRU` or `SubtransBuffer` | yes |
| LWLock (other) (second phase) | `≥ 20` sessions waiting on any other LWLock | yes |

Lock severity (counts only waiters over the 30 s threshold, so short normal waits elsewhere never escalate an incident or
mark another database as affected):

| Severity | Condition |
|---|---|
| Warning | any lock wait ≥ 30 s |
| High | ≥ 5 lock waiters, or any lock wait ≥ 2 min |
| Critical | ≥ 20 lock waiters, or lock waiters in ≥ 2 databases |
| Long transaction | any transaction older than 10 min | no, advisory (shown, aggregated, used for suspects) |
| SLRU pressure | `Subtrans` `blks_read`/s far above the 1-hour median | no in v1 (supporting evidence only, until we see real baselines) |
| Multi-database | waiters in `≥ 2` databases | raises severity |

All thresholds are settings.

Rules:

- **Open**: a triggering signal holds in 2 of the last 3 successful samples. A single-sample blip does nothing.
- **One incident per cluster event**: while an incident is open, any new signal or database joins it as an event ("Lock signal
  started", "database X affected"). It does not open a second incident. The incident keeps the signals that fired and the
  order in which they fired.
- **Severity** (Warning < High < Critical) only rises during an incident.
- **Recovery**: all triggers clear for 60 s of successful samples → `Recovering`. Detail capture continues for another
  2 minutes (`PostRecovery`), then the incident is `Closed`. If a trigger returns, the same incident goes back to `Active`.
- **Reopen window**: a trigger within 10 min after close reopens the same incident. This prevents e-mail storms from flapping.
- **Gaps**: disconnect, timeout or laptop sleep. Each is recorded as a gap event and the state is frozen, with no recovery.
  An incident still open when the app stops becomes `Interrupted` at next start.

### Suspects (labelled "Suspected", never "root cause")

On PG15 we cannot see which session overflowed its subtransaction cache, so the ranker uses observable hints:

1. Sessions **not** waiting on Subtrans that hold the oldest transactions with an assigned `backend_xid`, or the oldest
   `backend_xmin`.
2. `idle in transaction` sessions older than the threshold.
3. Root blockers of lock chains, i.e. a blocker that is not itself blocked.
4. The database/application that owns most of the above.

Every row says what was **Observed** (raw values), what was **Derived** (for example "session disappeared between samples:
terminated or finished, not verifiable"), and what is **Suspected**.

## 7. Storage (SQLite, local file)

| Item | Rule |
|---|---|
| File location | `%LOCALAPPDATA%\PgMonitor\monitor.db`; the demo/simulator uses `monitor-demo.db` |
| Mode | WAL, one writer task |
| Healthy | 1 aggregate row per 60 s (counts per signal, sessions per DB, max transaction age, SLRU rates) |
| Incident | Incident row, events, and per-sample session rows; SQL text de-duplicated by hash in its own table. Plain `idle` sessions (no transaction) are not stored: they hold no locks. Redacted rows are only counted |
| Restart | Incidents left Active/Recovering by a previous run become `Interrupted`; new incident ids continue after the stored maximum |
| Retention | Aggregates 30 days, closed incidents 180 days; deleted in batches |
| Size | Soft cap (default 1 GB) on `db + wal` size. Over the cap, delete the oldest **closed** incidents first. Never delete open incidents; if they alone exceed the cap, show a warning and thin out the stored samples (keep every 5th) |

A hard byte-exact cap is not realistic with SQLite and WAL, so the cap is checked after writes and the WAL is checkpointed.

## 8. E-mail

- MailKit over the company SMTP server. Recipients, host and port are in settings; the password is in Credential Manager.
- Mails are sent on Opened, Closed and (optional) Escalated, with a cooldown so there is at most one mail per incident per
  10 min, excluding Closed.
- Content: time, affected databases with the mapped application name, wait counts per signal, top 5 suspects (pid, db, app,
  user, client IP, state, transaction age, wait) **including SQL text truncated to 500 characters (configurable)**. The user
  chose to include SQL.
- Outbox table in SQLite. Retry with backoff up to 24 h. An SMTP failure never stops monitoring.
- Demo mode writes `.eml` files to a folder instead of sending.

## 9. Sensitive data life cycle

| Where | SQL text | Passwords |
|---|---|---|
| Memory (ring buffer) | yes, bounded | only while connecting |
| SQLite | only for incident samples | never |
| Log files | never (only pid/db/counts) | never |
| E-mail | truncated, by user decision | never |
| UI | yes | never |

## 10. Laptop realities

- Sleep, lid close or VPN drop means no data. The tool records the gap and cannot fill it.
- The app can optionally keep the PC awake while it runs on AC power (`SetThreadExecutionState`). This is a per-process request
  and changes no Windows settings.
- The app starts minimized to the tray and can be added to the user's Startup folder by the user.

## 11. Milestones

| # | Content | Done when |
|---|---|---|
| M0 | Solution skeleton, central package versions, build + empty tests | `dotnet build` / `dotnet test` green |
| M1 | Core models, LockGraph (chains, root blockers), IncidentEngine (Lock signal), RingBuffer | engine + graph tests on hand-made samples pass |
| M2 | Simulator + lock scenarios (S01, S02, S04, S05, S06, S07, S08) | scenario tests pass |
| M3 | SQLite store, retention, size cap | store tests on temp files pass |
| M4 | Postgres source + SqlCatalog + read-only check + mapper tests | builds; mapper/guard tests pass (live check at company) |
| M5 | Credential Manager, mail (file + SMTP), outbox, MonitorLoop | end-to-end demo run writes incidents + `.eml` |
| M6 | WPF: dashboard, incidents, incident detail, settings, tray | manual smoke in demo mode |
| M7 | User guide (`docs/USER_GUIDE.md`, Turkish) | the user can install, configure and read an incident from it alone |
| M8 | Company verification: user runs against the real cluster | checklist in TEST_PLAN.md |
| M9 | Subtrans signal, SlruRateTracker, Subtrans suspects, scenario S03 | Subtrans tests pass |

## 12. Out of scope for v1

Replay of recorded real incidents, installers, auto-update, multi-user/server mode, web UI, any database action.
