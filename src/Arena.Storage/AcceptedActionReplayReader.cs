using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed record AcceptedActionReplayReadResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    string? SourceRunId,
    IReadOnlyList<RecordedAction> AcceptedActions)
{
    public static AcceptedActionReplayReadResult Failure(string detail) => new(
        false,
        ArenaErrorCodes.ArtifactVerificationFailed,
        detail,
        null,
        []);
}

/// <summary>
/// Selects exactly the accepted actions from a sealed action stream for a
/// provider-free replay. Rejected, failed, and duplicate entries remain audit
/// evidence but never become replay commands.
/// </summary>
public static class AcceptedActionReplayReader
{
    private const long MaximumFileBytes = 16L * 1024 * 1024;
    private const int MaximumRecords = 10_000;
    private const int MaximumLineCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<AcceptedActionReplayReadResult> ReadAsync(
        string actionsPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actionsPath) || !File.Exists(actionsPath))
        {
            return AcceptedActionReplayReadResult.Failure("The recorded action stream does not exist.");
        }

        FileInfo info = new(actionsPath);
        if (info.Length > MaximumFileBytes)
        {
            return AcceptedActionReplayReadResult.Failure("The recorded action stream exceeds the bounded replay-reader size limit.");
        }

        List<RecordedAction> accepted = [];
        string? runId = null;
        int records = 0;
        try
        {
            await using FileStream stream = new(
                actionsPath,
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

                if (line.Length > MaximumLineCharacters || records++ >= MaximumRecords)
                {
                    return AcceptedActionReplayReadResult.Failure("The recorded action stream exceeds the bounded replay-reader record limits.");
                }

                RecordedAction? action = JsonSerializer.Deserialize<RecordedAction>(line, StrictJsonOptions);
                if (!IsValid(action))
                {
                    return AcceptedActionReplayReadResult.Failure("A recorded action does not satisfy the closed v1 action artifact contract.");
                }

                RecordedAction validAction = action!;
                runId ??= validAction.RunId;
                if (!string.Equals(runId, validAction.RunId, StringComparison.Ordinal))
                {
                    return AcceptedActionReplayReadResult.Failure("The recorded action stream mixes records from different runs.");
                }

                if (string.Equals(validAction.Result.Status, "accepted", StringComparison.Ordinal))
                {
                    accepted.Add(validAction);
                }
            }
        }
        catch (IOException)
        {
            return AcceptedActionReplayReadResult.Failure("The recorded action stream could not be read safely.");
        }
        catch (JsonException)
        {
            return AcceptedActionReplayReadResult.Failure("The recorded action stream contains invalid JSON or an unknown contract field.");
        }

        return accepted.Count == 0
            ? AcceptedActionReplayReadResult.Failure("The recorded action stream contains no accepted actions to replay.")
            : new AcceptedActionReplayReadResult(
                true,
                null,
                $"Selected {accepted.Count} accepted action(s) from source run {runId}.",
                runId,
                accepted);
    }

    private static bool IsValid(RecordedAction? action) =>
        action is not null &&
        string.Equals(action.SchemaVersion, ContractVersions.ObservationV1, StringComparison.Ordinal) &&
        ProtocolEnvelopeValidator.IsIdentifier(action.RunId) &&
        ProtocolEnvelopeValidator.IsIdentifier(action.DecisionId) &&
        action.Request is not null &&
        action.Result is not null &&
        string.Equals(action.Request.RunId, action.RunId, StringComparison.Ordinal) &&
        string.Equals(action.Request.DecisionId, action.DecisionId, StringComparison.Ordinal) &&
        string.Equals(action.Result.RunId, action.RunId, StringComparison.Ordinal) &&
        string.Equals(action.Result.ActionId, action.Request.ActionId, StringComparison.Ordinal) &&
        string.Equals(action.Result.CorrelationId, action.Request.CorrelationId, StringComparison.Ordinal) &&
        ProtocolEnvelopeValidator.IsIdentifier(action.Request.ActionId) &&
        ProtocolEnvelopeValidator.IsIdentifier(action.Request.CorrelationId) &&
        ProtocolEnvelopeValidator.IsIdentifier(action.Request.IdempotencyKey) &&
        RoadToolCatalog.All.Contains(action.Request.Tool, StringComparer.Ordinal);
}
