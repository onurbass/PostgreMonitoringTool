# Default configuration and email layout

All values live in `settings.json` (non-secret), validated at startup (`ValidateOnStart`), editable in the UI, and covered by unit tests at their boundaries.

## Detection

| Key | Default | Notes |
|---|---|---|
| `Polling.NormalSeconds` | 5 | min 2 |
| `Polling.IncidentSeconds` | 2 | min 1 |
| `Triggers.SubtransWaitSessions` | 1 | consecutive: 2 of 3 |
| `Triggers.LockWaitSessions` | 10 | |
| `Triggers.LWLockSurgeFactor` | 3.0 | x rolling baseline (EWMA, 15 min) |
| `Triggers.LWLockSurgeFloor` | 10 | absolute minimum waits |
| `Triggers.SlruBlksReadPerSecond` | 50 | tune from observed baseline |
| `Triggers.LongTransactionMinutes` | 15 | advisory |
| `Triggers.MultiDatabaseMinimum` | 2 | |
| `Recovery.ClearSamples` | 6 | at incident cadence |
| `Recovery.PostCollectionSeconds` | 120 | |
| `Recovery.ReopenWindowMinutes` | 10 | |
| `Buffer.PreIncidentSeconds` | 180 | |

## Notification

| Key | Default |
|---|---|
| `Email.Enabled` | false until configured and tested |
| `Email.Recipients` | explicit list |
| `Email.EscalationCooldownMinutes` | 10 |
| `Email.MaxPerHour` | 6 |
| `Email.IncludeSql` | true (masked, <= 300 chars) |
| `Email.Retry` | backoff 1-30 min, 8 attempts |

## Email body outline (plain text)

```
[Critical] INC-20261117-0142  -  OPENED  (2026-11-17 10:41:07 local, server time 07:41:07Z)
Status: Active    Duration: 00:02:10    Visibility: Partial (14 sessions not visible)

Affected: db_x (App X), db_y (App Y)
Waits (observed): LWLock:SubtransSLRU 12, Lock:transactionid 3 | total sessions 187 (active 41)
SLRU (derived): Subtrans blks_read 310/s (baseline 4/s)

Longest transactions (observed)
  pid 4121  db_x  user u1  10.1.2.3  xact 00:47:12  state active   SELECT/UPDATE ...
Potentially contributing sessions (suspected, confidence Medium)
  1. pid 4121 db_x - txn older than onset; running UPDATE; subxact data unavailable (PG16)
Blocking (observed): root blocker pid 3302 (idle in transaction 00:12:44) -> 4 sessions

Limits: pg_stat_get_backend_subxact unavailable; partial session visibility.
No action was taken by this tool; sessions are never terminated by it.
```
