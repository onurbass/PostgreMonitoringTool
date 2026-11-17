using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

public class IncidentEngineTests
{
    private static readonly IncidentSettings Settings = new();
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(45);

    private sealed class Run
    {
        public EngineState State { get; private set; } = EngineState.Initial;
        public List<IncidentEvent> Events { get; } = [];
        public DateTimeOffset Now { get; private set; } = T0;

        public Run Feed(ClusterSample sample)
        {
            var r = IncidentEngine.Process(State, sample, Settings);
            State = r.State;
            Events.AddRange(r.Events);
            Now = sample.SampledAt;
            return this;
        }

        /// <summary>Feeds <paramref name="count"/> samples, one every 5 s, built by <paramref name="make"/>.</summary>
        public Run Repeat(int count, Func<DateTimeOffset, ClusterSample> make)
        {
            for (var i = 0; i < count; i++)
            {
                Now += Step;
                Feed(make(Now));
            }
            return this;
        }

        public Run For(TimeSpan duration, Func<DateTimeOffset, ClusterSample> make) =>
            Repeat((int)(duration.Ticks / Step.Ticks), make);

        public int Count(IncidentEventKind kind) => Events.Count(e => e.Kind == kind);
    }

    // ---------- Trigger thresholds

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, true)]
    public void Lock_trigger_threshold_is_inclusive(int seconds, bool expected)
    {
        var eval = IncidentEngine.EvaluateLock(Blocked(T0, TimeSpan.FromSeconds(seconds)), Settings);
        Assert.Equal(expected, eval.Triggered);
    }

    [Fact]
    public void Many_short_lock_waits_do_not_trigger()
    {
        var eval = IncidentEngine.EvaluateLock(Blocked(T0, TimeSpan.FromSeconds(3), waiters: 50), Settings);
        Assert.False(eval.Triggered);
    }

    [Theory]
    [InlineData(1, 45, Severity.Warning)]
    [InlineData(5, 45, Severity.High)]
    [InlineData(1, 120, Severity.High)]
    [InlineData(20, 45, Severity.Critical)]
    public void Severity_follows_waiter_count_and_wait_time(int waiters, int seconds, Severity expected)
    {
        var eval = IncidentEngine.EvaluateLock(Blocked(T0, TimeSpan.FromSeconds(seconds), waiters), Settings);
        Assert.Equal(expected, eval.Severity);
    }

    [Fact]
    public void Waiters_in_two_databases_are_critical()
    {
        var sample = Sample(T0,
            IdleInTx(100, T0, TimeSpan.FromMinutes(5)),
            LockWaiter(200, T0, Long, "app_orchid_db", 100),
            LockWaiter(300, T0, Long, "app_lotus_db", 100));
        Assert.Equal(Severity.Critical, IncidentEngine.EvaluateLock(sample, Settings).Severity);
    }

    // ---------- Opening and deduplication

    [Fact]
    public void Healthy_samples_never_open_an_incident()
    {
        var run = new Run().For(TimeSpan.FromHours(1), Healthy);
        Assert.Empty(run.Events);
        Assert.Equal(IncidentPhase.Healthy, run.State.Phase);
    }

    [Fact]
    public void Single_sample_blip_does_not_open()
    {
        var run = new Run().Repeat(3, Healthy).Repeat(1, t => Blocked(t, Long)).Repeat(10, Healthy);
        Assert.Equal(0, run.Count(IncidentEventKind.Opened));
    }

    [Fact]
    public void Two_of_three_samples_open_an_incident()
    {
        var run = new Run()
            .Repeat(1, t => Blocked(t, Long))
            .Repeat(1, Healthy)
            .Repeat(1, t => Blocked(t, Long));
        var opened = Assert.Single(run.Events, e => e.Kind == IncidentEventKind.Opened);
        Assert.Equal(1, opened.IncidentId);
        Assert.Equal(IncidentPhase.Active, run.State.Phase);
    }

    [Fact]
    public void Continuous_trigger_for_30_minutes_is_one_incident()
    {
        var run = new Run().For(TimeSpan.FromMinutes(30), t => Blocked(t, Long));
        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
        Assert.Equal(IncidentPhase.Active, run.State.Phase);
    }

    // ---------- Growth

    [Fact]
    public void Severity_escalates_but_never_falls()
    {
        var run = new Run()
            .Repeat(2, t => Blocked(t, Long))                 // Warning
            .Repeat(2, t => Blocked(t, Long, waiters: 6))     // High
            .Repeat(5, t => Blocked(t, Long));                // back to Warning-level samples
        Assert.Equal(1, run.Count(IncidentEventKind.SeverityEscalated));
        Assert.Equal(Severity.High, run.State.Incident!.Severity);
    }

    [Fact]
    public void New_database_is_a_scope_event_not_a_new_incident()
    {
        var run = new Run()
            .Repeat(2, t => Blocked(t, Long))
            .Repeat(2, t => Sample(t,
                IdleInTx(100, t, TimeSpan.FromMinutes(5)),
                LockWaiter(200, t, Long, "app_orchid_db", 100),
                LockWaiter(300, t, Long, "app_lotus_db", 100)));

        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
        var scope = Assert.Single(run.Events, e => e.Kind == IncidentEventKind.DatabaseAffected);
        Assert.Contains("app_lotus_db", scope.Detail, StringComparison.Ordinal);
        Assert.Equal(["app_lotus_db", "app_orchid_db"], run.State.Incident!.Databases);
    }

    // ---------- Recovery

    [Fact]
    public void Recovery_needs_60s_clear_then_closes_after_post_recovery()
    {
        var run = new Run().Repeat(2, t => Blocked(t, Long));
        run.For(TimeSpan.FromSeconds(60), Healthy); // 12 clear samples: 55 s since the first one
        Assert.Equal(0, run.Count(IncidentEventKind.RecoveryStarted));

        run.Repeat(1, Healthy); // 60 s since the first clear sample
        Assert.Equal(1, run.Count(IncidentEventKind.RecoveryStarted));
        Assert.Equal(IncidentPhase.Recovering, run.State.Phase);

        run.For(TimeSpan.FromMinutes(2), Healthy);
        Assert.Equal(1, run.Count(IncidentEventKind.Closed));
        Assert.Equal(IncidentPhase.Healthy, run.State.Phase);
        Assert.NotNull(run.State.Incident!.ClosedAt);
    }

    [Fact]
    public void Trigger_during_recovery_reactivates_same_incident()
    {
        var run = new Run().Repeat(2, t => Blocked(t, Long)).For(TimeSpan.FromSeconds(65), Healthy);
        Assert.Equal(IncidentPhase.Recovering, run.State.Phase);

        run.Repeat(1, t => Blocked(t, Long));
        Assert.Equal(1, run.Count(IncidentEventKind.Reactivated));
        Assert.Equal(IncidentPhase.Active, run.State.Phase);
        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
    }

    [Fact]
    public void Trigger_within_reopen_window_reopens_same_incident()
    {
        var run = new Run()
            .Repeat(2, t => Blocked(t, Long))
            .For(TimeSpan.FromMinutes(4), Healthy)       // closed
            .For(TimeSpan.FromMinutes(5), Healthy)
            .Repeat(2, t => Blocked(t, Long));

        Assert.Equal(1, run.Count(IncidentEventKind.Closed));
        Assert.Equal(1, run.Count(IncidentEventKind.Reopened));
        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
        Assert.Equal(1, run.State.Incident!.Id);
    }

    [Fact]
    public void Trigger_after_reopen_window_opens_a_new_incident()
    {
        var run = new Run()
            .Repeat(2, t => Blocked(t, Long))
            .For(TimeSpan.FromMinutes(4), Healthy)
            .For(TimeSpan.FromMinutes(11), Healthy)
            .Repeat(2, t => Blocked(t, Long));

        Assert.Equal(2, run.Count(IncidentEventKind.Opened));
        Assert.Equal(2, run.State.Incident!.Id);
    }

    // ---------- Gaps and blindness

    [Fact]
    public void Failed_samples_never_count_as_recovery()
    {
        var run = new Run().Repeat(2, t => Blocked(t, Long));
        run.For(TimeSpan.FromMinutes(10), t => ClusterSample.Failed(t, CollectFailureKind.Disconnected));

        Assert.Equal(IncidentPhase.Active, run.State.Phase);
        Assert.Equal(1, run.Count(IncidentEventKind.GapStarted));
        Assert.Equal(0, run.Count(IncidentEventKind.RecoveryStarted));
    }

    [Fact]
    public void After_a_gap_recovery_needs_fresh_clear_samples()
    {
        var run = new Run().Repeat(2, t => Blocked(t, Long)).For(TimeSpan.FromSeconds(50), Healthy);
        run.Repeat(1, t => ClusterSample.Failed(t, CollectFailureKind.Timeout));
        run.Repeat(1, Healthy); // gap ends; clear clock restarts here
        Assert.Equal(1, run.Count(IncidentEventKind.GapEnded));
        Assert.Equal(IncidentPhase.Active, run.State.Phase);

        run.For(TimeSpan.FromSeconds(55), Healthy);
        Assert.Equal(IncidentPhase.Active, run.State.Phase);
        run.Repeat(1, Healthy);
        Assert.Equal(IncidentPhase.Recovering, run.State.Phase);
    }

    [Fact]
    public void Laptop_sleep_between_successful_samples_is_a_gap()
    {
        var run = new Run().Repeat(2, t => Blocked(t, Long)).For(TimeSpan.FromSeconds(50), Healthy);
        var afterSleep = run.Now + TimeSpan.FromMinutes(40);
        run.Feed(Healthy(afterSleep));

        Assert.Equal(1, run.Count(IncidentEventKind.GapStarted));
        Assert.Equal(1, run.Count(IncidentEventKind.GapEnded));
        Assert.Equal(IncidentPhase.Active, run.State.Phase); // no recovery across the sleep
    }

    [Fact]
    public void OwnOnly_visibility_clear_samples_do_not_recover()
    {
        var run = new Run().Repeat(2, t => Blocked(t, Long));
        run.For(TimeSpan.FromMinutes(5), t => Healthy(t) with { Visibility = Visibility.OwnOnly });
        Assert.Equal(IncidentPhase.Active, run.State.Phase);
    }

    [Fact]
    public void Server_restart_is_reported()
    {
        var run = new Run().Repeat(1, Healthy);
        var restarted = Healthy(run.Now + Step) with
        {
            Server = new ServerIdentity(150006, T0, "198.51.100.5", false),
        };
        run.Feed(restarted);
        Assert.Equal(1, run.Count(IncidentEventKind.ServerChanged));
    }

    [Fact]
    public void Engine_is_deterministic()
    {
        static List<IncidentEvent> Go() => new Run()
            .Repeat(2, t => Blocked(t, Long, waiters: 3))
            .Repeat(3, t => Blocked(t, Long, waiters: 7))
            .For(TimeSpan.FromMinutes(4), Healthy)
            .Events;

        Assert.Equal(Go(), Go());
    }
}
