using System.Globalization;
using PgMonitor.Core.Model;

namespace PgMonitor.Core.Simulation;

/// <summary>
/// Deterministic generator of fictional pg_stat_activity samples. Same scenario + seed = same samples.
/// It advances its own virtual clock (one <see cref="Scenario.Step"/> per call) and never reads the system clock.
/// </summary>
public sealed class Simulator
{
    private static readonly string[] Databases = ["app_orchid_db", "app_lotus_db", "app_cedar_db"];
    private static readonly string[] Users = ["svc_orchid", "svc_lotus", "svc_cedar"];
    private static readonly string[] Entities = ["orchid$customer", "lotus$order", "cedar$invoice"];
    private const int PoolSize = 25;

    /// <summary>Slots 0..14 are reserved for scripted lock chains; background noise uses the rest.</summary>
    private const int FirstNoiseSlot = 15;

    private readonly Scenario _scenario;
    private readonly Random _rng;
    private readonly SimSession[][] _pool;
    private int _nextPid = 20_000;
    private long _index;
    private DateTimeOffset? _last;
    private DateTimeOffset _postmasterStart;

    public Simulator(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.LockChains.Any(c => c.Waiters + c.SecondLevelWaiters + 1 > FirstNoiseSlot))
        {
            throw new ArgumentException("A lock chain can involve at most 15 sessions.", nameof(scenario));
        }

