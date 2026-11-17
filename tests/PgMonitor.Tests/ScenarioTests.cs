using PgMonitor.Core.Incidents;
using PgMonitor.Core.Locks;
using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;

namespace PgMonitor.Tests;

/// <summary>End-to-end engine behaviour on the built-in scenarios (TEST_PLAN 2).</summary>
public class ScenarioTests
{
    private static DateTimeOffset At(Scenario s, double minutes) => s.Start + TimeSpan.FromMinutes(minutes);

    [Fact]
    public void S01_healthy_day_has_no_incident()
    {
        var run = ScenarioRunner.Run(Scenarios.S01HealthyDay);
        Assert.Equal(17_280, run.SampleCount);
        Assert.Empty(run.Events);
    }

    [Fact]
    public void S01_noise_contains_short_lock_waits_that_never_trigger()
    {
        var noiseWaits = 0;
        ScenarioRunner.Run(Scenarios.S01HealthyDay with { Duration = TimeSpan.FromHours(2) }, observe: (sample, result) =>
        {
            noiseWaits += sample.Sessions.Count(s => s.IsWaitingOnLock);
            Assert.False(result.Lock!.Triggered);
        });
        Assert.True(noiseWaits > 10, "the healthy scenario should not be trivially empty");
    }

    [Fact]
    public void S02_single_blip_opens_nothing()
    {
        var triggeredSamples = 0;
        var run = ScenarioRunner.Run(Scenarios.S02SingleBlip, observe: (_, r) => triggeredSamples += r.Lock!.Triggered ? 1 : 0);
        Assert.Equal(1, triggeredSamples);
        Assert.Equal(0, run.Count(IncidentEventKind.Opened));
    }

    [Fact]
    public void S04_flapping_is_one_incident()
    {
        var run = ScenarioRunner.Run(Scenarios.S04Flapping);
        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
        Assert.Equal(0, run.Count(IncidentEventKind.Reopened));
        Assert.Equal(1, run.Count(IncidentEventKind.Closed));
        Assert.Equal(IncidentPhase.Healthy, run.FinalState.Phase);
    }

    [Fact]
    public void S05_lock_chain_finds_root_blocker_and_closes_after_cleanup()
    {
        var s = Scenarios.S05LockChain;
        RootBlocker? biggest = null;
        int? rootPidAtStart = null;
        var run = ScenarioRunner.Run(s, observe: (sample, result) =>
        {
            if (sample.SampledAt == At(s, 5))
            {
                rootPidAtStart = sample.Sessions.Single(r => r.State == "idle in transaction" && r.BackendXid == "731002").Pid;
            }
            var roots = result.Lock?.Analysis.RootBlockers;
            var root = roots is { Count: > 0 } ? roots[0] : null;
            if (root is not null && (biggest is null || root.BlockedCount > biggest.BlockedCount))
            {
                biggest = root;
            }
        });

        Assert.NotNull(biggest);
        Assert.Equal(rootPidAtStart, biggest.Pid);
        Assert.Equal(8, biggest.BlockedCount);
        Assert.Equal(2, biggest.ChainDepth);
        Assert.Equal("idle in transaction", biggest.Session!.State);

        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
        Assert.Equal(Severity.High, run.FinalState.Incident!.Severity);
        var closed = Assert.Single(run.Events, e => e.Kind == IncidentEventKind.Closed);
        Assert.True(closed.At >= At(s, 15) + TimeSpan.FromMinutes(3), "closes only after 60 s clear + 2 min post-recovery");
    }

    [Fact]
    public void S06_partial_visibility_is_reported_and_hidden_chain_is_not_invented()
    {
        var s = Scenarios.S06PartialVisibility;
        var visibilities = new HashSet<Visibility>();
        var redacted = 0;
        var run = ScenarioRunner.Run(s, observe: (sample, _) =>
        {
            visibilities.Add(sample.Visibility);
            redacted += sample.Sessions.Count(r => r.IsRedacted);
        });

        Assert.Equal([Visibility.Partial], visibilities);
        Assert.True(redacted > 0);
        var opened = Assert.Single(run.Events, e => e.Kind == IncidentEventKind.Opened);
        Assert.True(opened.At >= At(s, 15), "the hidden app_lotus_db chain cannot be seen; only the visible one opens");
        Assert.Equal(["app_orchid_db"], run.FinalState.Incident!.Databases);
    }

    [Fact]
    public void S07_disconnect_and_sleep_never_count_as_recovery()
    {
        var s = Scenarios.S07DisconnectAndSleep;
        var run = ScenarioRunner.Run(s);

        Assert.Equal(1, run.Count(IncidentEventKind.Opened));
        Assert.Equal(2, run.Count(IncidentEventKind.GapStarted));
        Assert.Equal(2, run.Count(IncidentEventKind.GapEnded));

        var recovery = Assert.Single(run.Events, e => e.Kind == IncidentEventKind.RecoveryStarted);
        Assert.True(recovery.At >= At(s, 9), "recovery needs 60 s of fresh samples after the outage ends at 8 min");

        var closed = Assert.Single(run.Events, e => e.Kind == IncidentEventKind.Closed);
        Assert.True(closed.At >= At(s, 29.5) + TimeSpan.FromMinutes(2), "post-recovery capture restarts after the sleep");
    }

    [Fact]
    public void S08_restart_is_reported_and_short_wait_with_pid_reuse_opens_nothing()
    {
        var run = ScenarioRunner.Run(Scenarios.S08RestartAndPidReuse);
        Assert.Equal(1, run.Count(IncidentEventKind.ServerChanged));
        Assert.Equal(0, run.Count(IncidentEventKind.Opened));
    }

    [Fact]
    public void Scenario_runs_are_deterministic()
    {
        var a = ScenarioRunner.Run(Scenarios.S04Flapping).Events;
        var b = ScenarioRunner.Run(Scenarios.S04Flapping).Events;
        Assert.Equal(a, b);
    }
}
