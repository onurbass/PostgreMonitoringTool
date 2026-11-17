using PgMonitor.Core.Model;
using PgMonitor.Core.Simulation;

namespace PgMonitor.Tests;

/// <summary>Self-checks of the simulator, so the fake data can be trusted (TEST_PLAN 3).</summary>
public class SimulatorTests
{
    private static List<ClusterSample> All(Scenario s)
    {
        var sim = new Simulator(s);
        var list = new List<ClusterSample>();
        while (sim.HasNext)
        {
            list.Add(sim.Next());
        }
        return list;
    }

    [Fact]
    public void Same_seed_gives_identical_samples()
    {
        var a = All(Scenarios.S05LockChain);
        var b = All(Scenarios.S05LockChain);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].SampledAt, b[i].SampledAt);
            Assert.Equal(a[i].Sessions, b[i].Sessions, SessionComparer.Instance);
        }
    }

    [Fact]
    public void Different_seed_gives_different_noise()
    {
        var a = All(Scenarios.S02SingleBlip);
        var b = All(Scenarios.S02SingleBlip with { Seed = 7 });
        Assert.NotEqual(a.Select(s => s.Sessions.Count(r => r.State == "active")), b.Select(s => s.Sessions.Count(r => r.State == "active")));
    }

    public static TheoryData<string> ScenarioIds() => new(Scenarios.All.Select(s => s.Id));

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Samples_are_internally_consistent(string id)
    {
        var scenario = Scenarios.All.Single(s => s.Id == id);
        var sim = new Simulator(scenario);
        DateTimeOffset? previous = null;
        while (sim.HasNext)
        {
            var sample = sim.Next();
            Assert.True(previous is null || sample.SampledAt > previous, "time moves forward");
            previous = sample.SampledAt;
            if (!sample.Succeeded)
            {
                Assert.Empty(sample.Sessions);
                continue;
            }

            var t = sample.SampledAt;
            Assert.Equal(sample.Sessions.Count, sample.Sessions.Select(s => s.Key).Distinct().Count());
            Assert.Equal(sample.Sessions.Count, sample.Sessions.Select(s => s.Pid).Distinct().Count());
            foreach (var r in sample.Sessions)
            {
                if (r.IsRedacted)
                {
                    Assert.Null(r.State);
                    Assert.Null(r.WaitEventType);
                    Assert.Equal("<insufficient privilege>", r.Query);
                    continue;
                }
                Assert.True(r.BackendStart <= t, "backend_start not in the future");
                Assert.True(r.QueryStart is null || r.QueryStart <= t, "query_start not in the future");
                Assert.True(r.TransactionStart is null || r.TransactionStart <= t, "xact_start not in the future");
                if (r.TransactionStart is { } x && r.State is "active" or "idle in transaction")
                {
                    Assert.True(x <= r.QueryStart, "xact_start <= query_start");
                }
                if (r.State == "idle")
                {
                    Assert.Null(r.TransactionStart);
                    Assert.NotEqual("Lock", r.WaitEventType);
                    Assert.NotEqual("LWLock", r.WaitEventType);
                }
                if (!r.IsWaitingOnLock)
                {
                    Assert.Empty(r.BlockingPids);
                }
            }
        }
    }

    [Fact]
    public void Laptop_sleep_produces_no_samples()
    {
        var samples = All(Scenarios.S07DisconnectAndSleep);
        var start = Scenarios.S07DisconnectAndSleep.Start;
        Assert.DoesNotContain(samples, s => s.SampledAt >= start + TimeSpan.FromMinutes(9.5) && s.SampledAt < start + TimeSpan.FromMinutes(29.5));
        Assert.Contains(samples, s => s.Failure == CollectFailureKind.Disconnected);
    }

    [Fact]
    public void Pid_reuse_keeps_sessions_apart_by_backend_start()
    {
        var keysByPid = All(Scenarios.S08RestartAndPidReuse)
            .SelectMany(s => s.Sessions)
            .GroupBy(r => r.Pid)
            .Select(g => g.Select(r => r.BackendStart).Distinct().Count());
        Assert.Contains(keysByPid, n => n > 1);
    }

    [Fact]
    public void Restart_changes_postmaster_start()
    {
        var starts = All(Scenarios.S08RestartAndPidReuse).Select(s => s.Server!.PostmasterStart).Distinct().ToList();
        Assert.Equal(2, starts.Count);
    }

    [Fact]
    public void Fixtures_use_only_fictional_names_and_documentation_ips()
    {
        var sessions = All(Scenarios.S05LockChain).SelectMany(s => s.Sessions).ToList();
        Assert.All(sessions, r => Assert.StartsWith("app_", r.Database, StringComparison.Ordinal));
        Assert.All(sessions, r => Assert.True(
            r.ClientAddress!.StartsWith("192.0.2.", StringComparison.Ordinal) || r.ClientAddress.StartsWith("198.51.100.", StringComparison.Ordinal)));
    }

    private sealed class SessionComparer : IEqualityComparer<SessionRow>
    {
        public static readonly SessionComparer Instance = new();

        public bool Equals(SessionRow? x, SessionRow? y) =>
            x is not null && y is not null &&
            x with { BlockingPids = [] } == y with { BlockingPids = [] } &&
            x.BlockingPids.SequenceEqual(y.BlockingPids);

        public int GetHashCode(SessionRow obj) => obj.Pid;
    }
}
