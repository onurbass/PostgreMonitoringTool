using PgMonitor.Core.Locks;
using static PgMonitor.Tests.TestData;

namespace PgMonitor.Tests;

public class LockGraphTests
{
    private static readonly DateTimeOffset Now = T0;

    [Fact]
    public void No_waiters_gives_empty_analysis()
    {
        var a = LockGraph.Analyze([Idle(1), Idle(2)], Now);
        Assert.Equal(0, a.WaiterCount);
        Assert.Empty(a.RootBlockers);
        Assert.Equal(TimeSpan.Zero, a.LongestWait);
    }

    [Fact]
    public void Single_chain_root_is_idle_in_transaction_blocker()
    {
        var a = LockGraph.Analyze(
        [
            IdleInTx(100, Now, TimeSpan.FromMinutes(5)),
            LockWaiter(200, Now, TimeSpan.FromSeconds(40), blockedBy: 100),
            LockWaiter(201, Now, TimeSpan.FromSeconds(10), blockedBy: 100),
        ], Now);

        var root = Assert.Single(a.RootBlockers);
        Assert.Equal(100, root.Pid);
        Assert.Equal(2, root.BlockedCount);
        Assert.Equal(1, root.ChainDepth);
        Assert.Equal("idle in transaction", root.Session!.State);
        Assert.Equal(TimeSpan.FromSeconds(40), a.LongestWait);
    }

    [Fact]
    public void Two_level_chain_reports_depth_two_and_only_the_top_as_root()
    {
        // 100 <- 200 <- 300, 301
        var a = LockGraph.Analyze(
        [
            IdleInTx(100, Now, TimeSpan.FromMinutes(5)),
            LockWaiter(200, Now, TimeSpan.FromMinutes(2), blockedBy: 100),
            LockWaiter(300, Now, TimeSpan.FromMinutes(1), blockedBy: 200),
            LockWaiter(301, Now, TimeSpan.FromMinutes(1), blockedBy: 200),
        ], Now);

        var root = Assert.Single(a.RootBlockers);
        Assert.Equal(100, root.Pid);
        Assert.Equal(3, root.BlockedCount);
        Assert.Equal(2, root.ChainDepth);
    }

    [Fact]
    public void Duplicate_and_self_pids_are_ignored()
    {
        var a = LockGraph.Analyze(
        [
            IdleInTx(100, Now, TimeSpan.FromMinutes(5)),
            LockWaiter(200, Now, TimeSpan.FromSeconds(40), blockedBy: [100, 100, 200]),
        ], Now);

        Assert.Equal([100], a.BlockedBy[200]);
        Assert.Equal(1, Assert.Single(a.RootBlockers).BlockedCount);
    }

    [Fact]
    public void Pid_zero_is_a_prepared_transaction_root()
    {
        var a = LockGraph.Analyze([LockWaiter(200, Now, TimeSpan.FromSeconds(40), blockedBy: 0)], Now);

        var root = Assert.Single(a.RootBlockers);
        Assert.True(root.IsPreparedTransaction);
        Assert.Null(root.Session);
    }

    [Fact]
    public void Blocker_missing_from_sample_is_still_a_root_without_details()
    {
        var a = LockGraph.Analyze([LockWaiter(200, Now, TimeSpan.FromSeconds(40), blockedBy: 999)], Now);

        var root = Assert.Single(a.RootBlockers);
        Assert.Equal(999, root.Pid);
        Assert.Null(root.Session);
    }

    [Fact]
    public void Cycle_does_not_hang_and_yields_a_root()
    {
        var a = LockGraph.Analyze(
        [
            LockWaiter(200, Now, TimeSpan.FromSeconds(5), blockedBy: 201),
            LockWaiter(201, Now, TimeSpan.FromSeconds(5), blockedBy: 200),
        ], Now);

        var root = Assert.Single(a.RootBlockers);
        Assert.Equal(200, root.Pid);
        Assert.Equal(1, root.BlockedCount);
    }

    [Fact]
    public void Roots_are_ordered_by_blocked_count()
    {
        var a = LockGraph.Analyze(
        [
            IdleInTx(100, Now, TimeSpan.FromMinutes(5)),
            IdleInTx(101, Now, TimeSpan.FromMinutes(9), db: "app_lotus_db"),
            LockWaiter(200, Now, TimeSpan.FromSeconds(40), blockedBy: 100),
            LockWaiter(300, Now, TimeSpan.FromSeconds(40), db: "app_lotus_db", blockedBy: 101),
            LockWaiter(301, Now, TimeSpan.FromSeconds(40), db: "app_lotus_db", blockedBy: 101),
        ], Now);

        Assert.Equal([101, 100], a.RootBlockers.Select(r => r.Pid));
        Assert.Equal(["app_lotus_db", "app_orchid_db"], a.AffectedDatabases);
    }
}
