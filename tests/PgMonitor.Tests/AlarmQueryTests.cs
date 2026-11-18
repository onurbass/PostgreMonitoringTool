using System.Text.RegularExpressions;
using PgMonitor.Core.Alarms;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

/// <summary>
/// The company alarm SQL and its C# mirror. The mirror is checked against hand-made samples and, minute by minute,
/// against the built-in scenarios (the monitoring system polls once a minute).
/// </summary>
public partial class AlarmQueryTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    // ------------------------------------------------------------------ the SQL text

    [Theory]
    [InlineData(30, 120)]
    [InlineData(60, 180)]
    public void Sql_carries_the_tier_thresholds_and_is_read_only(int waiter, int blocker)
    {
        var tier = new AlarmTier("t", TimeSpan.FromSeconds(waiter), TimeSpan.FromSeconds(blocker));
        var sql = AlarmQuery.Sql(tier);

        Assert.Contains($"w.query_start < now() - interval '{waiter} seconds'", sql, StringComparison.Ordinal);
        Assert.Contains($"b.state_change < now() - interval '{blocker} seconds'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("{", sql, StringComparison.Ordinal);

        Assert.Equal("SELECT", FirstWord().Match(sql).Value.ToUpperInvariant());
        Assert.DoesNotContain(";", sql, StringComparison.Ordinal);
        var withoutLiterals = Literal().Replace(sql, "''");
        foreach (var word in new[]
                 {
                     "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "COPY", "DO", "CALL", "SET", "LOCK",
                     "pg_terminate_backend", "pg_cancel_backend", "set_config", "pg_stat_reset", "pg_advisory", "pg_sleep",
                 })
        {
            Assert.False(Regex.IsMatch(withoutLiterals, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase), $"forbidden token {word}");
        }
    }

    [Fact]
    public void Alarm_sql_is_not_part_of_the_sql_the_application_can_run()
    {
        Assert.DoesNotContain(PgMonitor.Infrastructure.Postgres.SqlCatalog.All.Values, v => v.Contains("en_uzun_durum_sn", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\s*\w+")]
    private static partial Regex FirstWord();

    [GeneratedRegex("'(?:[^']|'')*'")]
    private static partial Regex Literal();

    // ------------------------------------------------------------------ the mirror on hand-made samples

    private static SessionRow Blocker(int pid, DateTimeOffset now, TimeSpan stateAge, string db = "app_orchid_db", string state = "idle in transaction") =>
        Idle(pid, db) with
        {
            State = state,
            WaitEventType = state == "active" ? null : "Client",
            WaitEvent = state == "active" ? null : "ClientRead",
            TransactionStart = now - stateAge - TimeSpan.FromMinutes(1),
            QueryStart = now - stateAge,
            StateChange = now - stateAge,
        };

    [Fact]
    public void Idle_in_transaction_blocker_over_the_limit_gives_one_row_per_database()
    {
        var s = Sample(T0,
            Blocker(100, T0, TimeSpan.FromMinutes(5)),
            LockWaiter(200, T0, TimeSpan.FromSeconds(40), blockedBy: 100),
            LockWaiter(201, T0, TimeSpan.FromSeconds(45), blockedBy: 100));

        var row = Assert.Single(AlarmQuery.Evaluate(s, AlarmTier.Standard));

        Assert.Equal(new AlarmRow("app_orchid_db", 1, 2, 300, 45), row);
    }

    [Fact]
    public void Blocker_that_is_not_old_enough_or_waiter_that_has_not_waited_long_enough_gives_nothing()
    {
        var young = Sample(T0, Blocker(100, T0, TimeSpan.FromSeconds(119)), LockWaiter(200, T0, TimeSpan.FromSeconds(60), blockedBy: 100));
        var brief = Sample(T0, Blocker(100, T0, TimeSpan.FromMinutes(5)), LockWaiter(200, T0, TimeSpan.FromSeconds(29), blockedBy: 100));
        Assert.Empty(AlarmQuery.Evaluate(young, AlarmTier.Standard));
        Assert.Empty(AlarmQuery.Evaluate(brief, AlarmTier.Standard));
    }

    [Fact]
    public void Thresholds_are_strict_like_the_sql_comparison()
    {
        var atLimit = Sample(T0, Blocker(100, T0, TimeSpan.FromSeconds(120)), LockWaiter(200, T0, TimeSpan.FromSeconds(30), blockedBy: 100));
        var over = Sample(T0, Blocker(100, T0, TimeSpan.FromSeconds(121)), LockWaiter(200, T0, TimeSpan.FromSeconds(31), blockedBy: 100));
        Assert.Empty(AlarmQuery.Evaluate(atLimit, AlarmTier.Standard));
        Assert.Single(AlarmQuery.Evaluate(over, AlarmTier.Standard));
    }

    [Fact]
    public void Only_the_top_of_a_chain_and_its_direct_waiters_are_counted()
    {
        // 100 <- 200 <- 300: 200 waits for a lock itself, so it is not a blocker row; 300 points at 200 and is not counted.
        var s = Sample(T0,
            Blocker(100, T0, TimeSpan.FromMinutes(5)),
            LockWaiter(200, T0, TimeSpan.FromSeconds(90), blockedBy: 100),
            LockWaiter(300, T0, TimeSpan.FromSeconds(60), blockedBy: 200));

        var row = Assert.Single(AlarmQuery.Evaluate(s, AlarmTier.Standard));
        Assert.Equal(1, row.Blockers);
        Assert.Equal(1, row.Waiters);
    }

    [Fact]
    public void Active_long_running_blocker_without_a_wait_event_counts_too()
    {
        var s = Sample(T0,
            Blocker(100, T0, TimeSpan.FromMinutes(4), state: "active"),
            LockWaiter(200, T0, TimeSpan.FromSeconds(40), blockedBy: 100));
        Assert.Single(AlarmQuery.Evaluate(s, AlarmTier.Standard)); // wait_event_type NULL passes IS DISTINCT FROM 'Lock'
    }

    [Fact]
    public void Blockers_in_two_databases_give_two_rows_named_after_the_blocker_database()
    {
        var s = Sample(T0,
            Blocker(100, T0, TimeSpan.FromMinutes(5), "app_orchid_db"),
            LockWaiter(200, T0, TimeSpan.FromSeconds(40), "app_orchid_db", 100),
            Blocker(300, T0, TimeSpan.FromMinutes(9), "app_lotus_db"),
            LockWaiter(400, T0, TimeSpan.FromSeconds(50), "app_lotus_db", 300));

        Assert.Equal(["app_lotus_db", "app_orchid_db"], AlarmQuery.Evaluate(s, AlarmTier.Standard).Select(r => r.Database));
    }

    [Fact]
    public void Redacted_prepared_transaction_and_missing_blockers_are_invisible_to_the_sql()
    {
        var hidden = Blocker(100, T0, TimeSpan.FromMinutes(5)) with { IsRedacted = true };
        var s = Sample(T0,
            hidden,
            LockWaiter(200, T0, TimeSpan.FromSeconds(40), blockedBy: 100),   // blocker row is redacted
            LockWaiter(201, T0, TimeSpan.FromSeconds(40), blockedBy: 0),     // prepared transaction
            LockWaiter(202, T0, TimeSpan.FromSeconds(40), blockedBy: 999));  // not in the sample
        Assert.Empty(AlarmQuery.Evaluate(s, AlarmTier.Standard));
    }

    [Fact]
    public void Failed_sample_gives_nothing()
    {
        Assert.Empty(AlarmQuery.Evaluate(ClusterSample.Failed(T0, CollectFailureKind.Timeout), AlarmTier.Standard));
    }

    // ------------------------------------------------------------------ polling the scenarios once a minute

    private static List<(TimeSpan At, IReadOnlyList<AlarmRow> Rows)> PollScenario(Scenario scenario, AlarmTier tier)
    {
        var sim = new Simulator(scenario);
        var results = new List<(TimeSpan, IReadOnlyList<AlarmRow>)>();
        while (sim.HasNext)
        {
            var sample = sim.Next();
            var offset = sample.SampledAt - scenario.Start;
            if (sample.Succeeded && offset.Ticks % Poll.Ticks == 0)
            {
                results.Add((offset, AlarmQuery.Evaluate(sample, tier)));
            }
        }
        return results;
    }

    private static List<int> FiringMinutes(Scenario s, AlarmTier tier) =>
        PollScenario(s, tier).Where(p => p.Rows.Count > 0).Select(p => (int)p.At.TotalMinutes).ToList();

    [Fact]
    public void S01_healthy_day_never_fires_either_level()
    {
        Assert.Empty(FiringMinutes(Scenarios.S01HealthyDay, AlarmTier.Standard));
    }

    [Fact]
    public void S05_lock_chain_fires_at_minute_6_while_the_blocker_is_far_from_the_5_minute_kill_limit_and_stops_after_the_cleanup()
    {
        var s = Scenarios.S05LockChain;

        Assert.Equal(Enumerable.Range(6, 9), FiringMinutes(s, AlarmTier.Standard)); // minutes 6..14; the DBA cleanup is at 15

        var first = PollScenario(s, AlarmTier.Standard).First(p => p.Rows.Count > 0).Rows.Single();
        Assert.Equal("app_orchid_db", first.Database);
        Assert.Equal(1, first.Blockers);
        Assert.Equal(3, first.Waiters);             // the 5 second-level waiters wait for a waiter, not for the root
        Assert.Equal(149, first.MaxStateSeconds);
        Assert.Equal(60, first.MaxWaitSeconds);
        Assert.True(first.MaxStateSeconds < 300, "the alarm comes before the apps' own 5-minute kill script acts");
    }

    [Fact]
    public void S02_single_blip_fires_once()
    {
        Assert.Equal([10], FiringMinutes(Scenarios.S02SingleBlip, AlarmTier.Standard));
    }

    [Fact]
    public void S04_flapping_fires_in_isolated_minutes_only_so_two_checks_in_a_row_would_suppress_all_of_them()
    {
        var fired = FiringMinutes(Scenarios.S04Flapping, AlarmTier.Standard);
        Assert.Equal([7, 14, 21, 28], fired);
        Assert.DoesNotContain(fired, m => fired.Contains(m + 1)); // never two consecutive polls
    }

    [Fact]
    public void S06_partial_visibility_hides_the_chain_of_the_other_user_and_the_alarm_stays_silent()
    {
        var fired = FiringMinutes(Scenarios.S06PartialVisibility, AlarmTier.Standard);
        Assert.DoesNotContain(fired, m => m is >= 5 and < 10);   // hidden chain in the other user's database
        Assert.Equal(16, fired[0]);                              // the visible chain (15-20 min) is found
    }
}
