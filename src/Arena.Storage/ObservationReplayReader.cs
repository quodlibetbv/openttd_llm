using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed record ObservationReplayFrame(
    string RunId,
    string GameDate,
    string ObservationSha256,
    long Cash,
    long Loan,
    int RouteCount,
    int ActiveProjectCount,
    string? TopOpportunityId,
    string? LatestEventCode);

public sealed record ObservationReplayResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    IReadOnlyList<ObservationReplayFrame> Frames)
{
    public static ObservationReplayResult Failure(string detail) => new(
        false,
        ArenaErrorCodes.ArtifactVerificationFailed,
        detail,
        []);
}

/// <summary>
/// Reads exact public observation artifacts without a provider, credential, or
/// OpenTTD process. Every line is independently canonical-hash verified so a
/// human-facing replay summary never silently describes a modified snapshot.
/// </summary>
public static class ObservationReplayReader
{
    private const long MaximumFileBytes = 16L * 1024 * 1024;
    private const int MaximumRecords = 10_000;
    private const int MaximumLineCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<ObservationReplayResult> ReadAsync(string observationsPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(observationsPath) || !File.Exists(observationsPath))
        {
            return ObservationReplayResult.Failure("The recorded observations artifact does not exist.");
        }

        FileInfo info = new(observationsPath);
        if (info.Length > MaximumFileBytes)
        {
            return ObservationReplayResult.Failure("The recorded observations artifact exceeds the bounded replay-reader size limit.");
        }

        List<ObservationReplayFrame> frames = [];
        string? expectedRunId = null;
        try
        {
            await using FileStream stream = new(
                observationsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamReader reader = new(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaximumLineCharacters || frames.Count >= MaximumRecords)
                {
                    return ObservationReplayResult.Failure("The recorded observations artifact exceeds the bounded replay-reader record limits.");
                }

                RecordedObservation? record = JsonSerializer.Deserialize<RecordedObservation>(line, StrictJsonOptions);
                if (record is null ||
                    record.Observation is null ||
                    !string.Equals(record.SchemaVersion, ContractVersions.ObservationV1, StringComparison.Ordinal) ||
                    !ProtocolEnvelopeValidator.IsIdentifier(record.RunId) ||
                    !string.Equals(record.RunId, record.Observation.RunId, StringComparison.Ordinal))
                {
                    return ObservationReplayResult.Failure("A recorded observation does not satisfy the v1 public artifact contract.");
                }

                expectedRunId ??= record.RunId;
                if (!string.Equals(expectedRunId, record.RunId, StringComparison.Ordinal))
                {
                    return ObservationReplayResult.Failure("The observations artifact mixes records from different runs.");
                }

                JsonElement serialized = JsonSerializer.SerializeToElement(record.Observation, ObservationJsonContext.Default.ObservationSnapshot);
                string exactHash = ObservationDeltaCodec.ComputeHash(serialized);
                string replayHash = ObservationReplayHasher.ComputeSha256(record.Observation);
                if (!string.Equals(exactHash, record.ObservationSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(replayHash, record.ReplayObservationSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return ObservationReplayResult.Failure("A recorded observation hash does not match its canonical public payload.");
                }

                ObservationSections sections = record.Observation.Sections;
                frames.Add(new ObservationReplayFrame(
                    record.RunId,
                    record.Observation.GameDate,
                    exactHash,
                    sections.FinancialSummary.Cash,
                    sections.FinancialSummary.Loan,
                    sections.CompanySummary.RouteCount,
                    sections.ActiveProjects.Count,
                    sections.CandidateOpportunities.Opportunities.Count == 0
                        ? null
                        : sections.CandidateOpportunities.Opportunities[0].OpportunityId,
                    sections.RecentEvents.Count == 0
                        ? null
                        : sections.RecentEvents[^1].EventCode));
            }
        }
        catch (IOException)
        {
            return ObservationReplayResult.Failure("The recorded observations artifact could not be read safely.");
        }
        catch (JsonException)
        {
            return ObservationReplayResult.Failure("The recorded observations artifact contains invalid JSON or an unknown contract field.");
        }

        return frames.Count == 0
            ? ObservationReplayResult.Failure("The recorded observations artifact contains no public observation records.")
            : new ObservationReplayResult(
                true,
                null,
                $"Verified {frames.Count} canonical public observation record(s) for run {expectedRunId}.",
                frames);
    }
}
