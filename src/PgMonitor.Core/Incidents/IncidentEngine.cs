using System.Collections.Immutable;
using System.Globalization;
using PgMonitor.Core.Locks;
using PgMonitor.Core.Model;

namespace PgMonitor.Core.Incidents;

/// <summary>
/// Pure incident logic: (previous state, sample, settings) -> (new state, events).
/// No I/O, no clock: all times come from the sample. Failed samples and gaps never count as recovery.
/// </summary>
public static class IncidentEngine
{
    public static EngineResult Process(EngineState state, ClusterSample sample, IncidentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(settings);

        var events = new List<IncidentEvent>();
        var t = sample.SampledAt;
        var openId = state.Phase == IncidentPhase.Healthy ? 0 : state.Incident?.Id ?? 0;
        var openSeverity = state.Phase == IncidentPhase.Healthy ? Severity.None : state.Incident?.Severity ?? Severity.None;

        // ---- Failed sample: freeze everything, restart the recovery clock later.
        if (!sample.Succeeded)
        {
            if (!state.InGap)
            {
                events.Add(new(IncidentEventKind.GapStarted, openId, t, openSeverity, $"Sampling failed: {sample.Failure}"));
                state = state with { InGap = true, ClearSince = null };
            }
            return new(state, events, null);
        }

        // ---- Gap handling for successful samples. After a gap, recovery and post-recovery capture restart.
        var gapEnded = false;
        if (state.InGap)
        {
            events.Add(new(IncidentEventKind.GapEnded, openId, t, openSeverity, "Sampling resumed"));
            state = state with { InGap = false };
            gapEnded = true;
        }
        else if (state.LastSuccessAt is { } last && t - last > settings.MaxSampleGap)
        {
            var gap = t - last;
            events.Add(new(IncidentEventKind.GapStarted, openId, last, openSeverity, $"No samples for {Format(gap)} (sleep or network loss)"));
            events.Add(new(IncidentEventKind.GapEnded, openId, t, openSeverity, "Sampling resumed"));
            gapEnded = true;
        }
        if (gapEnded)
        {
            state = state with
            {
                ClearSince = null,
                RecoveringSince = state.Phase == IncidentPhase.Recovering ? t : state.RecoveringSince,
            };
        }

        if (state.Server is { } oldServer && sample.Server is { } newServer &&
            (oldServer.PostmasterStart != newServer.PostmasterStart ||
             !string.Equals(oldServer.ServerAddress, newServer.ServerAddress, StringComparison.Ordinal)))
        {
            events.Add(new(IncidentEventKind.ServerChanged, openId, t, openSeverity, "PostgreSQL restarted or the connection moved to another node"));
        }

        state = state with { LastSuccessAt = t, Server = sample.Server ?? state.Server };

        // ---- Evaluate the lock signal.
        var lockEval = EvaluateLock(sample, settings);
        var recent = state.RecentTriggers.Add(lockEval.Triggered);
        while (recent.Count > settings.OpenWindow)
        {
            recent = recent.RemoveAt(0);
        }
        state = state with { RecentTriggers = recent };
        var hits = recent.Count(x => x);

        switch (state.Phase)
        {
            case IncidentPhase.Healthy:
                if (lockEval.Triggered && hits >= settings.OpenHits)
                {
                    state = Open(state, lockEval, t, settings, events);
                }
                break;

            case IncidentPhase.Active:
                if (lockEval.Triggered)
                {
                    state = Grow(state with { ClearSince = null }, lockEval, t, events);
                }
                else if (sample.Visibility == Visibility.OwnOnly)
                {
                    // We cannot see other sessions, so "no waiters" proves nothing.
                    state = state with { ClearSince = null };
                }
                else
                {
                    var clearSince = state.ClearSince ?? t;
                    state = state with { ClearSince = clearSince };
                    if (t - clearSince >= settings.RecoveryClear)
                    {
                        events.Add(Event(IncidentEventKind.RecoveryStarted, state, t, $"No lock waits for {Format(t - clearSince)}"));
                        state = state with { Phase = IncidentPhase.Recovering, RecoveringSince = t };
                    }
                }
                break;

            case IncidentPhase.Recovering:
                if (lockEval.Triggered)
                {
                    events.Add(Event(IncidentEventKind.Reactivated, state, t, Describe(lockEval)));
                    state = Grow(state with { Phase = IncidentPhase.Active, RecoveringSince = null, ClearSince = null }, lockEval, t, events);
                }
                else if (state.RecoveringSince is { } since && t - since >= settings.PostRecovery)
                {
                    var closed = state.Incident! with { ClosedAt = t };
                    events.Add(new(IncidentEventKind.Closed, closed.Id, t, closed.Severity, $"Incident lasted {Format(t - closed.OpenedAt)}"));
                    state = state with { Phase = IncidentPhase.Healthy, Incident = closed, RecoveringSince = null, ClearSince = null };
                }
                break;
        }

        return new(state, events, lockEval);
    }

