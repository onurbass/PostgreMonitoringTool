# SQLite schema blueprint (user_version 1)

Timestamps = unix ms UTC (`INTEGER`). Durations = milliseconds. Strings normalized into `dim_string`. Adjust, but keep: dimension tables, no per-row JSON, indexes only where listed.

```sql
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;

CREATE TABLE dim_string (            -- db names, users, apps, IPs, wait events
  id INTEGER PRIMARY KEY, value TEXT NOT NULL UNIQUE);

CREATE TABLE sql_text (              -- one row per distinct statement snippet
  hash INTEGER PRIMARY KEY,          -- 64-bit hash of normalized text
  text TEXT NOT NULL, truncated INTEGER NOT NULL DEFAULT 0, first_seen INTEGER NOT NULL);

-- Normal-mode aggregates: one row per window per db (db_id=0 means cluster-wide)
CREATE TABLE metric_window (
  window_start INTEGER NOT NULL, db_id INTEGER NOT NULL,
  samples INTEGER NOT NULL,
  sessions_max INTEGER, active_max INTEGER, idle_in_tx_max INTEGER,
  lock_waits_max INTEGER, lwlock_waits_max INTEGER, subtrans_waits_max INTEGER,
  invisible_max INTEGER, max_xact_age_ms INTEGER,
  PRIMARY KEY (window_start, db_id)) WITHOUT ROWID;

CREATE TABLE slru_window (           -- per-second deltas, Subtrans only
  window_start INTEGER PRIMARY KEY, blks_hit_ps REAL, blks_read_ps REAL,
  blks_written_ps REAL, flushes_ps REAL, reset_detected INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;

CREATE TABLE incident (
  id INTEGER PRIMARY KEY, ref TEXT NOT NULL UNIQUE,   -- e.g. INC-20261117-0142
  status TEXT NOT NULL,              -- Open | Recovering | Closed | Interrupted
  severity_max TEXT NOT NULL,
  opened_at INTEGER NOT NULL, first_abnormal_at INTEGER, recovered_at INTEGER, closed_at INTEGER,
  server_version INTEGER, visibility TEXT NOT NULL,    -- Full | Partial | OwnOnly
  trigger_summary TEXT NOT NULL);
CREATE INDEX ix_incident_opened ON incident(opened_at);

CREATE TABLE incident_tick (
  incident_id INTEGER NOT NULL REFERENCES incident(id) ON DELETE CASCADE,
  tick_id INTEGER NOT NULL, ts INTEGER NOT NULL,
  phase TEXT NOT NULL,               -- pre | active | recovering | post
  lock_waits INTEGER, lwlock_waits INTEGER, subtrans_waits INTEGER, sessions INTEGER,
  slru_blks_read_ps REAL, degraded INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (incident_id, tick_id)) WITHOUT ROWID;

CREATE TABLE incident_session_obs (
  incident_id INTEGER NOT NULL, tick_id INTEGER NOT NULL,
  pid INTEGER NOT NULL, backend_start INTEGER NOT NULL,
  db_id INTEGER, user_id INTEGER, app_id INTEGER, ip_id INTEGER,
  state_id INTEGER, wait_type_id INTEGER, wait_event_id INTEGER,
  xact_start INTEGER, xact_age_ms INTEGER, state_age_ms INTEGER, query_age_ms INTEGER,
  xmin_age INTEGER, subxact_count INTEGER, subxact_overflow INTEGER,  -- NULL = unavailable
  sql_hash INTEGER REFERENCES sql_text(hash),
  PRIMARY KEY (incident_id, tick_id, pid, backend_start),
  FOREIGN KEY (incident_id, tick_id) REFERENCES incident_tick(incident_id, tick_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE incident_blocking (
  incident_id INTEGER NOT NULL, tick_id INTEGER NOT NULL,
  blocked_pid INTEGER NOT NULL, blocker_pid INTEGER NOT NULL,
  PRIMARY KEY (incident_id, tick_id, blocked_pid, blocker_pid),
  FOREIGN KEY (incident_id, tick_id) REFERENCES incident_tick(incident_id, tick_id) ON DELETE CASCADE
) WITHOUT ROWID;

CREATE TABLE incident_event (        -- opened, escalated, session_disappeared, gap, notified, ...
  id INTEGER PRIMARY KEY, incident_id INTEGER NOT NULL REFERENCES incident(id) ON DELETE CASCADE,
  ts INTEGER NOT NULL, kind TEXT NOT NULL, evidence TEXT NOT NULL,  -- Observed | Derived | Suspected
  detail TEXT NOT NULL);
CREATE INDEX ix_event_incident ON incident_event(incident_id, ts);

CREATE TABLE notification_outbox (   -- bounded; pruned by age
  id INTEGER PRIMARY KEY, incident_id INTEGER, kind TEXT NOT NULL,
  created_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
  next_attempt_at INTEGER NOT NULL, status TEXT NOT NULL,           -- Pending | Sent | Failed
  subject TEXT NOT NULL, body TEXT NOT NULL);
CREATE INDEX ix_outbox_due ON notification_outbox(status, next_attempt_at);
```

Notes
- The `ON DELETE CASCADE` chain makes retention a delete of `incident_tick` / `incident` rows; run it in batches (e.g. 1 000 ticks) and follow with `PRAGMA incremental_vacuum(2000)`.
- Orphan `sql_text` rows: delete where `hash NOT IN (SELECT DISTINCT sql_hash FROM incident_session_obs)` in the same retention pass.
- No passwords, connection strings, tokens or SMTP secrets in any column or `meta`. `meta` holds schema/app version and last-clean-shutdown flag only.
- Rough sizing: 200 relevant sessions x 2 s ticks x 30 min is about 180 k observation rows, roughly 15-20 MB for one long incident. Measure with real data and tune the "write on change" optimisation and retention before raising caps.
