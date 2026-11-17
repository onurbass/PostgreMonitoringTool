using PgMonitor.Core.Aggregation;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;

namespace PgMonitor.Infrastructure.Storage;

public enum IncidentStatus
{
    Active,
    Recovering,
    Closed,
    /// <summary>The app stopped while the incident was open; it was never seen to recover.</summary>
    Interrupted,
}

/// <summary>Which part of an incident a stored sample belongs to.</summary>
public enum SamplePhase
{
    /// <summary>From the in-memory buffer: the minutes before the incident opened.</summary>
    Pre,
    Active,
    Recovering,
}

public sealed record StoredIncident(
    long Id,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    IncidentStatus Status,
    Severity Severity,
    IReadOnlyList<string> Databases,
    int SampleCount);

public sealed record StoredEvent(long? IncidentId, DateTimeOffset At, IncidentEventKind Kind, Severity Severity, string Detail);

public sealed record StoredSample(long Id, long IncidentId, DateTimeOffset At, SamplePhase Phase, Visibility Visibility, CollectFailureKind? Failure, int RedactedRows, IReadOnlyList<SessionRow> Sessions);

public sealed record StoredIncidentDetail(StoredIncident Incident, IReadOnlyList<StoredEvent> Events, IReadOnlyList<StoredSample> Samples);

public sealed record StorageReport(long DatabaseBytes, long WalBytes, int IncidentCount, int OpenIncidentCount, int AggregateRows, int SampleRows)
{
    public long TotalBytes => DatabaseBytes + WalBytes;
}

public sealed record StorageSettings
{
    public TimeSpan AggregateRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan IncidentRetention { get; init; } = TimeSpan.FromDays(180);

    /// <summary>Soft cap on database + WAL size (checked after writes, see TECHNICAL_DECISIONS D11).</summary>
    public long MaxBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Rows deleted per statement during retention, so the writer is never blocked for long.</summary>
    public int DeleteBatchSize { get; init; } = 5_000;
}

/// <summary>Result of a retention / size-cap pass.</summary>
public sealed record MaintenanceResult(int AggregatesDeleted, int IncidentsDeleted, int SamplesThinned, bool OverCapWithOpenIncidents, StorageReport Report);

/// <summary>One recorded lock wait (any wait of at least the minimum duration, incident or not).</summary>
public sealed record StoredLockWait(
    int Pid,
    DateTimeOffset? BackendStart,
    DateTimeOffset QueryStart,
    string? Database,
    string? UserName,
    string? ApplicationName,
    string? ClientAddress,
    string? WaitEvent,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset? EndedAt,
    string? EndReason,
    IReadOnlyList<int> Blockers,
    int? RootPid,
    string? RootDatabase,
    string? RootUser,
    string? RootClient,
    string? RootState,
    DateTimeOffset? RootTransactionStart,
    string? Query,
    string? RootQuery)
{
    /// <summary>Longest observed wait: from the statement start to the last sample that still showed it waiting.</summary>
    public TimeSpan MaxWait => LastSeen - QueryStart;
}

/// <summary>A transaction that stayed open longer than the threshold (advisory record, no incident).</summary>
public sealed record StoredLongTransaction(
    int Pid,
    DateTimeOffset? BackendStart,
    DateTimeOffset TransactionStart,
    string? Database,
    string? UserName,
    string? ApplicationName,
    string? ClientAddress,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    DateTimeOffset? EndedAt,
    string? EndReason,
    string? State,
    string? WaitEventType,
    string? WaitEvent,
    DateTimeOffset? QueryStart,
    DateTimeOffset? StateChange,
    string? Query);

/// <summary>A queued e-mail. <see cref="Mime"/> is the complete RFC 822 message; it never contains a password.</summary>
public sealed record OutboxMail(string DedupeKey, long? IncidentId, string Kind, DateTimeOffset CreatedAt, string Subject, byte[] Mime)
{
    public long Id { get; init; }
    public int Attempts { get; init; }
}

public sealed record OutboxCounts(int Pending, int Sent, int Failed);

/// <summary>Persistence for aggregates and incident evidence. One instance per database file; single writer.</summary>
public interface IMonitorStore : IDisposable
{
    /// <summary>Inserts or updates lock-wait records (one row per waiting statement).</summary>
    void RecordLockWaits(IReadOnlyList<PgMonitor.Core.Locks.LockWaitEvent> events);

    IReadOnlyList<StoredLockWait> ListLockWaits(int limit = 1000);

    /// <summary>Most recent events of any kind, newest first (survives restarts).</summary>
    IReadOnlyList<StoredEvent> ListRecentEvents(int limit = 50);

    /// <summary>Inserts or updates long-transaction records (one row per transaction).</summary>
    void RecordLongTransactions(IReadOnlyList<PgMonitor.Core.Transactions.LongTxEvent> events);

    IReadOnlyList<StoredLongTransaction> ListLongTransactions(int limit = 500);

    /// <summary>Queues a mail. Returns false when a mail with the same dedupe key already exists (idempotent).</summary>
    bool EnqueueMail(OutboxMail mail);

    IReadOnlyList<OutboxMail> DueMails(DateTimeOffset now, int limit = 10);

    void MarkMailSent(long id, DateTimeOffset now);

    /// <summary>Records a failed attempt; <paramref name="nextAttempt"/> null means give up.</summary>
    void MarkMailFailed(long id, DateTimeOffset? nextAttempt, string safeError);

    DateTimeOffset? LastMailAt(long incidentId, string kind);

    OutboxCounts MailCounts();

    long MaxIncidentId();

    /// <summary>Marks incidents left open by a previous run as Interrupted. Returns how many.</summary>
    int MarkOpenIncidentsInterrupted(DateTimeOffset now);

    void WriteAggregate(AggregateRow row);

    void UpsertIncident(IncidentInfo incident, IncidentStatus status);

    void AppendEvents(IReadOnlyList<IncidentEvent> events);

    /// <summary>Stores samples of one incident in a single transaction. Plain idle sessions are not stored (they hold no locks).</summary>
    void WriteSamples(long incidentId, SamplePhase phase, IReadOnlyList<ClusterSample> samples);

    IReadOnlyList<StoredIncident> ListIncidents(int limit = 200);

    StoredIncidentDetail? GetIncident(long id);

    IReadOnlyList<AggregateRow> ReadAggregates(DateTimeOffset from, DateTimeOffset to);

    MaintenanceResult RunMaintenance(DateTimeOffset now, StorageSettings settings);

    StorageReport Report();
}
