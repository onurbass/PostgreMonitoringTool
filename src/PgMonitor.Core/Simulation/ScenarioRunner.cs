using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;

namespace PgMonitor.Core.Simulation;

/// <summary>Outcome of running a scenario through the incident engine.</summary>
public sealed record ScenarioRun(
    Scenario Scenario,
    IReadOnlyList<IncidentEvent> Events,
    int SampleCount,
    int FailedSamples,
    EngineState FinalState)
{
    public int Count(IncidentEventKind kind) => Events.Count(e => e.Kind == kind);
}

/// <summary>Runs a scenario through the engine at virtual speed. Used by tests and the demo mode.</summary>
public static class ScenarioRunner
{
    public static ScenarioRun Run(
        Scenario scenario,
        IncidentSettings? settings = null,
        Action<ClusterSample, EngineResult>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        settings ??= new IncidentSettings();

        var sim = new Simulator(scenario);
        var state = EngineState.Initial;
        var events = new List<IncidentEvent>();
        var samples = 0;
        var failed = 0;

        while (sim.HasNext)
        {
            var sample = sim.Next();
            samples++;
            if (!sample.Succeeded)
            {
                failed++;
            }
            var result = IncidentEngine.Process(state, sample, settings);
            state = result.State;
            events.AddRange(result.Events);
            observe?.Invoke(sample, result);
        }

        return new ScenarioRun(scenario, events, samples, failed, state);
    }
}
