using PgMonitor.Core.Model;

namespace PgMonitor.Core.Simulation;

/// <summary>
/// Demo/test source: plays a scenario sample by sample. Opens no connection of any kind.
/// With <c>loop</c>, the scenario starts again right after it ends, with time continuing forward.
/// </summary>
public sealed class SimulatedSampleSource(Scenario scenario, bool loop = false) : ISampleSource
{
    private Simulator _sim = new(scenario);

    public SourceKind Kind => SourceKind.Simulated;

    public Scenario Scenario => _sim.Scenario;

    public bool IsExhausted => !loop && !_sim.HasNext;

    public Task<ClusterSample> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sim.HasNext)
        {
            if (!loop)
            {
                throw new InvalidOperationException("The scenario has ended.");
            }
            var s = _sim.Scenario;
            _sim = new Simulator(s with { Start = s.Start + s.Duration });
        }
        return Task.FromResult(_sim.Next());
    }
}
