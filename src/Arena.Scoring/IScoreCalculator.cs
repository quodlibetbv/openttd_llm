using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Scoring;

public interface IScoreCalculator
{
    ScoreResult Calculate(ScoreInput input);
}

public sealed record ScoreInput(
    BenchmarkScenario Scenario,
    BenchmarkMetricSnapshot FinalMetrics,
    IReadOnlyList<BenchmarkMetricSnapshot> PeriodicMetrics);
