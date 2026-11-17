---
name: lightweight-monitoring-architecture
description: Design the tiered, storage-efficient sampling pipeline - normal vs incident mode, bounded in-memory circular buffer, compact SQLite aggregates, incident-triggered detail persistence, single-writer SQLite with WAL, retention, hard size cap, DB-load budget and monitor-blind gap handling. Use when designing or changing sampling cadence, buffering, persistence, schema or retention.
compatibility: Microsoft.Data.Sqlite 10.x (bundled SQLite, WAL), .NET 10. SQLite is the only persistence technology; no external stores, queues or services.
---

# lightweight-monitoring-architecture

Owns: **how samples flow from PostgreSQL to memory to disk, and how much each stage may cost.** Query content -> postgres-diagnostics. Trigger/lifecycle rules -> incident-analysis-and-alerting. Host/DI plumbing -> dotnet-windows-development. Schema detail: `references/sqlite-schema.md`.

## Pipeline

```
PeriodicTimer -> Collector (1-3 read-only queries) -> Sample
   -> RingBuffer (memory, bounded)  ----------------------+
   -> Aggregator (window max/avg) -> SQLite every 30-60 s |  flush on incident open
   -> IncidentEngine (pure, consumes Sample)  <-----------+
          | open/active -> DetailWriter (bounded Channel) -> SQLite
```
Each stage is an interface in `Core`; the engine never touches IO.

## Budgets (defaults, all configurable, all enforced in code)

| Resource | Normal mode | Incident mode |
|---|---|---|
| Poll interval | 5 s | 2 s (floor 1 s; never lower) |
| DB statements per tick | Q2 + Q3 (+Q5) | + Q4 when lock waits, + Q6 if supported |
| DB connections | 2 max | 2 max |
| Statement timeout | 5 s | 5 s |
| Ring buffer | ~3 min (36 ticks @5 s), **byte cap 64 MB**, row cap 500/tick | same buffer keeps filling |
| SQL snippet in buffer | 512 chars | 4096 chars |
| SQLite writes | 1 aggregate row set / 30-60 s | batched detail every tick |
| App working set target | < 150 MB | < 250 MB |

If a tick's collection exceeds ~50% of the interval, log once, widen the interval (adaptive back-off up to 4x), and show "monitor under pressure". Observability must never hurt the observed system.

## Sampling loop rules

- `PeriodicTimer` + one tick in flight: **if the previous tick is still running, skip, don't queue.** Count skipped ticks.
- Stamp each sample with server `now()` (from Q2), client monotonic time, tick id, capability flags, visibility level, collection duration.
- Collector failures produce a `SampleFailed` marker (not an exception escape). N consecutive failures -> state `Disconnected`; the engine treats this as **unknown**, not healthy or recovered.
- Gaps (sleep/resume, disconnect, skipped ticks > 2x interval) are written as `monitor_gap` events and inserted into the ring buffer as explicit gap markers so timelines show them.

## Ring buffer

- Fixed-capacity circular structure of immutable `Sample` objects holding session rows; eviction by **age (retention window) and bytes**, whichever first.
- Intern repeated strings (database, user, app, wait event, IP) in a shared `StringPool`; store SQL text once per hash (`ulong` xxHash/FNV of normalized text) in a `SqlTextPool` with refcount eviction. Repeating statements then cost 8 bytes per row.
- Estimate size cheaply per sample (rows x fixed overhead + new strings); do not use `GC.GetTotalMemory` in the hot path.
- Reads for UI/flush take a snapshot (copy of array of references), no locks held while serializing.
- Healthy state persists **no** session rows and **no** SQL text to disk.

## Normal-mode persistence (aggregates)

- Keep an in-memory window accumulator: per db and cluster-wide `max`/`avg` of sessions, active, idle_in_tx, lock/lwlock/subtrans waits, max transaction age, plus SLRU counter deltas. Persist **window max**, not just the last point, so spikes between writes are kept.
- Insert in one transaction per window (a few dozen small rows). Roughly 2-3k windows/day.
- Idle detection: windows with no change may be skipped only if all values equal the previous window and are zero-wait (optional optimization).

## Incident-mode persistence

On `IncidentOpened(id)`:
1. Create the incident row (status Open) and **flush the ring buffer** (pre-incident, phase `pre`) in a single transaction, deduplicating SQL text by hash.
2. Switch to incident cadence; each tick enqueue detail rows (phase `active`) to a **bounded** `Channel<T>` (DropOldest never for open-incident data; instead apply back-pressure by coarsening: drop idle-in-tx-without-waits rows first, then widen interval, and record `detail_degraded`).
3. After recovery, continue `post` phase for the configured period (default 2 min), then close.
- Store only *relevant* sessions: in a transaction, active, or waiting; pure idle sessions are summarized as counts per db. Optional "write on change": skip a session row identical (state, wait, sql hash, xact_start) to the previous tick but write a periodic heartbeat (every 30 s) so duration is reconstructable.

## SQLite rules (Microsoft.Data.Sqlite)

- One **single writer** task consuming a bounded channel; readers (UI) use separate short-lived read-only connections. Never share a connection across threads.
- On create: `PRAGMA journal_mode=WAL; synchronous=NORMAL; foreign_keys=ON; busy_timeout=5000; journal_size_limit=<bytes>; auto_vacuum=INCREMENTAL` (auto_vacuum must be set before tables exist). Periodic `wal_checkpoint(TRUNCATE)` when idle.
- Schema versioning via `PRAGMA user_version` with embedded, forward-only SQL migration scripts. No ORM.
- Timestamps `INTEGER` unix ms UTC; repeated strings normalized into dimension tables; no JSON blobs for hot rows.
- Parameterized commands only; reuse prepared commands inside batch transactions.
- Pooling is on by default; for deterministic file release in tests/shutdown call `SqliteConnection.ClearAllPools()`.

## Retention and size cap

- Configurable: aggregates (default 30 d), closed-incident detail (default 14 d), incident headers/events (default 180 d), outbox (7 d).
- A retention job runs hourly in small batches (`DELETE ... LIMIT`-style via rowid ranges) followed by `PRAGMA incremental_vacuum(n)`; never one huge delete that blocks the writer.
- **Hard cap** (default 512 MB): enforce with `PRAGMA max_page_count` plus a soft threshold at 80%. Prune order: expired aggregates -> oldest closed-incident detail -> oldest aggregates beyond minimum. **Never delete an open incident**; if the cap would be hit by an open incident, degrade detail granularity, raise a storage warning (UI + one email), but keep the header and events.
- Expose DB size, WAL size, row counts, free disk space, and oldest/newest data in the Storage view.

## Failure containment

- SQLite write failure (disk full, locked): keep sampling, keep the ring buffer, retry with backoff, raise `StorageDegraded`; the incident engine and email path continue.
- Crash safety: WAL + `synchronous=NORMAL` tolerates app crash; on startup mark incidents left `Open` by a previous process as `Interrupted` with a gap event, then let the engine decide whether to resume.
- Any queue, cache or list in the app must have an explicit capacity. A reviewer should be able to point to the bound for each.
