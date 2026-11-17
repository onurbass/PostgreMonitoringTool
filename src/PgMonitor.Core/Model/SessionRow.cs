namespace PgMonitor.Core.Model;

/// <summary>
/// Stable identity of a backend session. PIDs are reused by the OS, so the backend start time is part of the key.
/// </summary>
public readonly record struct SessionKey(int Pid, DateTimeOffset BackendStart);

/// <summary>One client backend row from pg_stat_activity, as observed in a single sample.</summary>
public sealed record SessionRow
{
    public required int Pid { get; init; }
    public required DateTimeOffset BackendStart { get; init; }
    public string? Database { get; init; }
    public string? UserName { get; init; }
    public string? ApplicationName { get; init; }
    public string? ClientAddress { get; init; }
    public string? State { get; init; }
    public string? WaitEventType { get; init; }
    public string? WaitEvent { get; init; }
    public DateTimeOffset? TransactionStart { get; init; }
    public DateTimeOffset? QueryStart { get; init; }
    public DateTimeOffset? StateChange { get; init; }
    public string? BackendXid { get; init; }
    public string? BackendXmin { get; init; }
    public string? Query { get; init; }

    /// <summary>Result of pg_blocking_pids(pid). Only filled for sessions waiting on a heavyweight lock.</summary>
    public IReadOnlyList<int> BlockingPids { get; init; } = [];

    /// <summary>True when PostgreSQL hid details of this row because of missing privileges.</summary>
    public bool IsRedacted { get; init; }

    public SessionKey Key => new(Pid, BackendStart);

    public bool IsWaitingOnLock => string.Equals(WaitEventType, "Lock", StringComparison.Ordinal);

    public bool IsIdleInTransaction =>
        State is not null && State.StartsWith("idle in transaction", StringComparison.Ordinal);

    /// <summary>Time since the current statement started; for a lock waiter this is an upper bound of the lock wait.</summary>
    public TimeSpan? QueryAge(DateTimeOffset now) => QueryStart is { } q ? now - q : null;

    public TimeSpan? TransactionAge(DateTimeOffset now) => TransactionStart is { } x ? now - x : null;
}
