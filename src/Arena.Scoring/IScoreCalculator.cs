using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Scoring;

public interface IScoreCalculator
{
    ScoreResult Calculate(ScoreInput input);
}

public sealed record ScoreInput(
    BenchmarkGoal Goal,
    IReadOnlyDictionary<string, decimal> FinalMetrics,
    IReadOnlyDictionary<string, decimal> PeriodicMetrics);