        _scenario = scenario;
        _rng = new Random(scenario.Seed);
        _postmasterStart = scenario.Start.AddDays(-30);
        _pool = new SimSession[Databases.Length][];
        for (var db = 0; db < Databases.Length; db++)
        {
            _pool[db] = new SimSession[PoolSize];
            for (var slot = 0; slot < PoolSize; slot++)
            {
                _pool[db][slot] = new SimSession(_nextPid++, scenario.Start.AddHours(-2).AddSeconds(db * 100 + slot));
            }
        }
    }

    public Scenario Scenario => _scenario;

    public DateTimeOffset End => _scenario.Start + _scenario.Duration;

    /// <summary>Time of the next sample, skipping laptop-sleep periods; null when the scenario is over.</summary>
    public DateTimeOffset? PeekNextTime()
    {
        var i = _index;
        while (true)
        {
            var t = _scenario.Start + _scenario.Step * i;
            if (t >= End)
            {
                return null;
            }
            if (!IsAsleep(t))
            {
                return t;
            }
            i++;
        }
    }

    public bool HasNext => PeekNextTime() is not null;

    public ClusterSample Next()
    {
        DateTimeOffset t;
        while (true)
        {
            t = _scenario.Start + _scenario.Step * _index++;
            if (t >= End)
            {
                throw new InvalidOperationException("The scenario has no more samples.");
            }
            if (!IsAsleep(t))
            {
                break;
            }
        }

        ApplyLifecycle(t);
        _last = t;

        var outage = _scenario.Outages.FirstOrDefault(o => In(t, o.From, o.To));
        if (outage is not null)
        {
            return ClusterSample.Failed(t, outage.Kind);
        }

        var rows = new Dictionary<(int Db, int Slot), SessionRow>();
        for (var db = 0; db < Databases.Length; db++)
        {
            for (var slot = 0; slot < PoolSize; slot++)
            {
                rows[(db, slot)] = Noise(db, slot, t);
            }
        }

        AddNoiseLockWait(rows, t);

        foreach (var chain in _scenario.LockChains.Where(c => In(t, c.From, c.To)))
        {
            ApplyChain(chain, rows, t);
        }

        var visibility = Visibility.Full;
        var partial = _scenario.Visibility.FirstOrDefault(v => In(t, v.From, v.To));
        IEnumerable<SessionRow> result = rows.Values;
        if (partial is not null)
        {
            visibility = Visibility.Partial;
            result = result.Select(r => string.Equals(r.UserName, partial.VisibleUser, StringComparison.Ordinal) ? r : Redact(r));
        }

        return new ClusterSample
        {
            SampledAt = t,
            Sessions = result.OrderBy(r => r.Pid).ToList(),
            Visibility = visibility,
            Server = new ServerIdentity(150010, _postmasterStart, "198.51.100.5", false),
        };
    }

    // ------------------------------------------------------------------ lifecycle

    private void ApplyLifecycle(DateTimeOffset t)
    {
        var from = _last ?? DateTimeOffset.MinValue;

        foreach (var restart in _scenario.Restarts)
        {
            var at = _scenario.Start + restart.At;
            if (at > from && at <= t)
            {
                _postmasterStart = at;
                _nextPid = 30_000 + (int)(restart.At.TotalSeconds % 1000);
                for (var db = 0; db < Databases.Length; db++)
                {
                    for (var slot = 0; slot < PoolSize; slot++)
                    {
                        _pool[db][slot] = new SimSession(_nextPid++, at);
                    }
                }
            }
        }

        foreach (var chain in _scenario.LockChains.Where(c => c.TerminatedAtEnd))
        {
            var end = _scenario.Start + chain.To;
            if (end > from && end <= t)
            {
                var involved = 1 + chain.Waiters + chain.SecondLevelWaiters;
                for (var slot = 0; slot < involved; slot++)
                {
                    var old = _pool[chain.DatabaseIndex][slot];
                    var pid = chain.ReusePidOnReconnect ? old.Pid : _nextPid++;
                    _pool[chain.DatabaseIndex][slot] = new SimSession(pid, end);
                }
            }
        }
    }

    // ------------------------------------------------------------------ rows

    private SessionRow Base(int db, int slot) => new()
    {
        Pid = _pool[db][slot].Pid,
        BackendStart = _pool[db][slot].BackendStart,
        Database = Databases[db],
        UserName = Users[db],
        ApplicationName = "PostgreSQL JDBC Driver",
        ClientAddress = db == 2 ? "198.51.100.20" : string.Create(CultureInfo.InvariantCulture, $"192.0.2.{10 + db * 30}"),
    };

    private SessionRow Noise(int db, int slot, DateTimeOffset t)
    {
        var row = Base(db, slot);
        var roll = _rng.NextDouble();
        if (roll < 0.80)
        {
            var queryStart = Max(t - TimeSpan.FromSeconds(1 + _rng.Next(600)), row.BackendStart!.Value);
            return row with
            {
                State = "idle",
                WaitEventType = "Client",
                WaitEvent = "ClientRead",
                QueryStart = queryStart,
                StateChange = queryStart.AddMilliseconds(40),
                Query = SelectSql(db),
            };
        }
        if (roll < 0.95)
        {
            var queryStart = t - TimeSpan.FromMilliseconds(_rng.Next(1500));
            var xactStart = queryStart - TimeSpan.FromMilliseconds(_rng.Next(2000));
            var io = _rng.NextDouble() < 0.1;
            return row with
            {
                State = "active",
                WaitEventType = io ? "IO" : null,
                WaitEvent = io ? "DataFileRead" : null,
                TransactionStart = xactStart,
                QueryStart = queryStart,
                StateChange = queryStart,
                Query = SelectSql(db),
            };
        }
        var txStart = t - TimeSpan.FromMilliseconds(500 + _rng.Next(2500));
        return row with
        {
            State = "idle in transaction",
            WaitEventType = "Client",
            WaitEvent = "ClientRead",
            TransactionStart = txStart,
            QueryStart = txStart.AddMilliseconds(100),
            StateChange = txStart.AddMilliseconds(150),
            BackendXid = Xid(),
            Query = UpdateSql(db),
        };
    }

    private void AddNoiseLockWait(Dictionary<(int Db, int Slot), SessionRow> rows, DateTimeOffset t)
    {
        if (_rng.NextDouble() >= _scenario.NoiseLockProbability)
        {
            return;
        }
        var db = _rng.Next(Databases.Length);
        var blockerSlot = FirstNoiseSlot + _rng.Next(PoolSize - FirstNoiseSlot);
        var waiterSlot = FirstNoiseSlot + ((blockerSlot - FirstNoiseSlot + 1 + _rng.Next(PoolSize - FirstNoiseSlot - 1)) % (PoolSize - FirstNoiseSlot));
        var waited = TimeSpan.FromMilliseconds(500 + _rng.Next(8000));

        var blocker = Base(db, blockerSlot);
        rows[(db, blockerSlot)] = blocker with
        {
            State = "active",
            TransactionStart = t - waited - TimeSpan.FromSeconds(1),
            QueryStart = t - TimeSpan.FromMilliseconds(200),
            StateChange = t - TimeSpan.FromMilliseconds(200),
            BackendXid = Xid(),
            Query = UpdateSql(db),
        };
        rows[(db, waiterSlot)] = Waiter(db, waiterSlot, t - waited, blocker.Pid, "transactionid");
    }

    private void ApplyChain(LockChain chain, Dictionary<(int Db, int Slot), SessionRow> rows, DateTimeOffset t)
    {
        var db = chain.DatabaseIndex;
        var from = _scenario.Start + chain.From;
        var root = Base(db, 0);
        var rootActive = string.Equals(chain.RootState, "active", StringComparison.Ordinal);
        var rootTx = Min(from - TimeSpan.FromMinutes(2) - chain.InitialWait, t);
        rows[(db, 0)] = root with
        {
            State = chain.RootState,
            WaitEventType = rootActive ? null : "Client",
            WaitEvent = rootActive ? null : "ClientRead",
            TransactionStart = Max(rootTx, root.BackendStart!.Value),
            QueryStart = rootActive ? Max(rootTx, root.BackendStart!.Value) : Max(from - TimeSpan.FromSeconds(90) - chain.InitialWait, root.BackendStart!.Value),
            StateChange = rootActive ? Max(rootTx, root.BackendStart!.Value) : Max(from - TimeSpan.FromSeconds(89) - chain.InitialWait, root.BackendStart!.Value),
            BackendXid = "731002",
            Query = UpdateSql(db),
        };

        var firstWaiterPid = _pool[db][1].Pid;
        for (var i = 0; i < chain.Waiters + chain.SecondLevelWaiters; i++)
        {
            var queryStart = from + chain.WaiterStagger * i - chain.InitialWait;
            if (queryStart > t)
            {
                break;
            }
            var slot = 1 + i;
            var secondLevel = i >= chain.Waiters;
            rows[(db, slot)] = Waiter(db, slot, Max(queryStart, _pool[db][slot].BackendStart), secondLevel ? firstWaiterPid : root.Pid, secondLevel ? "tuple" : "transactionid");
        }
    }

    private SessionRow Waiter(int db, int slot, DateTimeOffset queryStart, int blockedBy, string waitEvent)
    {
        queryStart = Max(queryStart, _pool[db][slot].BackendStart);
        return Base(db, slot) with
        {
            State = "active",
            WaitEventType = "Lock",
            WaitEvent = waitEvent,
            TransactionStart = Max(queryStart - TimeSpan.FromMilliseconds(300), _pool[db][slot].BackendStart),
            QueryStart = queryStart,
            StateChange = queryStart,
            BackendXmin = "731002",
            Query = UpdateSql(db),
            BlockingPids = [blockedBy],
        };
    }

    private static SessionRow Redact(SessionRow r) => new()
    {
        Pid = r.Pid,
        Database = r.Database,
        UserName = r.UserName,
        ApplicationName = r.ApplicationName,
        Query = "<insufficient privilege>",
        IsRedacted = true,
    };

    // ------------------------------------------------------------------ helpers

    private string Xid() => (700_000 + _rng.Next(30_000)).ToString(CultureInfo.InvariantCulture);

    private static string SelectSql(int db) =>
        $"SELECT \"{Entities[db]}\".\"id\", \"{Entities[db]}\".\"name\" FROM \"{Entities[db]}\" WHERE \"{Entities[db]}\".\"id\" = $1";

    private static string UpdateSql(int db) =>
        $"UPDATE \"{Entities[db]}\" SET \"changeddate\" = $1 WHERE \"id\" = $2";

    private bool IsAsleep(DateTimeOffset t) =>
        _scenario.ClockJumps.Any(j => In(t, j.At, j.At + j.Length));

    private bool In(DateTimeOffset t, TimeSpan from, TimeSpan to) =>
        t >= _scenario.Start + from && t < _scenario.Start + to;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private sealed record SimSession(int Pid, DateTimeOffset BackendStart);
}
