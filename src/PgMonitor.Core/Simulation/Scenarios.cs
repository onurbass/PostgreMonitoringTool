namespace PgMonitor.Core.Simulation;

/// <summary>
/// Built-in lock scenarios (design/TEST_PLAN.md section 2). S03 (Subtrans) is added with the Subtrans signal in M9.
/// Database index 0 = app_orchid_db, 1 = app_lotus_db, 2 = app_cedar_db.
/// </summary>
public static class Scenarios
{
    private static TimeSpan Min(double m) => TimeSpan.FromMinutes(m);

    public static Scenario S01HealthyDay { get; } = new()
    {
        Id = "S01",
        Title = "Healthy 24 h: churn, short transactions, occasional short lock waits",
        Duration = TimeSpan.FromHours(24),
    };

    public static Scenario S02SingleBlip { get; } = new()
    {
        Id = "S02",
        Title = "One sample shows a 40 s lock wait, then it is gone",
        LockChains =
        [
            new LockChain { From = Min(10), To = Min(10) + TimeSpan.FromSeconds(5), Waiters = 1, InitialWait = TimeSpan.FromSeconds(40), TerminatedAtEnd = false },
        ],
    };

    public static Scenario S04Flapping { get; } = new()
    {
        Id = "S04",
        Title = "Lock waits come and go for 30 min (60 s on, 45 s off)",
        Duration = Min(40),
        LockChains = Enumerable.Range(0, 17)
            .Select(i => new LockChain
            {
                From = Min(1) + TimeSpan.FromSeconds(105 * i),
                To = Min(1) + TimeSpan.FromSeconds(105 * i + 60),
                Waiters = 2,
                TerminatedAtEnd = false,
            })
            .ToList(),
    };

    public static Scenario S05LockChain { get; } = new()
    {
        Id = "S05",
        Title = "Idle-in-transaction root blocker, 3 direct and 5 indirect waiters, DBA terminates at 15 min",
        Duration = Min(25),
        LockChains =
        [
            new LockChain { From = Min(5), To = Min(15), Waiters = 3, SecondLevelWaiters = 5 },
        ],
    };

    public static Scenario S06PartialVisibility { get; } = new()
    {
        Id = "S06",
        Title = "Only svc_orchid rows are visible; a hidden chain in app_lotus_db, then a visible one in app_orchid_db",
        LockChains =
        [
            new LockChain { From = Min(5), To = Min(10), DatabaseIndex = 1, Waiters = 3 },
            new LockChain { From = Min(15), To = Min(20), DatabaseIndex = 0, Waiters = 3 },
        ],
        Visibility = [new PartialVisibility(TimeSpan.Zero, Min(30))],
    };

    public static Scenario S07DisconnectAndSleep { get; } = new()
    {
        Id = "S07",
        Title = "Incident, connection lost 6-8 min (DBA cleans up meanwhile), laptop sleeps 9:30-29:30",
        Duration = Min(40),
        LockChains =
        [
            new LockChain { From = Min(2), To = Min(7), Waiters = 4 },
        ],
        Outages = [new Outage(Min(6), Min(8))],
        ClockJumps = [new ClockJump(Min(9.5), Min(20))],
    };

    public static Scenario S08RestartAndPidReuse { get; } = new()
    {
        Id = "S08",
        Title = "Short lock wait terminated with PID reuse, PostgreSQL restart at 60 min",
        Duration = TimeSpan.FromHours(2),
        LockChains =
        [
            new LockChain { From = Min(20), To = Min(20) + TimeSpan.FromSeconds(25), Waiters = 2, ReusePidOnReconnect = true },
        ],
        Restarts = [new RestartAt(Min(60))],
    };

    public static IReadOnlyList<Scenario> All { get; } =
        [S01HealthyDay, S02SingleBlip, S04Flapping, S05LockChain, S06PartialVisibility, S07DisconnectAndSleep, S08RestartAndPidReuse];
}
