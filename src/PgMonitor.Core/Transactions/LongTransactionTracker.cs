using PgMonitor.Core.Model;

namespace PgMonitor.Core.Transactions;

/// <summary>
/// Identity of one transaction: the session (pid + backend_start, because PIDs are reused) plus its xact_start
/// (a session runs many transactions over its life).
/// </summary>
public readonly record struct LongTxKey(int Pid, DateTimeOffset? BackendStart, DateTimeOffset TransactionStart)
{
    public string Text => FormattableString.Invariant($"{Pid}:{BackendStart?.ToUnixTimeMilliseconds()}:{TransactionStart.ToUnixTimeMilliseconds()}");
}

public enum LongTxEventKind
{
    /// <summary>The transaction crossed the threshold for the first time.</summary>
    Started,
    /// <summary>Its state, wait or current statement changed (throttled).</summary>
    Changed,
    /// <summary>It is no longer seen.</summary>
    Ended,
}

/// <param name="Session">The row as last seen (for Ended: the last sample in which it was present).</param>
/// <param name="EndReason">Derived, for Ended only: the session vanished or the transaction finished.</param>
public sealed record LongTxEvent(
    LongTxEventKind Kind,
    LongTxKey Key,
    DateTimeOffset At,
    SessionRow Session,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? EndReason);

/// <summary>A transaction currently over the threshold, for the dashboard.</summary>
public sealed record LongTransaction(SessionRow Session, DateTimeOffset FirstSeen, TimeSpan TransactionAge, TimeSpan? StateAge, TimeSpan? QueryAge);

/// <summary>
/// Follows transactions that stay open longer than a threshold, whether or not they block anyone (an advisory signal:
/// it never opens an incident). Pure apart from its own memory: no clock, no I/O; time comes from the samples.
/// Failed samples change nothing, so a network gap never "ends" a transaction.
/// </summary>
public sealed class LongTransactionTracker
{
    public const string ReasonSessionGone = "session kayboldu (bitti veya sonlandırıldı – ayırt edilemez)";
    public const string ReasonTransactionEnded = "transaction bitti (session devam ediyor)";

    private readonly Dictionary<LongTxKey, Entry> _open = [];

    public IReadOnlyList<LongTransaction> Current { get; private set; } = [];

    public IReadOnlyList<LongTxEvent> Update(ClusterSample sample, TimeSpan threshold, TimeSpan changeEvery)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!sample.Succeeded)
        {
            return [];
        }

        var t = sample.SampledAt;
        var events = new List<LongTxEvent>();
        var seen = new HashSet<LongTxKey>();

        foreach (var row in sample.Sessions)
        {
            if (row.IsRedacted || row.TransactionStart is not { } xs || t - xs < threshold)
            {
                continue;
            }
            var key = new LongTxKey(row.Pid, row.BackendStart, xs);
            seen.Add(key);
            if (!_open.TryGetValue(key, out var entry))
            {
                _open[key] = new Entry(row, t, t, t);
                events.Add(new LongTxEvent(LongTxEventKind.Started, key, t, row, t, t, null));
                continue;
            }

            var changed = !string.Equals(entry.Row.State, row.State, StringComparison.Ordinal)
                          || !string.Equals(entry.Row.WaitEvent, row.WaitEvent, StringComparison.Ordinal)
                          || entry.Row.QueryStart != row.QueryStart;
            var updated = entry with { Row = row, LastSeen = t };
            if (changed && t - entry.LastEmitted >= changeEvery)
            {
                updated = updated with { LastEmitted = t };
                events.Add(new LongTxEvent(LongTxEventKind.Changed, key, t, row, entry.FirstSeen, t, null));
            }
            _open[key] = updated;
        }

        foreach (var (key, entry) in _open.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            var sessionStillThere = sample.Sessions.Any(r => r.Pid == key.Pid && r.BackendStart == key.BackendStart);
            events.Add(new LongTxEvent(LongTxEventKind.Ended, key, t, entry.Row, entry.FirstSeen, entry.LastSeen,
                sessionStillThere ? ReasonTransactionEnded : ReasonSessionGone));
            _open.Remove(key);
        }

        Current = _open.Values
            .Select(e => new LongTransaction(e.Row, e.FirstSeen, t - e.Row.TransactionStart!.Value,
                e.Row.StateChange is { } sc ? t - sc : null, e.Row.QueryStart is { } qs ? t - qs : null))
            .OrderByDescending(x => x.TransactionAge)
            .ToList();
        return events;
    }

    private sealed record Entry(SessionRow Row, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, DateTimeOffset LastEmitted);
}
