# Project Context – PostgreSQL Cluster Monitoring Tool

## 1. Environment and Background

Multiple Mendix applications run on a shared PostgreSQL cluster.

Each Mendix application has its own database, but all databases share the same PostgreSQL cluster and underlying resources.

We occasionally experience severe database performance problems related to Subtrans (Subtransaction) contention.

When one application experiences or contributes to Subtrans-related contention, other applications sharing the same PostgreSQL cluster can also experience database performance degradation, lock waits and blocked sessions.

The database team usually resolves these incidents by terminating problematic sessions.

The main problem is that after sessions are terminated, the original session information disappears from pg_stat_activity. Consequently, investigating the root cause after the incident becomes extremely difficult.

I currently investigate these problems manually using PostgreSQL monitoring queries.

I will provide:

- My existing SQL monitoring queries.
- A screenshot captured during an actual database incident.
- Additional technical observations when necessary.

Use these materials as the initial reference for designing the monitoring system.

## 2. Main Objective

Develop a local PostgreSQL monitoring application that can detect, record, alert on and help investigate database incidents.

The most important requirement is preserving evidence before it disappears.

The application should help answer the following questions:

- Which database or Mendix application was affected?
- Which application or sessions may have contributed to the problem?
- Which sessions were waiting?
- Were the waits related to Lock, LWLock or Subtrans?
- Which SQL statements were executing?
- Which sessions had unusually long-running transactions?
- Were there blocking relationships between sessions?
- When did the incident start?
- How did the situation evolve?
- Which sessions were terminated before the investigation?
- When did the problem recover?

The application should provide historical evidence rather than relying exclusively on real-time database snapshots.

## 3. Storage-Efficient Monitoring Architecture

Storage consumption is a major concern because this application will run locally and continuously collect monitoring data.

Do not persist every detailed pg_stat_activity snapshot to SQLite.

Implement a multi-level monitoring strategy.

### 3.1 Normal Monitoring Mode

During healthy operation:

- Poll PostgreSQL at a configurable interval, initially 5 seconds.
- Collect lightweight cluster-level health metrics.
- Monitor Lock and LWLock wait counts.
- Monitor SubtransSLRU and SubtransBuffer wait counts.
- Collect session counts grouped by database.
- Monitor long-running transactions and idle-in-transaction session counts.
- Collect relevant cumulative Subtrans statistics.
- Maintain a bounded in-memory circular buffer containing recent session-level snapshots.
- Keep approximately 3 minutes of pre-incident history in memory.
- Limit memory consumption and the maximum number of retained snapshots.
- Persist only compact aggregate metrics to SQLite, preferably every 30-60 seconds.

Do not continuously persist full session details or SQL query text while the cluster is healthy.

### 3.2 Incident Monitoring Mode

When predefined abnormal conditions are detected, automatically activate detailed monitoring.

Potential triggers include:

- One or more SubtransSLRU wait sessions.
- SubtransBuffer wait sessions.
- An unusual increase in LWLock wait counts.
- A configurable number of Lock wait sessions.
- Long-running transactions exceeding configured thresholds.
- A sudden increase in relevant Subtrans statistics.
- Multiple databases experiencing wait events simultaneously.

When an incident is detected:

1. Create an incident record.
2. Preserve the existing in-memory snapshots by flushing the pre-incident buffer to SQLite.
3. Capture detailed information about all relevant client backend sessions, not only sessions currently waiting.
4. Store SQL query text, PID, database name, username, application_name, client IP, session state, wait events and transaction/query durations.
5. Record blocking relationships whenever PostgreSQL can identify them.
6. Increase monitoring frequency during the incident if appropriate.
7. Continue storing detailed snapshots until recovery.
8. Collect an additional configurable post-recovery period.
9. Close the incident and preserve its historical information.

Avoid creating multiple incidents for the same ongoing problem.

Implement incident deduplication, consecutive-sample checks, configurable thresholds and recovery conditions.

### 3.3 Historical Investigation

The application should provide the ability to select an incident and investigate:

- Conditions before the incident.
- The first detected abnormal session.
- Session activity throughout the incident.
- Potentially responsible applications and sessions.
- Changes in wait event counts.
- The evolution of transaction durations.
- Affected databases.
- Recovery and termination events, when observable.

Do not automatically assume that the first waiting session or the session with the longest transaction is the root cause.

Distinguish between observed facts and suspected causes.

### 3.4 Data Retention and Storage Limits

Use SQLite for local persistent storage.

Implement:

- Configurable retention periods for aggregate metrics.
- Separate retention periods for detailed incident snapshots.
- Automatic deletion of expired records.
- A configurable maximum storage size.
- Protection for unresolved incidents from automatic deletion.
- Visibility into current SQLite database size.
- Bounded memory usage.

The system should remain lightweight even when monitoring runs continuously for extended periods.

## 4. PostgreSQL Monitoring Requirements

Use pg_stat_activity as the primary source of live session information.

Important fields include:

