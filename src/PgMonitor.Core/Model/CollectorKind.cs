namespace PgMonitor.Core.Model;

/// <summary>Where samples come from. Simulated data must always be labelled as such in the UI.</summary>
public enum CollectorKind
{
    Simulated,
    Live,
}
