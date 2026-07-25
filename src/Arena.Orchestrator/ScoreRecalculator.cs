using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Scoring;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ScoreRecalculationResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    string? StoredScoreSha256,
    string? RecalculatedScoreSha256)
{
    public static ScoreRecalculationResult Failure(string errorCode, string detail) => new(
        false,
        errorCode,
        detail,
        null,
        null);
}

/// <summary>
/// Independently regenerates a persisted score from the captured scenario and
/// authoritative metric stream. It never contacts a provider or starts
/// OpenTTD, so it is safe to run on an archived evidence directory.
/// </summary>
public static class ScoreRecalculator
{
    public static async Task<ScoreRecalculationResult> RecalculateAsync(
        string runDirectory,
        CancellationToken cancellationToken)
    {
        RunVerificationResult verification = await RunVerifier.VerifyAsync(runDirectory, cancellationToken);
        if (!verification.Succeeded)
        {
            return ScoreRecalculationResult.Failure(
                verification.ErrorCode ?? ArenaErrorCodes.ArtifactVerificationFailed,
                verification.Detail);
        }

        try
        {
            RunPathPolicy paths = new(runDirectory);
            ScenarioLoadResult scenario = await ScenarioLoader.LoadAsync(
                runDirectory,
                paths.Resolve("input/scenario.yaml"),
                cancellationToken);
            if (!scenario.Succeeded || scenario.Document is null)
            {
                return ScoreRecalculationResult.Failure(
                    ArenaErrorCodes.ScenarioInvalid,
                    "The sealed scenario artifact is not a supported immutable benchmark scenario.");
            }

            BenchmarkMetricReadResult metrics = await BenchmarkMetricSnapshotReader.ReadAsync(
                paths.Resolve(ObservationArtifactWriter.MetricsFileName),
                cancellationToken);
            if (!metrics.Succeeded)
            {
                return ScoreRecalculationResult.Failure(
                    metrics.ErrorCode ?? ArenaErrorCodes.ArtifactVerificationFailed,
                    metrics.Detail);
            }

            BenchmarkMetricSnapshot finalMetrics = await BenchmarkArtifactStore.ReadFinalMetricsAsync(paths, cancellationToken);
            ScoreResult stored = await BenchmarkArtifactStore.ReadScoreAsync(paths, cancellationToken);
            ScoreResult recalculated = new RoadProfitScoreCalculator().Calculate(new ScoreInput(
                scenario.Document.Scenario,
                finalMetrics,
                metrics.Snapshots.Where(snapshot => string.Equals(snapshot.Kind, "periodic", StringComparison.Ordinal)).ToArray()));
            string storedHash = CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(stored));
            string recalculatedHash = CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(recalculated));
            if (!string.Equals(storedHash, recalculatedHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ScoreRecalculationResult(
                    false,
                    ArenaErrorCodes.ScoreRecalculationMismatch,
                    "The persisted score does not exactly match deterministic recalculation from the sealed metrics and scenario.",
                    storedHash,
                    recalculatedHash);
            }

            return new ScoreRecalculationResult(
                true,
                null,
                "The persisted score exactly matches deterministic recalculation from the sealed metrics and scenario.",
                storedHash,
                recalculatedHash);
        }
        catch (InvalidOperationException exception)
        {
            return ScoreRecalculationResult.Failure(
                ArenaErrorCodes.ArtifactVerificationFailed,
                ArtifactTextRedactor.Redact(exception.Message));
        }
    }
}
