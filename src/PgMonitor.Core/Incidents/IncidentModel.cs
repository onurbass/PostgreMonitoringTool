using System.Collections.Immutable;
using PgMonitor.Core.Model;

namespace PgMonitor.Core.Incidents;

public enum IncidentPhase
{
    /// <summary>No incident, or the last one is closed.</summary>
    Healthy,
    Active,
    /// <summary>Triggers cleared; detail capture continues for the post-recovery period.</summary>
    Recovering,
}

/// <summary>Monitoring signal families. They are evaluated separately (see TECHNICAL_DECISIONS D8, D13).</summary>
public enum SignalKind
{
    Lock,
}

public enum IncidentEventKind
{
    Opened,
    Reopened,
    SeverityEscalated,
    DatabaseAffected,
    RecoveryStarted,
    Reactivated,
    Closed,
    GapStarted,
    GapEnded,
    ServerChanged,
}

/// <summary>Something that happened. <see cref="IncidentId"/> is 0 for events outside an incident.</summary>
public sealed record IncidentEvent(
    IncidentEventKind Kind,
    long IncidentId,
    DateTimeOffset At,
    Severity Severity,
    string Detail);

/// <summary>The incident the engine is tracking (open, or recently closed and still inside the reopen window).</summary>
public sealed record IncidentInfo
{
    public required long Id { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required Severity Severity { get; init; }
    public ImmutableSortedSet<string> Databases { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);
    public ImmutableList<SignalKind> Signals { get; init; } = [];
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>Complete engine state. Immutable; the engine returns a new one per sample.</summary>
public sealed record EngineState
{
    public IncidentPhase Phase { get; init; } = IncidentPhase.Healthy;
    public IncidentInfo? Incident { get; init; }

    /// <summary>Trigger results of the most recent successful samples, newest last.</summary>
    public ImmutableList<bool> RecentTriggers { get; init; } = [];

    public DateTimeOffset? ClearSince { get; init; }
    public DateTimeOffset? RecoveringSince { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public bool InGap { get; init; }
    public ServerIdentity? Server { get; init; }
    public long LastIncidentId { get; init; }

    public static EngineState Initial { get; } = new();
}

/// <summary>What the engine decided for one sample.</summary>
public sealed record EngineResult(EngineState State, IReadOnlyList<IncidentEvent> Events, LockEvaluation? Lock);

/// <summary>Lock signal evaluation for one sample.</summary>
public sealed record LockEvaluation(
    bool Triggered,
    Severity Severity,
    int QualifyingWaiters,
    Locks.LockAnalysis Analysis);