- pid
- datname
- application_name
- usename
- client_addr
- state
- wait_event_type
- wait_event
- xact_start
- query_start
- state_change
- query
- backend_type

Pay particular attention to:

- Lock / transactionid
- LWLock / SubtransSLRU
- LWLock / SubtransBuffer
- idle in transaction
- Long-running transactions
- Active sessions
- Session counts grouped by database

Transaction duration and query duration must be treated as separate metrics.

Also distinguish between the current query duration and the duration of a previously executed query when a session is not active.

### 4.1 Additional PostgreSQL Statistics

Evaluate whether the following monitoring sources can provide additional diagnostic information:

**pg_stat_slru**

Investigate the Subtrans SLRU statistics, including:

- blocks hit
- blocks read
- blocks written
- flushes
- other available counters

Capture cumulative counter values and calculate their changes between monitoring samples.

Do not interpret cumulative values as instantaneous activity.

**Backend subtransaction statistics**

Investigate the availability of:

- pg_stat_get_backend_subxact()

Use backend-level subtransaction count and overflow information when supported by the PostgreSQL version and accessible through my existing database permissions.

Check PostgreSQL version compatibility before implementing these queries.

If a monitoring statistic is unavailable, handle it gracefully without breaking the entire monitoring process.

### 4.2 Lock and LWLock Analysis

Do not treat PostgreSQL Lock waits and LWLock waits as equivalent.

Use pg_blocking_pids() to investigate ordinary PostgreSQL lock relationships where applicable.

However, do not assume that pg_blocking_pids() can identify the source of SubtransSLRU or SubtransBuffer waits.

Implement separate analytical approaches:

- Lock analysis: Identify blocked sessions and their blocking sessions when PostgreSQL provides this information.
- LWLock/Subtrans analysis: Correlate wait events, transaction ages, backend activity and available Subtrans statistics across monitoring samples.

The tool should identify potentially suspicious sessions and applications without claiming an exact root cause when the available evidence is insufficient.

## 5. Application Identification

All Mendix applications share the same PostgreSQL cluster, but they have separate databases.

Identifying the originating application is an important requirement.

Use the following fields together:

- datname
- application_name
- usename
- client_addr
- pid

Do not assume application_name always identifies the Mendix application correctly.

In my current monitoring results, application_name frequently appears as PostgreSQL JDBC Driver.

Provide a configurable database-to-application mapping so I can assign meaningful application names to database names.

The interface should display both the original database information and its configured application mapping.

## 6. Incident Alerting and Email Notifications

The application must support configurable email notifications.

Notify predefined recipients when:

- A new incident is detected.
- A significant escalation occurs, if configured.
- An incident is resolved.

Do not send an email for every monitoring cycle.

Implement configurable alert thresholds, incident deduplication and notification cooldowns.

Incident emails should include:

- Incident timestamp.
- Affected databases and applications.
- Relevant wait events.
- Number of affected sessions.
- Long-running transaction information.
- Potentially suspicious sessions.
- Relevant SQL statements when available.
- Client IP addresses and usernames.
- Incident reference and current status.

SMTP settings and credentials must be managed securely.

## 7. Local Dashboard

Provide a lightweight monitoring interface suitable for Windows desktop use.

The dashboard should include:

### Cluster Overview

- Overall cluster health.
- Total session count.
- Active session count.
- Lock wait count.
- LWLock wait count.
- SubtransSLRU and SubtransBuffer wait counts.
- Session distribution by database.

### Live Session Monitoring

- PID.
- Database.
- Application mapping.
- Username.
- Client IP.
- Session state.
- Wait event type and name.
- Transaction duration.
- Query duration.
- SQL query.

### Incident History

- Incident start and end times.
- Duration.
- Affected databases.
- Relevant session history.
- Incident severity.
- Detection and recovery information.

### Storage Monitoring

- Current SQLite database size.
- Stored incident count.
- Retention configuration.
- Available disk usage information where appropriate.

The interface should prioritize readability and quick incident investigation over unnecessary visual complexity.

## 8. Database Connection and Security Constraints

I already have authorized access to the PostgreSQL cluster through an existing database account.

I do NOT intend to request:

- A new database monitoring user.
- A new database username or password.
- Additional database privileges.
- Changes to database permissions.

The application must use my existing authorized PostgreSQL credentials.

Important requirements:

- Never hardcode database credentials.
- Never store plaintext passwords in source code, JSON, YAML, .env files, SQLite or application logs.
- Provide a secure database connection settings interface.
- Prefer Windows Credential Manager for securely storing the existing database password.
- Allow credentials to be entered and saved locally.
- Provide an option to use credentials only for the current application session without saving them.
- Retrieve saved credentials securely at runtime.
- Never log connection passwords.
- Never expose credentials through error messages.
- Allow saved credentials to be removed.
- Apply equivalent secure storage to SMTP credentials.

Connection information such as host, port and database name may be stored in local configuration.

Use my existing database account's permissions as the source of truth.

Do not assume that pg_read_all_stats, pg_monitor or any other additional role is available.

Design monitoring queries to work with
