using PgMonitor.Core.Aggregation;
using PgMonitor.Core.Buffers;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;

namespace PgMonitor.Infrastructure.Storage;

public sealed record RecorderSettings
{
    /// <summary>Samples kept in memory for the pre-incident flush (about 3 minutes at a 2 s interval).</summary>
    public int PreIncidentSamples { get; init; } = 90;

    /// <summary>Memory cap for the pre-incident buffer.</summary>
    public long PreIncidentMaxBytes { get; init; } = 32L * 1024 * 1024;

    public TimeSpan AggregateWindow { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Decides what goes to disk for each processed sample:
/// healthy → one aggregate row per window only; incident opened → flush the in-memory buffer as "Pre";
/// incident open → every sample in detail; closed → status update.
/// </summary>
public sealed class EvidenceRecorder
{
    private readonly IMonitorStore _store;
    private readonly RingBuffer<ClusterSample> _buffer;
    private readonly MinuteAggregator _aggregator;

    public EvidenceRecorder(IMonitorStore store, RecorderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        settings ??= new RecorderSettings();
        _store = store;
        _buffer = new RingBuffer<ClusterSample>(settings.PreIncidentSamples, settings.PreIncidentMaxBytes, EstimateBytes);
        _aggregator = new MinuteAggregator(settings.AggregateWindow);
    }

    public int BufferedSamples => _buffer.Count;

    public long BufferedBytes => _buffer.TotalBytes;

    public void Handle(ClusterSample sample, EngineResult result)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(result);

        var before = _buffer.Snapshot();
        _buffer.Add(sample);

        if (_aggregator.Add(sample, result.Lock) is { } row)
        {
            _store.WriteAggregate(row);
        }

        var state = result.State;
        var incident = state.Incident;
        var events = result.Events;
        var opened = events.Any(e => e.Kind is IncidentEventKind.Opened or IncidentEventKind.Reopened);
        var closed = events.Any(e => e.Kind == IncidentEventKind.Closed);

        if (incident is not null && (opened || events.Any(e => e.IncidentId == incident.Id)))
        {
            _store.UpsertIncident(incident, StatusOf(state));
        }
        if (opened && incident is not null)
        {
            _store.WriteSamples(incident.Id, SamplePhase.Pre, before);
        }
        _store.AppendEvents(events);

        if (incident is not null && (state.Phase != IncidentPhase.Healthy || closed))
        {
            var phase = state.Phase == IncidentPhase.Active ? SamplePhase.Active : SamplePhase.Recovering;
            _store.WriteSamples(incident.Id, phase, [sample]);
        }
    }

    /// <summary>Writes the unfinished aggregate window (call on shutdown).</summary>
    public void Flush()
    {
        if (_aggregator.Flush() is { } row)
        {
            _store.WriteAggregate(row);
        }
    }

    private static IncidentStatus StatusOf(EngineState state) => state.Phase switch
    {
        IncidentPhase.Active => IncidentStatus.Active,
        IncidentPhase.Recovering => IncidentStatus.Recovering,
        _ => IncidentStatus.Closed,
    };

    /// <summary>Rough memory estimate: fixed cost per row plus SQL text (UTF-16).</summary>
    internal static long EstimateBytes(ClusterSample s) =>
        256 + s.Sessions.Sum(r => 300L + 2L * (r.Query?.Length ?? 0));
}
