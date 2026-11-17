# TEST_PLAN.md - How we test without the company database

Status: **v2 (2026-11-17).** Tests run with `dotnet test` on this machine. Only results that were actually run are reported.

## 1. Rules

- Test data is fictional: databases `app_orchid_db`, `app_lotus_db`, `app_cedar_db`; users `svc_*`; IPs from `192.0.2.0/24`
  and `198.51.100.0/24`; e-mail domain `example.test`; invented SQL with `$1` parameters.
- The only password in tests is the canary `CANARY-pw-123`.
- Tests use temp folders and never the real Credential Manager, settings or network. The one Credential Manager round-trip
  test is opt-in (`PGMONITOR_WINCRED_TEST=1`).
- No live PostgreSQL tests in the default run.

## 2. Simulator scenarios (deterministic: seed + virtual time)

| ID | Scenario | Expected |
|---|---|---|
| S01 | Healthy 24 h (random churn, short transactions, occasional single Lock wait) | 0 incidents, 0 mails, only aggregate rows |
| S02 | Single-sample Subtrans blip | 0 incidents |
| S03 | Subtrans incident: long transaction in app A, waiters spread to B and C, DBA terminates sessions, recovery | 1 incident, opened → escalated → recovering → closed; app A's long transaction is suspect #1; terminated sessions marked "disappeared"; 2 mails |
| S04 | Flapping around the threshold for 30 min | 1 incident (reopened events), mails ≤ cooldown limit |
| S05 | Lock chain: idle-in-transaction root blocker, 8 waiters in 2 levels | 1 incident, root blocker identified, chain depth 2 |
| S06 | Partial visibility (other users' rows redacted) | visibility = Partial, never "healthy" |
| S07 | Disconnect + laptop sleep gap during an incident | gap events, no recovery during gap, recovery only after fresh clear samples |
| S08 | `stats_reset` change, postmaster restart, PID reuse with new `backend_start` | no rate spike, baseline reset event, sessions not merged |

## 3. Unit tests (Core)

- RingBuffer: capacity by count and by bytes, order, snapshot copy.
- SlruRateTracker: first sample has no rate; normal delta; `stats_reset` change → re-baseline; counter decrease →
  re-baseline; restart → re-baseline.
- IncidentEngine:
  - each trigger at below, equal and above its threshold;
  - 2-of-3 rule;
  - severity never decreases;
  - one incident for a continuous trigger;
  - new database → scope event;
  - recovery timing;
  - reopen window;
  - gap freezes state;
  - Partial visibility.
- SuspectRanker: oldest xid holder ranked above waiters; root blocker detection with duplicates and pid 0 in `blocking_pids`;
  "Suspected" label always present.
- Simulator self-checks: same seed → same samples; no `xact_start` in the future; idle sessions have no wait;
  unique `(pid, backend_start)`.

## 4. Infrastructure tests

- **SQL guard**: every statement in `SqlCatalog` starts with `SELECT` or `SHOW`. None contains a write, DDL or admin keyword:
  `INSERT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `GRANT`, `COPY`, `DO`, `CALL`, `SET `, `pg_terminate`,
  `pg_cancel`, `pg_reload`, `set_config`, `pg_stat_reset`. Reflection ensures every catalog constant is covered.
- **Connection string**: always contains `default_transaction_read_only=on` and a command timeout. `ToString()` and log
  output never contain the password.
- **Row mapper**: `DataTable.CreateDataReader()` with NULLs everywhere, `<insufficient privilege>`, 1024-byte cut SQL and
  Turkish characters.
- **SQLite store** on temp files:
  - healthy run → only aggregates;
  - incident → pre-incident flush + detail;
  - reopen file → data readable, open incident becomes Interrupted;
  - retention keeps open incidents;
  - size cap deletes the oldest closed incident first.
- **Mail**: subject/body built from S03 contain db/app/suspects, SQL is truncated to the limit, and the canary never appears.
  The outbox retries after a failing sender and the loop keeps sampling.
- **Canary**: after S03 with a failing sender and a failing source, `CANARY-pw-123` is absent from the log folder, SQLite bytes
  and `.eml` files.

## 5. End-to-end (demo mode)

Run S03 through the real `MonitorLoop`, SQLite temp file and file mail sender with virtual time. Assert the incident, its
events, the stored sample count and the 2 `.eml` files.

## 6. Manual checks on this PC (WPF)

Start in demo mode and check:

- the SIMULATED banner;
- the dashboard numbers move;
- an incident appears with its timeline and suspects;
- the tray icon works;
- closing to tray keeps monitoring.

## 7. Company verification (user only, on the company laptop)

| # | Check | Pass |
|---|---|---|
| C1 | Save the password in Credential Manager; search the data folder for it | not found |
| C2 | Connect | version shows 15.x; visibility Full/Partial is shown |
| C3 | Read-only | the connection test shows `default_transaction_read_only = on` |
| C4 | Query cost | the "Check connection" screen shows the timing of each query; p95 well below 1 s |
| C5 | Real column types | no mapper errors over one day |
| C6 | Healthy day | no false incidents; tune thresholds from the stored aggregates |
| C7 | Test mail | arrives at the recipients |
| C8 | Sleep/VPN drop | gap recorded, no false recovery |
| C9 | Real incident | evidence present after the DB team cleans up sessions |
