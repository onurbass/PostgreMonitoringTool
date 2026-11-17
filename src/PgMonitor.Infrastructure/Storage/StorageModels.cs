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

/// <summary>Persistence for aggregates and incident evidence. One instance per database file; single writer.</summary>
public interface IMonitorStore : IDisposable
{
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
