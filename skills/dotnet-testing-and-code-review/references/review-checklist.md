# Code review checklist (constraint-first)

Mark each item Pass / Fail / N/A with a one-line note. Any Fail in sections 1-4 blocks the change.

## 1. Project constraints
- [ ] Only SELECT/SHOW statements; new SQL added to `SqlCatalog` and the guard test.
- [ ] No new DB user, role, privilege or extension requirement; limited-visibility path handled.
- [ ] No Docker, Kubernetes, cloud SDK, web server, listening port, Windows Service or extra external service.
- [ ] No outbound traffic except configured PostgreSQL and SMTP hosts; no telemetry/update checks.
- [ ] SQLite via Microsoft.Data.Sqlite only; no new persistence tech.

## 2. Secrets and privacy
- [ ] No credential in code, config, SQLite, logs, exceptions, UI, tests, fixtures.
- [ ] Secrets read from Credential Manager at point of use; session-only mode never persists.
- [ ] No `ToString()`/logging of connection string builders, data sources or SMTP clients.
- [ ] Exceptions shown to user/logged are sanitized (no `PostgresException.Detail/Where`).
- [ ] SQL text handling follows `QueryTextMode`; emails masked and truncated.
- [ ] Fixtures/screenshots anonymized.

## 3. Resource bounds
- [ ] Every queue, cache, list, dictionary and string pool has an explicit capacity and eviction.
- [ ] Ring buffer bounded by age **and** bytes; row caps and `LIMIT` on queries.
- [ ] No unbounded `SELECT`, no `query` column without `left(...)`.
- [ ] Healthy mode writes only aggregates; incident mode writes through bounded channel with documented degradation.
- [ ] Retention covers any new table; open incidents protected; size cap honored.
- [ ] At most 2 PostgreSQL connections; statement/lock timeouts set; no tick overlap.

## 4. Correctness of diagnosis
- [ ] Lock and LWLock handled separately; `pg_blocking_pids` only for `Lock` waiters.
- [ ] Transaction age and query age kept separate; idle query age not reported as running time.
- [ ] Session identity uses `(pid, backend_start)`; transaction identity includes `xact_start`.
- [ ] Cumulative counters converted to deltas with reset handling.
- [ ] Observed / Derived / Suspected labels preserved; no root-cause claims; confidence rules respected.
- [ ] Gaps, failures and invisibility never interpreted as healthy/recovered.
- [ ] Version/capability differences degrade a feature, not the loop.

## 5. .NET quality
- [ ] Async end-to-end with `CancellationToken`; no `.Result`/`.Wait()`/`async void` (except UI handlers).
- [ ] `TimeProvider` used; UTC stored; no `DateTime.Now/UtcNow` in Core.
- [ ] Culture-invariant parsing/formatting/casing for identifiers and storage (Turkish locale safe).
- [ ] `IDisposable`/`IAsyncDisposable` correct for connections, commands, readers, channels, timers.
- [ ] Hot path allocation-conscious (no LINQ/regex/string concat per row without need).
- [ ] UI thread not blocked; grid virtualized and capped; UI reads immutable snapshots.
- [ ] Failure of SMTP, SQLite or PostgreSQL does not stop sampling or crash the app.
- [ ] Parameterized SQL on both PostgreSQL and SQLite; no string-built SQL.

## 6. Tests
- [ ] New behavior covered by unit or scenario-replay tests; bug fixes have a regression test.
- [ ] Tests deterministic (fake clock), no real network, no real Credential Manager, no production DB.
- [ ] Storage tests clean temp files and pools.
- [ ] Analyzer warnings: none suppressed without a justification comment.

## 7. UX and operability
- [ ] Visibility level and capability limits visible in UI/email.
- [ ] Storage view reflects new data; settings validated with clear messages.
- [ ] Logs are useful, bounded, and free of SQL literals/secrets.
