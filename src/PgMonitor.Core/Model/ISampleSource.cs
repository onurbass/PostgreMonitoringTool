namespace PgMonitor.Core.Model;

/// <summary>Where samples come from. Simulated data must always be labelled as such in the UI.</summary>
public enum SourceKind
{
    Simulated,
    Live,
}

/// <summary>
/// The only door to monitoring data. Implementations: the simulator (demo and tests) and the PostgreSQL source (live).
/// Implementations never throw for collection problems; they return <see cref="ClusterSample.Failed"/> instead.
/// </summary>
public interface ISampleSource
{
    SourceKind Kind { get; }

    Task<ClusterSample> CollectAsync(CancellationToken cancellationToken);
}
