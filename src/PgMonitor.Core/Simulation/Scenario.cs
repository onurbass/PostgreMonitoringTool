using PgMonitor.Core.Model;

namespace PgMonitor.Core.Simulation;

/// <summary>
/// A scripted, deterministic timeline of fictional cluster behaviour. Times are offsets from <see cref="Start"/>.
/// All names and addresses are invented (documentation IP ranges, example databases).
/// </summary>
public sealed record Scenario
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(5);
    public int Seed { get; init; } = 42;
    public DateTimeOffset Start { get; init; } = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Background noise: probability per sample that one short (under 10 s) lock wait is visible.</summary>
    public double NoiseLockProbability { get; init; } = 0.03;

    public IReadOnlyList<LockChain> LockChains { get; init; } = [];
    public IReadOnlyList<Outage> Outages { get; init; } = [];
    public IReadOnlyList<ClockJump> ClockJumps { get; init; } = [];
    public IReadOnlyList<RestartAt> Restarts { get; init; } = [];
    public IReadOnlyList<PartialVisibility> Visibility { get; init; } = [];
}

/// <summary>
/// A lock chain in one database during [From, To). The root holds a transaction open; waiters pile up behind it.
/// With <see cref="SecondLevelWaiters"/> &gt; 0, those wait on the first waiter (chain depth 2).
/// </summary>
public sealed record LockChain
{
    public required TimeSpan From { get; init; }
    public required TimeSpan To { get; init; }
    public int DatabaseIndex { get; init; }
    public int Waiters { get; init; } = 3;
    public int SecondLevelWaiters { get; init; }

    /// <summary>"idle in transaction" (typical forgotten transaction) or "active".</summary>
    public string RootState { get; init; } = "idle in transaction";

    /// <summary>How long waiters had already been waiting when the chain becomes visible (for blips).</summary>
    public TimeSpan InitialWait { get; init; } = TimeSpan.Zero;

    /// <summary>A new waiter joins every this often until <see cref="Waiters"/> is reached.</summary>
    public TimeSpan WaiterStagger { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>At <see cref="To"/>, the sessions are terminated (DBA cleanup) and their apps reconnect.</summary>
    public bool TerminatedAtEnd { get; init; } = true;

    /// <summary>Reconnected sessions get the same PID back (PID reuse) with a new backend_start.</summary>
    public bool ReusePidOnReconnect { get; init; }
}

/// <summary>Collection fails during [From, To) (VPN drop, server unreachable).</summary>
public sealed record Outage(TimeSpan From, TimeSpan To, CollectFailureKind Kind = CollectFailureKind.Disconnected);

/// <summary>The laptop sleeps at <see cref="At"/> for <see cref="Length"/>; no samples are produced in between.</summary>
public sealed record ClockJump(TimeSpan At, TimeSpan Length);

/// <summary>PostgreSQL restarts at <see cref="At"/>: new postmaster start time, every session reconnects.</summary>
public sealed record RestartAt(TimeSpan At);

/// <summary>During [From, To) only <see cref="VisibleUser"/>'s rows have details; other rows are redacted.</summary>
public sealed record PartialVisibility(TimeSpan From, TimeSpan To, string VisibleUser = "svc_orchid");
