namespace Aura.Orchestrator.Metrics;

/// <summary>
/// Minimal abstraction over the metrics backend used by the orchestrator components.
/// </summary>
public interface IMetricsCollector
{
    void IncrementCounter(string metric, double value = 1, IReadOnlyDictionary<string, string>? labels = null);

    void RecordGauge(string metric, double value, IReadOnlyDictionary<string, string>? labels = null);

    void RecordHistogram(string metric, double value, IReadOnlyDictionary<string, string>? labels = null);
}
