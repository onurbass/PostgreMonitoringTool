using PgMonitor.Core.Model;

namespace PgMonitor.Core.Locks;

/// <summary>A blocker at the top of a lock chain: it blocks others and is not itself blocked by a lock.</summary>
/// <param name="Pid">Blocker PID; 0 means a prepared transaction (no session).</param>
/// <param name="Session">The blocker's row, or null for a prepared transaction or a session not visible in the sample.</param>
/// <param name="BlockedCount">Number of distinct sessions waiting on it, directly or indirectly.</param>
/// <param name="ChainDepth">Longest chain below it (1 = only direct waiters).</param>
public sealed record RootBlocker(int Pid, SessionRow? Session, int BlockedCount, int ChainDepth)
{
    public bool IsPreparedTransaction => Pid == PreparedTransactionPid;

    public const int PreparedTransactionPid = 0;
}

/// <summary>Result of analysing heavyweight-lock waits in one sample. Everything here is Observed or Derived, not guessed.</summary>
public sealed record LockAnalysis(
    IReadOnlyList<SessionRow> Waiters,
    IReadOnlyDictionary<int, IReadOnlyList<int>> BlockedBy,
    IReadOnlyList<RootBlocker> RootBlockers,
    TimeSpan LongestWait,
    IReadOnlyList<string> AffectedDatabases)
{
    public int WaiterCount => Waiters.Count;
}

/// <summary>
/// Builds blocking chains from pg_blocking_pids() results.
/// Handles: duplicate PIDs (parallel query), a session reported as blocking itself (parallel workers on relation
/// extension), PID 0 (prepared transaction), blockers that are missing from the sample, and cycles.
/// </summary>
public static class LockGraph
{
    public static LockAnalysis Analyze(IReadOnlyList<SessionRow> sessions, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var byPid = new Dictionary<int, SessionRow>();
        foreach (var s in sessions)
        {
            byPid[s.Pid] = s; // pid is unique within one sample
        }

        var waiters = sessions.Where(s => s.IsWaitingOnLock).ToList();

        // Edges: waiter pid -> distinct blocker pids (self-references removed).
        var blockedBy = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var w in waiters)
        {
            var blockers = w.BlockingPids.Where(p => p != w.Pid).Distinct().Order().ToList();
            if (blockers.Count > 0)
            {
                blockedBy[w.Pid] = blockers;
            }
        }

        // Reverse edges: blocker -> direct waiters.
        var waitersOf = new Dictionary<int, List<int>>();
        foreach (var (waiter, blockers) in blockedBy)
        {
            foreach (var b in blockers)
            {
                if (!waitersOf.TryGetValue(b, out var list))
                {
                    waitersOf[b] = list = [];
                }
                list.Add(waiter);
            }
        }

        // Roots: blockers that are not blocked themselves.
        var roots = waitersOf.Keys.Where(b => !blockedBy.ContainsKey(b)).ToList();

        // Cycles (should be broken quickly by the deadlock detector, but a sample can catch one):
        // if some blocker is not reachable from any root, pick the lowest pid of each such group as a root.
        var reachable = new HashSet<int>();
        foreach (var r in roots)
        {
            Walk(r, waitersOf, reachable);
        }
        foreach (var b in waitersOf.Keys.Order())
        {
            if (!reachable.Contains(b))
            {
                roots.Add(b);
                Walk(b, waitersOf, reachable);
            }
        }

        var rootBlockers = roots
            .Select(r =>
            {
                var (count, depth) = Measure(r, waitersOf);
                return new RootBlocker(r, byPid.GetValueOrDefault(r), count, depth);
            })
            .OrderByDescending(r => r.BlockedCount)
            .ThenByDescending(r => r.ChainDepth)
            .ThenBy(r => r.Session?.TransactionStart ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Pid)
            .ToList();

        var longest = waiters
            .Select(w => w.QueryAge(now) ?? TimeSpan.Zero)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();

        var databases = waiters
            .Select(w => w.Database)
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new LockAnalysis(waiters, blockedBy, rootBlockers, longest, databases);
    }

    private static void Walk(int start, Dictionary<int, List<int>> waitersOf, HashSet<int> seen)
    {
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p))
            {
                continue;
            }
            if (waitersOf.TryGetValue(p, out var next))
            {
                foreach (var n in next)
                {
                    stack.Push(n);
                }
            }
        }
    }

    /// <summary>Distinct transitive waiters below <paramref name="root"/> and the longest chain length (BFS, cycle-safe).</summary>
    private static (int Count, int Depth) Measure(int root, Dictionary<int, List<int>> waitersOf)
    {
        var seen = new HashSet<int> { root };
        var frontier = new List<int> { root };
        var depth = 0;
        while (true)
        {
            var next = new List<int>();
            foreach (var p in frontier)
            {
                if (waitersOf.TryGetValue(p, out var ws))
                {
                    next.AddRange(ws.Where(seen.Add));
                }
            }
            if (next.Count == 0)
            {
                break;
            }
            depth++;
            frontier = next;
        }
        return (seen.Count - 1, depth);
    }
}
