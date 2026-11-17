namespace PgMonitor.Core.Incidents;

public enum Severity
{
    None = 0,
    Warning = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Thresholds for incident detection. Defaults follow design/ARCHITECTURE.md section 6.</summary>
public sealed record IncidentSettings
{
    /// <summary>A lock waiter counts once its statement has waited at least this long.</summary>
    public TimeSpan LockWaitThreshold { get; init; } = TimeSpan.FromSeconds(30);

    public int HighWaiterCount { get; init; } = 5;
    public TimeSpan HighWait { get; init; } = TimeSpan.FromMinutes(2);
    public int CriticalWaiterCount { get; init; } = 20;
    public int CriticalDatabaseCount { get; init; } = 2;

    /// <summary>Open when the trigger held in at least <see cref="OpenHits"/> of the last <see cref="OpenWindow"/> successful samples.</summary>
    public int OpenHits { get; init; } = 2;
    public int OpenWindow { get; init; } = 3;

    /// <summary>All triggers must be clear for this long (successful samples only) before recovery starts.</summary>
    public TimeSpan RecoveryClear { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Detail capture continues this long after recovery starts; then the incident closes.</summary>
    public TimeSpan PostRecovery { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A trigger this soon after closing reopens the same incident instead of opening a new one.</summary>
    public TimeSpan ReopenWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Two successful samples further apart than this are treated as a gap (sleep, VPN drop).</summary>
    public TimeSpan MaxSampleGap { get; init; } = TimeSpan.FromSeconds(30);
}
