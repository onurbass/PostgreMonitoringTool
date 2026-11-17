using Microsoft.Extensions.Logging;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Core.Transactions;
using PgMonitor.Infrastructure.Notifications;
using PgMonitor.Infrastructure.Postgres;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.Infrastructure.Runtime;

public sealed record MonitorOptions
{
    public IncidentSettings Incident { get; init; } = new();
    public StorageSettings Storage { get; init; } = new();
    public RecorderSettings Recorder { get; init; } = new();
    public TimeSpan NormalInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IncidentInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaintenanceEvery { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan MailCheckEvery { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Everything the UI needs after one cycle. Immutable.</summary>
public sealed record MonitorSnapshot
{
    public required SourceKind Source { get; init; }
    public ClusterSample? Sample { get; init; }
    public EngineState State { get; init; } = EngineState.Initial;
    public LockEvaluation? Lock { get; init; }
    public IReadOnlyList<IncidentEvent> RecentEvents { get; init; } = [];
    public int ConsecutiveFailures { get; init; }
    public string? CollectError { get; init; }
    public bool StorageDegraded { get; init; }
    public string? StorageError { get; init; }
    public StorageReport? Storage { get; init; }
    public bool StorageOverCap { get; init; }
    public OutboxCounts? Mail { get; init; }
    public string? MailError { get; init; }
    public ServerCapabilities? Capabilities { get; init; }

    /// <summary>Transactions currently open longer than the threshold (whether or not they block anyone).</summary>
    public IReadOnlyList<LongTransaction> LongTransactions { get; init; } = [];
}

/// <summary>
/// The monitoring pipeline: source → engine → recorder → notifier, one sample at a time (never overlapping).
/// Mail sending runs on its own loop, so SMTP problems never delay sampling. A failure in one stage is logged
/// (type only, never SQL or secrets) and the loop continues.
/// </summary>
public sealed partial class MonitorLoop
{
    private const int MaxRecentEvents = 100;

    private readonly ISampleSource _source;
    private readonly IMonitorStore _store;
    private readonly MonitorOptions _options;
    private readonly MailNotifier? _notifier;
    private readonly OutboxWorker? _outbox;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly EvidenceRecorder _recorder;
    private readonly LongTransactionTracker _longTx = new();
    private readonly List<IncidentEvent> _recent = [];

    private EngineState _state = EngineState.Initial;
    private int _failures;
    private bool _started;
    private DateTimeOffset _lastMaintenance = DateTimeOffset.MinValue;
    private MaintenanceResult? _maintenance;

    public MonitorLoop(ISampleSource source, IMonitorStore store, MonitorOptions options, MailNotifier? notifier, OutboxWorker? outbox,
        TimeProvider time, ILogger<MonitorLoop> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _store = store;
        _options = options;
        _notifier = notifier;
        _outbox = outbox;
        _time = time;
        _logger = logger;
        _recorder = new EvidenceRecorder(store, options.Recorder);
        Latest = new MonitorSnapshot { Source = source.Kind };
    }

    public MonitorSnapshot Latest { get; private set; }

    /// <summary>Raised on the loop's thread after every cycle.</summary>
    public event EventHandler<MonitorSnapshot>? Updated;

    public void Start()
    {
        if (_started)
        {
            return;
        }
        var interrupted = _store.MarkOpenIncidentsInterrupted(_time.GetUtcNow());
        if (interrupted > 0)
        {
            LogInterrupted(interrupted);
        }
        _state = EngineState.Initial with { LastIncidentId = _store.MaxIncidentId() };
        _started = true;
    }

    /// <summary>One cycle. Public for tests and for a "sample now" button.</summary>
    public async Task<MonitorSnapshot> TickAsync(CancellationToken cancellationToken)
    {
        Start();
        var sample = await _source.CollectAsync(cancellationToken).ConfigureAwait(false);
        var result = IncidentEngine.Process(_state, sample, _options.Incident);
        _state = result.State;
        _failures = sample.Succeeded ? 0 : _failures + 1;
        if (!sample.Succeeded)
        {
            LogSampleFailed(sample.Failure!.Value, _failures);
        }
        foreach (var e in result.Events)
        {
            LogEvent(e.Kind, e.IncidentId, e.Severity);
        }

        string? storageError = null;
        var longTxEvents = _longTx.Update(sample, _options.Incident.LongTransactionThreshold, TimeSpan.FromMinutes(1));
        try
        {
            _recorder.Handle(sample, result);
            _store.RecordLongTransactions(longTxEvents);
        }
#pragma warning disable CA1031 // Storage problems must not stop sampling; they are shown in the UI.
        catch (Exception ex)
        {
            storageError = ex.GetType().Name;
            LogStorageFailed(storageError);
        }

        try
        {
            _notifier?.OnResult(sample, result);
        }
        catch (Exception ex)
        {
            LogNotifyFailed(ex.GetType().Name);
        }

        var now = _time.GetUtcNow();
        if (now - _lastMaintenance >= _options.MaintenanceEvery)
        {
            _lastMaintenance = now;
            try
            {
                _maintenance = _store.RunMaintenance(now, _options.Storage);
                if (_maintenance.OverCapWithOpenIncidents)
                {
                    LogOverCap();
                }
            }
            catch (Exception ex)
            {
                storageError ??= ex.GetType().Name;
                LogStorageFailed(ex.GetType().Name);
            }
        }
#pragma warning restore CA1031

        _recent.AddRange(result.Events);
        if (_recent.Count > MaxRecentEvents)
        {
            _recent.RemoveRange(0, _recent.Count - MaxRecentEvents);
        }

        OutboxCounts? mail = null;
        try
        {
            mail = _notifier is null ? null : _store.MailCounts();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // counts are informational only
        }

        Latest = new MonitorSnapshot
        {
            Source = _source.Kind,
            Sample = sample,
            State = _state,
            Lock = result.Lock,
            RecentEvents = _recent.ToList(),
            ConsecutiveFailures = _failures,
            CollectError = (_source as PostgresSampleSource)?.LastError,
            StorageDegraded = storageError is not null,
            StorageError = storageError,
            Storage = _maintenance?.Report,
            StorageOverCap = _maintenance?.OverCapWithOpenIncidents ?? false,
            Mail = mail,
            MailError = _outbox?.LastError,
            Capabilities = (_source as PostgresSampleSource)?.Capabilities,
            LongTransactions = _longTx.Current,
        };
        Updated?.Invoke(this, Latest);
        return Latest;
    }

    /// <summary>Delay before the next sample: faster during an incident, exponential backoff while failing.</summary>
    public TimeSpan NextDelay()
    {
        if (_failures > 0)
        {
            var backoff = TimeSpan.FromTicks(_options.NormalInterval.Ticks * (1L << Math.Min(_failures - 1, 10)));
            return backoff < _options.MaxBackoff ? backoff : _options.MaxBackoff;
        }
        return _state.Phase == IncidentPhase.Healthy ? _options.NormalInterval : _options.IncidentInterval;
    }

    /// <summary>Runs until cancelled. Sampling and mail sending are separate loops.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Start();
        var mailLoop = _outbox is null ? Task.CompletedTask : MailLoopAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // Never let one bad cycle end monitoring.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _failures++;
                    LogCycleFailed(ex.GetType().Name);
                }
                await Task.Delay(NextDelay(), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            try
            {
                _recorder.Flush();
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStorageFailed(ex.GetType().Name);
            }
            await mailLoop.ConfigureAwait(false);
        }
    }

    private async Task MailLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _outbox!.ProcessAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogNotifyFailed(ex.GetType().Name);
                }
                await Task.Delay(_options.MailCheckEvery, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} incident(s) from the previous run were still open and are now marked Interrupted")]
    private partial void LogInterrupted(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sample failed: {Kind} ({Consecutive} in a row)")]
    private partial void LogSampleFailed(CollectFailureKind kind, int consecutive);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident event {Kind} for incident {IncidentId}, severity {Severity}")]
    private partial void LogEvent(IncidentEventKind kind, long incidentId, Severity severity);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storage failure: {ErrorType}")]
    private partial void LogStorageFailed(string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification failure: {ErrorType}")]
    private partial void LogNotifyFailed(string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Monitoring cycle failed: {ErrorType}")]
    private partial void LogCycleFailed(string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage is over its size cap while incidents are open; open incidents were thinned")]
    private partial void LogOverCap();
}