    public static LockEvaluation EvaluateLock(ClusterSample sample, IncidentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(settings);

        var analysis = LockGraph.Analyze(sample.Sessions, sample.SampledAt);

        // Only waits over the threshold count. Short, normal lock waits elsewhere in the cluster
        // must neither raise severity nor mark another database as affected.
        var qualifying = analysis.Waiters
            .Where(w => (w.QueryAge(sample.SampledAt) ?? TimeSpan.Zero) >= settings.LockWaitThreshold)
            .ToList();
        if (qualifying.Count == 0)
        {
            return new(false, Severity.None, 0, [], analysis);
        }

        var databases = qualifying
            .Select(w => w.Database)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var severity = Severity.Warning;
        if (qualifying.Count >= settings.HighWaiterCount || analysis.LongestWait >= settings.HighWait)
        {
            severity = Severity.High;
        }
        if (qualifying.Count >= settings.CriticalWaiterCount || databases.Count >= settings.CriticalDatabaseCount)
        {
            severity = Severity.Critical;
        }
        return new(true, severity, qualifying.Count, databases, analysis);
    }

    private static EngineState Open(EngineState state, LockEvaluation eval, DateTimeOffset t, IncidentSettings settings, List<IncidentEvent> events)
    {
        var previous = state.Incident;
        IncidentInfo incident;
        if (previous?.ClosedAt is { } closedAt && t - closedAt <= settings.ReopenWindow)
        {
            incident = previous with
            {
                ClosedAt = null,
                Severity = Max(previous.Severity, eval.Severity),
                Databases = previous.Databases.Union(eval.AffectedDatabases),
            };
            events.Add(new(IncidentEventKind.Reopened, incident.Id, t, incident.Severity, Describe(eval)));
        }
        else
        {
            var id = state.LastIncidentId + 1;
            incident = new IncidentInfo
            {
                Id = id,
                OpenedAt = t,
                Severity = eval.Severity,
                Databases = eval.AffectedDatabases.ToImmutableSortedSet(StringComparer.Ordinal),
                Signals = [SignalKind.Lock],
            };
            state = state with { LastIncidentId = id };
            events.Add(new(IncidentEventKind.Opened, id, t, incident.Severity, Describe(eval)));
        }

        return state with { Phase = IncidentPhase.Active, Incident = incident, ClearSince = null, RecoveringSince = null };
    }

    /// <summary>Applies a triggering sample to an open incident: severity only rises, new databases are announced.</summary>
    private static EngineState Grow(EngineState state, LockEvaluation eval, DateTimeOffset t, List<IncidentEvent> events)
    {
        var incident = state.Incident!;
        foreach (var db in eval.AffectedDatabases.Where(d => !incident.Databases.Contains(d)))
        {
            incident = incident with { Databases = incident.Databases.Add(db) };
            events.Add(new(IncidentEventKind.DatabaseAffected, incident.Id, t, incident.Severity, $"Lock waits now also in database '{db}'"));
        }
        if (eval.Severity > incident.Severity)
        {
            incident = incident with { Severity = eval.Severity };
            events.Add(new(IncidentEventKind.SeverityEscalated, incident.Id, t, incident.Severity, Describe(eval)));
        }
        return state with { Incident = incident };
    }

    private static IncidentEvent Event(IncidentEventKind kind, EngineState state, DateTimeOffset t, string detail) =>
        new(kind, state.Incident!.Id, t, state.Incident.Severity, detail);

    private static Severity Max(Severity a, Severity b) => a >= b ? a : b;

    private static string Describe(LockEvaluation e) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{e.Analysis.WaiterCount} session(s) waiting on locks, {e.QualifyingWaiters} over threshold, longest {Format(e.Analysis.LongestWait)}, " +
            $"{e.Analysis.RootBlockers.Count} root blocker(s), databases: {string.Join(", ", e.Analysis.AffectedDatabases)}");

    internal static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h{span.Minutes:00}m")
            : span.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m{span.Seconds:00}s")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds}s");
}
