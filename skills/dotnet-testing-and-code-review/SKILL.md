---
name: dotnet-testing-and-code-review
description: Test strategy and code-review method for the .NET monitor without Docker or a test database - fake clock, recorded-snapshot scenario replay for the incident engine, temp-file SQLite tests, read-only SQL guard, secret-leak canary tests, bounded-memory soak tests, banned-API analyzers and a constraint-based review checklist. Use when writing or reviewing tests, fixtures, analyzers, pull requests or any change touching sampling, persistence, incidents, credentials or email.
compatibility: .NET 10, xUnit (v3 preferred, v2.9 acceptable), TimeProvider + Microsoft.Extensions.TimeProvider.Testing, Microsoft.Data.Sqlite, NSubstitute, Shouldly or AwesomeAssertions. No Docker/Testcontainers. Verify current package versions and licenses before adding.
---

# dotnet-testing-and-code-review

Owns: **proving the other five skills' rules hold.** It does not define behavior; it tests and reviews it. Review checklist: `references/review-checklist.md`.

## Principles

- The real production cluster is **never** a test dependency. Default test runs need only the .NET SDK.
- Test the pure core heavily (incident engine, ring buffer, aggregator, rules), the edges lightly (Npgsql mapping, SMTP, UI).
- Determinism: inject `TimeProvider` (use `FakeTimeProvider`), seed any randomness, no `Thread.Sleep`/real delays.
- Prefer library choices without licensing surprises: assertion library with a permissive license (Shouldly / AwesomeAssertions / plain asserts), NSubstitute for mocks. Check licenses on upgrade.

## Test layers

| Layer | Target | Technique |
|---|---|---|
| Unit | Rules, thresholds, hysteresis, severity, masking, name matching | Table-driven (`[Theory]`), boundary values |
| Scenario replay | `IncidentEngine` end-to-end | JSONL timelines of `Sample`s -> expected events/commands |
| Storage | Repositories, retention, size cap, migrations | Temp-file SQLite per test (WAL needs a file); `ClearAllPools()` on dispose |
| Mapping | Npgsql reader -> `Sample` | Fixture rows through the mapper; NULL/DBNull cases; optional opt-in live smoke |
| Security | Redaction, read-only guard, canary | See below |
| Resilience/soak | Bounds, no leaks, failure containment | Long fake-clock runs, injected failures |

## Scenario replay (most valuable)

Fixture format: one JSON object per tick (server_now, visibility, capability flags, per-db summary, session rows, SLRU counters, blocking pairs). Build fixtures **synthetically** or from anonymized real incidents (replace db names, users, IPs, SQL literals). Maintain at least:

1. Healthy baseline: no incident for 24 h of ticks.
2. Single-blip Subtrans wait: no incident (consecutive-sample rule).
3. Classic Subtrans incident: onset, escalation, recovery, post-collection, close; exactly one open/one resolved notification.
4. Flapping around threshold: one incident, `reopened` events, no mail storm (cooldown).
5. Multi-database spread: severity escalation only upward.
6. Lock chain with root blocker.
7. Session disappears mid-incident; PID reuse with different `backend_start` is a *different* session.
8. Partial visibility: never "healthy", suspect confidence capped.
9. Disconnect/sleep gap during incident: state frozen, no false recovery, gap event recorded.
10. Counter reset (`stats_reset` change / server restart): no negative or huge delta.
11. PG version variants: Subtrans names (`SubtransControlLock`, `SubtransSLRU`), `pg_stat_get_backend_subxact` unavailable.

Assert on events, evidence class (Observed/Derived/Suspected), commands and timestamps; snapshot-test email bodies (after masking).

## Tests that enforce project constraints

- **Read-only guard**: scan every constant in `SqlCatalog` (reflection) -> must start with `SELECT`/`SHOW`/`WITH`, must not contain write/DDL/admin tokens (`pg_terminate_backend`, `pg_cancel_backend`, `set_config`, `COPY`, `DO`, ...). Fail the build on addition of a new statement without a test entry.
- **No-secret canary**: run connection-failure, auth-failure, SMTP-failure and exception paths with password `CANARY-pw-123`; assert absence from captured logs, exception messages, UI strings, outbox, `monitor.db` bytes and settings file.
- **Bounds**: feed 100x expected load (huge SQL text, thousands of sessions) and assert ring buffer bytes <= cap, channel depth <= capacity, row cap respected, SQLite size <= `max_page_count`, open incident never pruned.
- **Healthy-mode persistence**: after N healthy ticks, `incident_session_obs` and `sql_text` row counts are zero.
- **Retention**: expired data removed in batches; open incident preserved; cascade removes children; orphan SQL text cleaned.
- **Culture**: run key suites under `tr-TR` (`CultureInfo.CurrentCulture` set in a fixture) to catch dotless-i and decimal-separator bugs.
- **Egress**: no `HttpClient`/`WebClient`/`Socket` usage outside the Postgres and SMTP projects (analyzer or architecture test).

## Analyzers and build gates

- `Nullable` enabled, `TreatWarningsAsErrors`, `AnalysisLevel` latest-recommended, `EnforceCodeStyleInBuild`.
- Enable CA1304/1305/1309/1311 (culture), CA2007 where applicable in libraries, CA2016 (pass CancellationToken), CA1849/VSTHRD-style async checks, CA5xxx security rules.
- `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `BannedSymbols.txt`: ban `DateTime.Now/UtcNow`, `DateTimeOffset.UtcNow` (outside the clock adapter), `Thread.Sleep`, `Task.Result/Wait`, `Console.*`, `NpgsqlConnectionStringBuilder.ToString`, `HttpClient` (outside allowed projects), `Process.Start`.
- CI-equivalent local script: restore (locked) -> build -> test -> `dotnet list package --vulnerable --include-transitive` -> format check.

## Performance and soak

- Benchmark only the hot path (sample -> buffer -> aggregator) with BenchmarkDotNet or a simple stopwatch harness: target well under 5 ms per tick for 500 sessions, near-zero allocations in steady state.
- Soak: 24 h of simulated ticks (fake clock, no sleeping) with periodic incidents; assert working set growth is flat after warm-up (`GC.GetGCMemoryInfo`, forced collection between checkpoints) and SQLite size plateaus under retention.

## Live checks (user-run, opt-in)

Integration tests against a real PostgreSQL are **excluded by default** (`[Trait("Category","Live")]`, enabled only by an explicit environment flag) and use the user's own credential via Credential Manager. They execute only catalog queries, assert shape/permissions-handling, and are never run by an agent or in automation.

## Reviewing a change

Use `references/review-checklist.md`. Reviews are constraint-first: any violation of read-only, secrets, bounded resources, no-egress or no-new-services blocks merging regardless of feature value. Request a failing test for every bug fix.
