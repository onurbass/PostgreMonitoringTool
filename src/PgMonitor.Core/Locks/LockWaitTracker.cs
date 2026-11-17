using PgMonitor.Core.Model;

namespace PgMonitor.Core.Locks;

/// <summary>Identity of one waiting statement: the session plus the start of the statement that waits.</summary>
public readonly record struct LockWaitKey(int Pid, DateTimeOffset? BackendStart, DateTimeOffset QueryStart)
{
    public string Text => FormattableString.Invariant($"{Pid}:{BackendStart?.ToUnixTimeMilliseconds()}:{QueryStart.ToUnixTimeMilliseconds()}");
}

public enum LockWaitEventKind
{
    Started,
    /// <summary>Periodic update while it lasts (at most once per period): latest chain and last-seen time.</summary>
    Changed,
    Ended,
}

/// <param name="Waiter">The waiting session as last seen.</param>
/// <param name="Blockers">Distinct direct blockers (pg_blocking_pids); empty if not looked up (over the per-sample limit).</param>
/// <param name="Root">Top of the chain above this waiter, if it could be followed; null for a prepared transaction or unknown.</param>
/// <param name="RootPid">Root pid even when its row is not in the sample (0 = prepared transaction).</param>
public sealed record LockWaitEvent(
    LockWaitEventKind Kind,
    LockWaitKey Key,
    DateTimeOffset At,
    SessionRow Waiter,
    IReadOnlyList<int> Blockers,
    int? RootPid,
    SessionRow? Root,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? EndReason);

/// <summary>
/// Records every heavyweight-lock wait that reaches a minimum duration (default 5 s), including the short ones that never
/// open an incident: who waited, who blocked, the root of the chain and when it ended. Pure apart from its own memory.
/// Failed samples change nothing. A wait shorter than the sampling interval can be missed.
/// </summary>
public sealed class LockWaitTracker
{
    public const string ReasonWaitEnded = "bekleme bitti (lock alındı ya da sorgu bitti)";
    public const string ReasonSessionGone = "session kayboldu (bitti veya sonlandırıldı – ayırt edilemez)";

    private readonly Dictionary<LockWaitKey, Entry> _open = [];

    public IReadOnlyList<LockWaitEvent> Update(ClusterSample sample, LockAnalysis analysis, TimeSpan minimumWait, TimeSpan changeEvery)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(analysis);
        if (!sample.Succeeded)
        {
            return [];
        }

        var t = sample.SampledAt;
        var byPid = sample.Sessions.GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.First());
        var events = new List<LockWaitEvent>();
        var seen = new HashSet<LockWaitKey>();

        foreach (var w in analysis.Waiters)
        {
            if (w.QueryStart is not { } qs || t - qs < minimumWait)
            {
                continue;
            }
            var key = new LockWaitKey(w.Pid, w.BackendStart, qs);
            seen.Add(key);
            var blockers = analysis.BlockedBy.TryGetValue(w.Pid, out var b) ? b : [];
            var rootPid = FindRoot(w.Pid, analysis.BlockedBy);
            var root = rootPid is { } rp ? byPid.GetValueOrDefault(rp) : null;

            if (!_open.TryGetValue(key, out var entry))
            {
                _open[key] = new Entry(w, blockers, rootPid, root, t, t, t);
                events.Add(new LockWaitEvent(LockWaitEventKind.Started, key, t, w, blockers, rootPid, root, t, t, null));
                continue;
            }

            var updated = entry with { Waiter = w, LastSeen = t, Blockers = blockers.Count > 0 ? blockers : entry.Blockers, RootPid = rootPid ?? entry.RootPid, Root = root ?? entry.Root };
            // Periodic update (latest chain and last-seen time) so the stored wait duration keeps up while it lasts.
            if (t - entry.LastEmitted >= changeEvery)
            {
                updated = updated with { LastEmitted = t };
                events.Add(new LockWaitEvent(LockWaitEventKind.Changed, key, t, w, updated.Blockers, updated.RootPid, updated.Root, entry.FirstSeen, t, null));
            }
            _open[key] = updated;
        }

        foreach (var (key, e) in _open.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            var stillThere = sample.Sessions.Any(r => r.Pid == key.Pid && r.BackendStart == key.BackendStart);
            events.Add(new LockWaitEvent(LockWaitEventKind.Ended, key, t, e.Waiter, e.Blockers, e.RootPid, e.Root, e.FirstSeen, e.LastSeen,
                stillThere ? ReasonWaitEnded : ReasonSessionGone));
            _open.Remove(key);
        }
        return events;
    }

    /// <summary>Follows "blocked by" upwards (first blocker each step) to a pid that is not itself blocked. Cycle-safe.</summary>
    internal static int? FindRoot(int pid, IReadOnlyDictionary<int, IReadOnlyList<int>> blockedBy)
    {
        if (!blockedBy.TryGetValue(pid, out var first) || first.Count == 0)
        {
            return null;
        }
        var visited = new HashSet<int> { pid };
        var current = first[0];
        while (blockedBy.TryGetValue(current, out var next) && next.Count > 0 && visited.Add(current))
        {
            current = next[0];
        }
        return current;
    }

    private sealed record Entry(SessionRow Waiter, IReadOnlyList<int> Blockers, int? RootPid, SessionRow? Root,
        DateTimeOffset FirstSeen, DateTimeOffset LastSeen, DateTimeOffset LastEmitted);
}
