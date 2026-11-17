using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;

namespace PgMonitor.Core.Aggregation;

/// <summary>Compact health metrics for one time window (default 60 s). This is all we store while the cluster is healthy.</summary>
public sealed record AggregateRow
{
    public required DateTimeOffset WindowStart { get; init; }
    public required int Samples { get; init; }
    public required int FailedSamples { get; init; }
    public required int MaxSessions { get; init; }
    public required int MaxActive { get; init; }
    public required int MaxIdleInTransaction { get; init; }
    public required int MaxLockWaiters { get; init; }
    public required int MaxLockWaitersOverThreshold { get; init; }
    public required double MaxLockWaitSeconds { get; init; }
    public required double MaxTransactionAgeSeconds { get; init; }

    /// <summary>Worst visibility seen in the window (Unknown when every sample failed).</summary>
    public required Visibility WorstVisibility { get; init; }

    /// <summary>Highest session count per database in the window.</summary>
    public required IReadOnlyDictionary<string, int> MaxSessionsByDatabase { get; init; }
}

/// <summary>
/// Folds samples into fixed windows aligned to the sample clock. Returns a finished row when a sample
/// falls into a new window. Pure: no clock, no I/O.
/// </summary>
public sealed class MinuteAggregator
{
    private readonly TimeSpan _window;
    private Accumulator? _current;

    public MinuteAggregator(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromMinutes(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_window, TimeSpan.Zero);
    }

    /// <summary>Adds a sample; returns the previous window's row when this sample starts a new window.</summary>
    public AggregateRow? Add(ClusterSample sample, LockEvaluation? lockEval)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var windowStart = Floor(sample.SampledAt);
        AggregateRow? finished = null;
        if (_current is not null && _current.WindowStart != windowStart)
        {
            finished = _current.ToRow();
            _current = null;
        }
        _current ??= new Accumulator(windowStart);
        _current.Add(sample, lockEval);
        return finished;
    }

    /// <summary>Returns the open window (for shutdown), or null.</summary>
    public AggregateRow? Flush()
    {
        var row = _current?.ToRow();
        _current = null;
        return row;
    }

    private DateTimeOffset Floor(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % _window.Ticks), TimeSpan.Zero);
    }

    private sealed class Accumulator(DateTimeOffset windowStart)
    {
        private readonly Dictionary<string, int> _byDb = new(StringComparer.Ordinal);
        private int _samples, _failed, _sessions, _active, _idleInTx, _lockWaiters, _overThreshold;
        private double _maxWait, _maxXact;
        private Visibility _worst = Visibility.Unknown;

        public DateTimeOffset WindowStart { get; } = windowStart;

        public void Add(ClusterSample s, LockEvaluation? lockEval)
        {
            _samples++;
            if (!s.Succeeded)
            {
                _failed++;
                return;
            }

            _worst = Worse(_worst, s.Visibility);
            _sessions = Math.Max(_sessions, s.Sessions.Count);
            _active = Math.Max(_active, s.Sessions.Count(r => r.State == "active"));
            _idleInTx = Math.Max(_idleInTx, s.Sessions.Count(r => r.IsIdleInTransaction));
            _lockWaiters = Math.Max(_lockWaiters, s.Sessions.Count(r => r.IsWaitingOnLock));
            if (lockEval is not null)
            {
                _overThreshold = Math.Max(_overThreshold, lockEval.QualifyingWaiters);
                _maxWait = Math.Max(_maxWait, lockEval.Analysis.LongestWait.TotalSeconds);
            }
            foreach (var r in s.Sessions)
            {
                if (r.TransactionAge(s.SampledAt) is { } age)
                {
                    _maxXact = Math.Max(_maxXact, age.TotalSeconds);
                }
            }
            foreach (var g in s.Sessions.Where(r => r.Database is not null).GroupBy(r => r.Database!, StringComparer.Ordinal))
            {
                _byDb[g.Key] = Math.Max(_byDb.GetValueOrDefault(g.Key), g.Count());
            }
        }

        public AggregateRow ToRow() => new()
        {
            WindowStart = WindowStart,
            Samples = _samples,
            FailedSamples = _failed,
            MaxSessions = _sessions,
            MaxActive = _active,
            MaxIdleInTransaction = _idleInTx,
            MaxLockWaiters = _lockWaiters,
            MaxLockWaitersOverThreshold = _overThreshold,
            MaxLockWaitSeconds = Math.Round(_maxWait, 1),
            MaxTransactionAgeSeconds = Math.Round(_maxXact, 1),
            WorstVisibility = _worst,
            MaxSessionsByDatabase = new SortedDictionary<string, int>(_byDb, StringComparer.Ordinal),
        };

        /// <summary>Full &lt; Partial &lt; OwnOnly; Unknown only when nothing succeeded.</summary>
        private static Visibility Worse(Visibility a, Visibility b) =>
            a == Visibility.Unknown ? b : b == Visibility.Unknown ? a : (Visibility)Math.Max((int)a, (int)b);
    }
}
