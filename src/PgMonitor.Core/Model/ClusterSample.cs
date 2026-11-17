namespace PgMonitor.Core.Model;

/// <summary>How much of pg_stat_activity the monitoring account can see.</summary>
public enum Visibility
{
    Unknown,
    /// <summary>Superuser or member of pg_read_all_stats: every row has full details.</summary>
    Full,
    /// <summary>Some rows are redacted.</summary>
    Partial,
    /// <summary>Only the account's own sessions have details.</summary>
    OwnOnly,
}

public enum CollectFailureKind
{
    Timeout,
    Disconnected,
    AuthFailed,
    PermissionDenied,
    Unknown,
}

/// <summary>Identity of the PostgreSQL node that produced a sample. A change means restart or failover.</summary>
public sealed record ServerIdentity(int VersionNum, DateTimeOffset PostmasterStart, string? ServerAddress, bool InRecovery);

/// <summary>
/// One monitoring sample. Either it succeeded (sessions are present) or it failed (<see cref="Failure"/> is set).
/// A failed sample must never be treated as healthy.
/// </summary>
public sealed record ClusterSample
{
    /// <summary>Server time (now()) for successful samples; local time of the attempt for failures.</summary>
    public required DateTimeOffset SampledAt { get; init; }
    public IReadOnlyList<SessionRow> Sessions { get; init; } = [];
    public Visibility Visibility { get; init; } = Visibility.Unknown;
    public ServerIdentity? Server { get; init; }
    public CollectFailureKind? Failure { get; init; }

    public bool Succeeded => Failure is null;

    public static ClusterSample Failed(DateTimeOffset at, CollectFailureKind kind) => new() { SampledAt = at, Failure = kind };
}
