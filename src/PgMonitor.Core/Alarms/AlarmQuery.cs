using System.Globalization;
using PgMonitor.Core.Model;

namespace PgMonitor.Core.Alarms;

/// <summary>
/// One alarm level of the company SQL alarm: a waiter must have waited at least <see cref="WaiterMinWait"/> and the
/// session that blocks it must have been in its current state at least <see cref="BlockerMinState"/>.
/// </summary>
public sealed record AlarmTier(string Name, TimeSpan WaiterMinWait, TimeSpan BlockerMinState)
{
    /// <summary>Early warning (lower severity, governance team).</summary>
    public static AlarmTier Early { get; } = new("Erken uyarı", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));

    /// <summary>Critical (SCC is notified). Stays below the apps' 5-minute kill script so the alarm fires before it.</summary>
    public static AlarmTier Critical { get; } = new("Kritik", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180));
}

/// <summary>One result row of the alarm SQL: one row per database of the blocking session.</summary>
public sealed record AlarmRow(string Database, int Blockers, int Waiters, int MaxStateSeconds, int MaxWaitSeconds);

/// <summary>
/// The SQL the monitoring team runs on a schedule (company alarm form), plus an equivalent in C# that evaluates the same
/// rule on a <see cref="ClusterSample"/>. This application NEVER sends the alarm SQL to a server: it is not part of
/// SqlCatalog. The C# version exists for the demo preview, for scenario tests and as a cross-check against live data.
/// It mirrors the SQL; it does not prove the SQL itself (that needs a real PostgreSQL, see docs/ALARM_REQUEST.md).
/// </summary>
public static class AlarmQuery
{
    private const string Template = """
        SELECT b.datname                                            AS veritabani,
               count(DISTINCT b.pid)                                AS bloklayan_session,
               count(DISTINCT w.pid)                                AS bekleyen_session,
               max(extract(epoch FROM now() - b.state_change))::int AS en_uzun_durum_sn,
               max(extract(epoch FROM now() - w.query_start))::int  AS en_uzun_bekleme_sn
        FROM pg_stat_activity w
        CROSS JOIN LATERAL unnest(pg_blocking_pids(w.pid)) AS bp(pid)
        JOIN pg_stat_activity b ON b.pid = bp.pid
        WHERE w.wait_event_type = 'Lock'
          AND w.query_start < now() - interval '{W} seconds'
          AND b.wait_event_type IS DISTINCT FROM 'Lock'
          AND b.state_change < now() - interval '{B} seconds'
        GROUP BY b.datname
        """;

    /// <summary>The SQL text for the form, with the thresholds of <paramref name="tier"/>.</summary>
    public static string Sql(AlarmTier tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return Template
            .Replace("{W}", Seconds(tier.WaiterMinWait), StringComparison.Ordinal)
            .Replace("{B}", Seconds(tier.BlockerMinState), StringComparison.Ordinal);
    }

    /// <summary>
    /// What the SQL would return for this sample. Rows the account cannot see (redacted) are invisible to the SQL as well,
    /// so they are skipped here too. A waiter is counted when a direct blocker qualifies; a blocker that itself waits for a
    /// lock (middle of a chain) is excluded, so only the top of each chain and its direct waiters are counted.
    /// </summary>
    public static IReadOnlyList<AlarmRow> Evaluate(ClusterSample sample, AlarmTier tier)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(tier);
        if (!sample.Succeeded)
        {
            return [];
        }

        var now = sample.SampledAt;
        var waiterCutoff = now - tier.WaiterMinWait;
        var blockerCutoff = now - tier.BlockerMinState;
        var byPid = new Dictionary<int, SessionRow>();
        foreach (var s in sample.Sessions)
        {
            byPid.TryAdd(s.Pid, s);
        }

        var pairs = new List<(SessionRow Waiter, SessionRow Blocker, DateTimeOffset WaitStart, DateTimeOffset StateStart)>();
        foreach (var w in sample.Sessions)
        {
            if (w.IsRedacted || !w.IsWaitingOnLock || w.QueryStart is not { } waitStart || waitStart >= waiterCutoff)
            {
                continue;
            }
            foreach (var pid in w.BlockingPids)
            {
                if (!byPid.TryGetValue(pid, out var b) || b.IsRedacted)
                {
                    continue; // pid 0 (prepared transaction) or not visible: the SQL join finds nothing either
                }
                // IS DISTINCT FROM 'Lock': a blocker with no wait event (NULL) passes.
                if (string.Equals(b.WaitEventType, "Lock", StringComparison.Ordinal))
                {
                    continue;
                }
                if (b.StateChange is not { } stateStart || stateStart >= blockerCutoff)
                {
                    continue;
                }
                pairs.Add((w, b, waitStart, stateStart));
            }
        }

        return pairs
            .GroupBy(p => p.Blocker.Database ?? "(bilinmiyor)", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AlarmRow(
                g.Key,
                g.Select(p => p.Blocker.Pid).Distinct().Count(),
                g.Select(p => p.Waiter.Pid).Distinct().Count(),
                g.Max(p => Rounded(now - p.StateStart)),
                g.Max(p => Rounded(now - p.WaitStart))))
            .ToList();
    }

    private static string Seconds(TimeSpan t) => ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>PostgreSQL's ::int on a numeric rounds half away from zero.</summary>
    private static int Rounded(TimeSpan t) => (int)Math.Round(t.TotalSeconds, MidpointRounding.AwayFromZero);
}
