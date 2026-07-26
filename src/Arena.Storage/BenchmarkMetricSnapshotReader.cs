using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed record BenchmarkMetricReadResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    IReadOnlyList<BenchmarkMetricSnapshot> Snapshots)
{
    public static BenchmarkMetricReadResult Failure(string detail) => new(
        false,
        ArenaErrorCodes.ArtifactVerificationFailed,
        detail,
        []);
}

/// <summary>
/// Reads the bounded, canonical metric stream without starting a game or
/// contacting a provider. The reader rejects mixed-run or malformed records
/// before they can be used for score recalculation.
/// </summary>
public static class BenchmarkMetricSnapshotReader
{
    private const long MaximumFileBytes = 16L * 1024 * 1024;
    private const int MaximumRecords = 10_000;
    private const int MaximumLineCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<BenchmarkMetricReadResult> ReadAsync(
        string metricsPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metricsPath) || !File.Exists(metricsPath))
        {
            return BenchmarkMetricReadResult.Failure("The recorded benchmark metric stream does not exist.");
        }

        FileInfo info = new(metricsPath);
        if (info.Length > MaximumFileBytes)
        {
            return BenchmarkMetricReadResult.Failure("The recorded benchmark metric stream exceeds the bounded reader size limit.");
        }

        List<BenchmarkMetricSnapshot> snapshots = [];
        HashSet<string> sampleIds = new(StringComparer.Ordinal);
        string? runId = null;
        try
        {
            await using FileStream stream = new(
                metricsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4_096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamReader reader = new(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaximumLineCharacters || snapshots.Count >= MaximumRecords)
                {
                    return BenchmarkMetricReadResult.Failure("The recorded benchmark metric stream exceeds the bounded record limits.");
                }

                BenchmarkMetricSnapshot? snapshot = JsonSerializer.Deserialize<BenchmarkMetricSnapshot>(line, StrictJsonOptions);
                if (!IsValid(snapshot))
                {
                    return BenchmarkMetricReadResult.Failure("A metric record does not satisfy the closed v1 benchmark metric contract.");
                }

                BenchmarkMetricSnapshot validSnapshot = snapshot!;
                if (!sampleIds.Add(validSnapshot.SampleId))
                {
                    return BenchmarkMetricReadResult.Failure("A metric record does not satisfy the closed v1 benchmark metric contract.");
                }

                runId ??= validSnapshot.RunId;
                if (!string.Equals(runId, validSnapshot.RunId, StringComparison.Ordinal))
                {
                    return BenchmarkMetricReadResult.Failure("The benchmark metric stream mixes records from different runs.");
                }

                snapshots.Add(validSnapshot);
            }
        }
        catch (IOException)
        {
            return BenchmarkMetricReadResult.Failure("The recorded benchmark metric stream could not be read safely.");
        }
        catch (JsonException)
        {
            return BenchmarkMetricReadResult.Failure("The recorded benchmark metric stream contains invalid JSON or an unknown contract field.");
        }

        return snapshots.Count == 0
            ? BenchmarkMetricReadResult.Failure("The recorded benchmark metric stream contains no snapshots.")
            : new BenchmarkMetricReadResult(
                true,
                null,
                $"Verified {snapshots.Count} authoritative metric snapshot(s) for run {runId}.",
                snapshots);
    }

    private static bool IsValid(BenchmarkMetricSnapshot? snapshot) =>
        snapshot is not null &&
        string.Equals(snapshot.SchemaVersion, ContractVersions.MetricV1, StringComparison.Ordinal) &&
        ProtocolEnvelopeValidator.IsIdentifier(snapshot.RunId) &&
        ProtocolEnvelopeValidator.IsIdentifier(snapshot.SampleId) &&
        snapshot.Kind is "initial" or "periodic" or "final" &&
        string.Equals(snapshot.Source, "gamescript", StringComparison.Ordinal) &&
        snapshot.GameTick >= 0 &&
        snapshot.Metrics is not null &&
        snapshot.Metrics.Loan >= 0 &&
        snapshot.Metrics.QuarterlyCargoDelivered >= 0 &&
        snapshot.Metrics.ActiveVehicleCount >= 0 &&
        snapshot.Metrics.OperationalRouteCount >= 0 &&
        snapshot.Metrics.CompletedProjectCount >= 0 &&
        snapshot.Metrics.InfrastructureInvestment >= 0 &&
        snapshot.Metrics.InvalidDecisionCount >= 0 &&
        snapshot.Metrics.ConstraintViolationCount >= 0;
}
