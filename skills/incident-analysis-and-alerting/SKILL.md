---
name: incident-analysis-and-alerting
description: Incident detection, lifecycle and alerting logic for Subtrans/Lock/LWLock events - trigger rules with hysteresis and dedup, incident state machine, severity, observed vs derived vs suspected evidence, suspect ranking without false root-cause claims, disappeared-session detection, and email notification policy with cooldowns and an outbox. Use when changing thresholds, the incident engine, investigation views/reports or notification content.
compatibility: Pure .NET logic in PgMonitor.Core driven by samples and TimeProvider; PostgreSQL 13-18 capability flags; SMTP via MailKit with TLS. No external alerting services.
---

# incident-analysis-and-alerting

Owns: **deciding that something is an incident, tracking it, explaining it honestly, and telling the user.** Data comes from postgres-diagnostics/lightweight-monitoring-architecture; secrets/TLS rules come from secure-local-development. Default thresholds and email layout: `references/rules-and-defaults.md`.

## Engine contract

`IncidentEngine.Process(Sample) -> EngineResult` is a **pure, deterministic** function of (state, sample, config, time from the sample/TimeProvider). No IO, no static clocks. Output: state transitions, events (with evidence class), and commands (`OpenIncident`, `Escalate`, `Recover`, `Close`, `SendNotification`). Because it is pure, every rule is replayable in tests from recorded samples.

## Triggers (any one, config-driven, evaluated on consecutive samples)

| Trigger | Default condition |
|---|---|
| Subtrans waits | >= 1 session `LWLock` `Subtrans*` in 2 of last 3 samples |
| Lock waits | >= N sessions in `Lock` wait (default 10) in 2 of 3 |
| LWLock surge | total LWLock waits > max(floor, k x rolling baseline) |
| SLRU pressure | Subtrans `blks_read` per second above threshold for 2 samples (if capability present) |
| Long transaction | xact age > T (default 15 min): **advisory** only unless combined with another trigger |
| Multi-database | waits observed in >= 2 databases in the same sample |

Rules: use **consecutive-sample confirmation** to avoid single-sample blips; hard triggers (Subtrans) may use 2/3, advisory triggers never open an incident alone. Samples with `SampleFailed`, gaps or visibility `OwnOnly` never count as "clear".

## State machine

```
Healthy -> Candidate -> Active -> Recovering -> PostRecovery -> Closed
              ^           ^           |              |
              |           +-----------+--------------+   (re-trigger returns to Active, same incident)
```
- **Candidate**: trigger seen but unconfirmed; nothing persisted except the in-memory buffer.
- **Active** (open): create incident row, flush pre-incident buffer, switch to incident cadence (architecture skill), notify "opened".
- **Recovering**: all triggers clear for R consecutive samples (default 6 at incident cadence). Any trigger resets to Active.
- **PostRecovery**: keep detail for configured period (default 2 min), then Closed and notify "resolved".
- **Dedup / flapping**: a trigger within the reopen window (default 10 min) of a Closed incident *reopens* it (event `reopened`) instead of creating a new one. At most one open incident at a time; additional databases/triggers become *scope expansion* events and may escalate severity.
- **Unknown handling**: while Disconnected/gap, freeze the state, record `monitor_gap`; do not auto-recover. After a gap > recovery window, require fresh clear samples.
- On restart, previous `Open` incidents become `Interrupted`; resume as Active only if triggers are still present.

## Severity (recompute each sample, escalate only upward within an incident)

- **Warning**: trigger present, <= 1 database, waits < 5, duration < 2 min.
- **Major**: Subtrans waits >= 5 or lock waits >= threshold, or >= 2 databases, or duration > 5 min.
- **Critical**: waits >= 20, or >= 3 databases, or duration > 15 min, or idle sessions/connection headroom dangerously low.
All cut-offs are configurable. Escalation emails obey cooldown.

## Evidence discipline (the core of honest analysis)

Every statement in the UI, reports and emails carries a class:

- **Observed**: directly present in a sample (e.g. "PID 4121 in db X waited on LWLock:SubtransSLRU at 10:41:05").
- **Derived**: computed from observations (durations, SLRU deltas, counts, disappearance gaps).
- **Suspected**: heuristic inference with reasons and a confidence tier.

Never state a root cause as fact. Use wording such as "Potentially contributing session" and list indicators. Waiting sessions are **victims** until evidence says otherwise; the first waiter and the longest transaction are *not* automatically the cause.

### Subtrans suspect ranking

Rank sessions (not just top-1; show top 5) by weighted indicators, each with an explanation line:
1. Transaction began before the first abnormal sample and was still open (age at onset).
2. `subxact_overflow = true` or high `subxact_count` (PG17+ only; mark "unavailable" otherwise).
3. State `active` running DML (INSERT/UPDATE/DELETE) with long running-query age.
4. Large `xmin_age` (pins the horizon).
5. Concentration: its database/application shows the largest share of long transactions at onset.
6. Temporal correlation of onset with `pg_stat_slru` Subtrans `blks_read` growth.

Confidence: **High** only with >= 2 independent strong indicators including a direct signal (overflow or SLRU correlation) *and* Full visibility; **Medium** with indirect signals only; **Low** otherwise. Cap at Medium under Partial visibility. State what could not be checked.

### Lock analysis (separate path)

From `incident_blocking` build blocker chains, identify **root blockers** (blocking, not blocked), their state (often `idle in transaction`), age, and SQL last seen. This is Observed, not Suspected, because PostgreSQL reports it.

### Disappeared sessions

Key sessions by `(pid, backend_start)`. If a session with a long transaction/wait is present in tick *t* and absent in *t+1*: record `session_disappeared` (Derived) with last-seen state. Say "terminated or completed - not verifiable"; do not claim termination unless several sessions vanish in the same tick while waits drop to zero (label as *Suspected: mass termination*). Record last-seen snapshot so evidence survives.

## Notifications

- Events: `Opened`, `Escalated` (severity increase, cooldown default 10 min), `Resolved`; optional storage-warning and monitor-down notices. **Never per sample.**
- Per-incident cooldown plus global rate limit (default max 6 mails/hour; excess folded into the next message).
- Content: reference id, status, time, duration, severity, affected databases + mapped application names, wait-event counts, affected session count, long transactions, top suspects (with evidence class and confidence), client IPs/users, truncated SQL (about 300 chars, literal-masked by default), visibility level and capability limitations. Plain text first, simple HTML optional. No credentials, no connection strings, no file paths beyond the incident id.
- Delivery goes through a **persistent outbox** (SQLite), processed by a separate worker with backoff (1 min -> 30 min), max attempts, and bounded retention. Sampling and incident logic must never await SMTP.
- Recipients/server from settings; SMTP password from Credential Manager; TLS validated. Provide "Send test email". If email fails repeatedly, show a UI banner and keep the incident intact.
- Subject pattern: `[PG Monitor][Critical] INC-20261117-0142 Subtrans waits - 3 DBs - OPENED`.

## Investigation views (data requirements)

Timeline from `pre` through `post`, wait-event counts over time, first abnormal session, per-session transaction age evolution, affected databases and applications, blocker chains, disappearance events, gaps, SLRU deltas. Always display the raw fields beside the mapped application name, and the visibility/capability banner.
