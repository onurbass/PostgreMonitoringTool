using PgMonitor.Core.Model;

namespace PgMonitor.Tests;

/// <summary>Fictional test data only (see design/TEST_PLAN.md section 1).</summary>
internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static SessionRow Idle(int pid, string db = "app_orchid_db") => new()
    {
        Pid = pid,
        BackendStart = T0.AddHours(-1),
        Database = db,
        UserName = "svc_orchid",
        ApplicationName = "PostgreSQL JDBC Driver",
        ClientAddress = "192.0.2.10",
        State = "idle",
    };

    /// <summary>A session waiting on a heavyweight lock for <paramref name="waiting"/> as of <paramref name="now"/>.</summary>
    public static SessionRow LockWaiter(int pid, DateTimeOffset now, TimeSpan waiting, string db = "app_orchid_db", params int[] blockedBy) =>
        Idle(pid, db) with
        {
            State = "active",
            WaitEventType = "Lock",
            WaitEvent = "transactionid",
            TransactionStart = now - waiting - TimeSpan.FromSeconds(1),
            QueryStart = now - waiting,
            StateChange = now - waiting,
            Query = "UPDATE \"app$customer\" SET \"changeddate\" = $1 WHERE \"id\" = $2",
            BlockingPids = blockedBy,
        };

    public static SessionRow IdleInTx(int pid, DateTimeOffset now, TimeSpan age, string db = "app_orchid_db") =>
        Idle(pid, db) with
        {
            State = "idle in transaction",
            WaitEventType = "Client",
            WaitEvent = "ClientRead",
            TransactionStart = now - age,
            QueryStart = now - age,
            BackendXid = "123456",
            Query = "SELECT 1",
        };

    public static ClusterSample Sample(DateTimeOffset at, params SessionRow[] sessions) => new()
    {
        SampledAt = at,
        Sessions = sessions,
        Visibility = Visibility.Full,
        Server = new ServerIdentity(150006, T0.AddDays(-10), "198.51.100.5", false),
    };

    public static ClusterSample Healthy(DateTimeOffset at) => Sample(at, Idle(100), Idle(101));

    /// <summary>Root blocker 100 (idle in transaction) blocking <paramref name="waiters"/> sessions for <paramref name="waiting"/>.</summary>
    public static ClusterSample Blocked(DateTimeOffset at, TimeSpan waiting, int waiters = 1, string db = "app_orchid_db")
    {
        var rows = new List<SessionRow> { IdleInTx(100, at, waiting + TimeSpan.FromMinutes(1), db) };
        for (var i = 0; i < waiters; i++)
        {
            rows.Add(LockWaiter(200 + i, at, waiting, db, 100));
        }
        return Sample(at, [.. rows]);
    }
}
