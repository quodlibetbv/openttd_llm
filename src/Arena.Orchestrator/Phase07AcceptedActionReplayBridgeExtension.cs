using System.Security.Cryptography;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Replays only the accepted typed actions from a previously sealed benchmark
/// run against the same starting-save fingerprint. The replay owns no model
/// provider and compares only the declared authoritative metric vector.
/// </summary>
public sealed class Phase07AcceptedActionReplayBridgeExtension : IPhase03BridgeExtension
{
    private const int VerificationPollLimit = 180;
    private readonly ScenarioDocument _scenario;
    private readonly BenchmarkMetricSnapshot _expectedFinalMetrics;
    private readonly IReadOnlyList<RecordedAction> _acceptedActions;
    private readonly string _sourceRunId;
    private readonly string _sourceStartingSaveSha256;

    public Phase07AcceptedActionReplayBridgeExtension(
        ScenarioDocument scenario,
        BenchmarkMetricSnapshot expectedFinalMetrics,
        IReadOnlyList<RecordedAction> acceptedActions,
        string sourceRunId,
        string sourceStartingSaveSha256)
    {
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        _expectedFinalMetrics = expectedFinalMetrics ?? throw new ArgumentNullException(nameof(expectedFinalMetrics));
        _acceptedActions = acceptedActions?.ToArray() ?? throw new ArgumentNullException(nameof(acceptedActions));
        _sourceRunId = ProtocolEnvelopeValidator.IsIdentifier(sourceRunId)
            ? sourceRunId
            : throw new ArgumentException("The source run identifier is invalid.", nameof(sourceRunId));
        _sourceStartingSaveSha256 = IsSha256(sourceStartingSaveSha256)
            ? sourceStartingSaveSha256
            : throw new ArgumentException("The source starting-save fingerprint is invalid.", nameof(sourceStartingSaveSha256));
    }

    public async Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<Phase03BridgeCheck> checks = [];
        if (!IsReplaySourceValid())
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ArtifactVerificationFailed,
                "The sealed source run does not provide a supported final metric snapshot and accepted action sequence.",
                checks);
        }

        string currentStartingSaveSha256;
        try
        {
            currentStartingSaveSha256 = await ComputeFileSha256Async(context.StartingSavePath, cancellationToken);
        }
        catch (IOException)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.RunArtifactMissing,
                "The current fixed starting save could not be fingerprinted before accepted-action replay.",
                checks);
        }

        if (!string.Equals(currentStartingSaveSha256, _sourceStartingSaveSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ArtifactVerificationFailed,
                "The current fixed starting save does not match the sealed source benchmark baseline.",
                checks);
        }

        ScenarioActionConstraintContext expectedConstraints = ScenarioLoader.CreateActionConstraintContext(_scenario);
        if (_acceptedActions.Any(action => !MatchesSourceScenario(action, expectedConstraints)))
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ArtifactVerificationFailed,
                "A sealed accepted action is not bound to the source scenario's immutable constraint context.",
                checks);
        }

        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        using ObservationArtifactWriter artifacts = new(context.Paths, context.RunId);
        bool paused = false;
        try
        {
            ArenaError? pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
            if (pauseError is not null)
            {
                return Failure(pauseError, "replay-pause", "The simulation could not be paused before accepted-action replay.", checks);
            }

            paused = true;
            GameScriptSnapshotResult initialSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!initialSnapshotResult.Succeeded || initialSnapshotResult.Snapshot is null || !initialSnapshotResult.Snapshot.Paused)
            {
                return Failure(initialSnapshotResult.Error, "replay-initial-snapshot", "ArenaGS did not provide a paused authoritative starting snapshot for action replay.", checks);
            }

            await artifacts.AppendMetricAsync(
                BenchmarkMetricCollector.Capture(context.RunId, "replay-metric-initial-1", "initial", initialSnapshotResult.Snapshot, 0, 0),
                cancellationToken);
            foreach (NormalizedGameEvent eventEntry in initialSnapshotResult.Snapshot.Events)
            {
                await artifacts.AppendEventAsync(eventEntry, cancellationToken);
            }

            for (int index = 0; index < _acceptedActions.Count; index++)
            {
                RecordedAction source = _acceptedActions[index];
                ActionRequest replayRequest = CreateReplayRequest(source.Request, expectedConstraints, context.RunId, index + 1);
                GameScriptActionResult execution = await gameScript.ExecuteActionAsync(
                    replayRequest,
                    context.RequestTimeout,
                    cancellationToken);
                ActionResult result = execution.Action ?? new ActionResult
                {
                    ActionId = replayRequest.ActionId,
                    RunId = context.RunId,
                    CorrelationId = replayRequest.CorrelationId,
                    Status = "failed",
                    ErrorCode = execution.Error?.Code ?? ArenaErrorCodes.AdminPortUnavailable,
                    Message = execution.Error?.UserMessage ?? "ArenaGS did not return a replay action result.",
                };
                await artifacts.AppendActionAsync(new RecordedAction
                {
                    SchemaVersion = ContractVersions.ObservationV1,
                    RunId = context.RunId,
                    DecisionId = replayRequest.DecisionId,
                    Request = replayRequest,
                    Result = result,
                }, cancellationToken);
                if (!string.Equals(result.Status, "accepted", StringComparison.Ordinal))
                {
                    return Phase03BridgeExtensionResult.Failure(
                        result.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        "An action accepted by the sealed source run was not accepted by ArenaGS during replay.",
                        checks);
                }

                ArenaError? resumeError = await gameScript.ResumeAsync(context.RequestTimeout, cancellationToken);
                if (resumeError is not null)
                {
                    return Failure(resumeError, "replay-resume", "The simulation could not advance an accepted replay action.", checks);
                }

                paused = false;
                Phase03BridgeExtensionResult? settlementFailure = await WaitForSettlementAsync(
                    context,
                    gameScript,
                    artifacts,
                    replayRequest,
                    checks,
                    cancellationToken);
                if (settlementFailure is not null)
                {
                    return settlementFailure;
                }

                if (index + 1 < _acceptedActions.Count)
                {
                    pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
                    if (pauseError is not null)
                    {
                        return Failure(pauseError, "replay-next-pause", "The simulation could not pause before the next accepted replay action.", checks);
                    }

                    paused = true;
                }
            }

            ArenaError? finalPauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
            if (finalPauseError is not null)
            {
                return Failure(finalPauseError, "replay-final-pause", "The simulation could not be paused before final replay metric capture.", checks);
            }

            paused = true;
            GameScriptSnapshotResult finalSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!finalSnapshotResult.Succeeded || finalSnapshotResult.Snapshot is null || !finalSnapshotResult.Snapshot.Paused)
            {
                return Failure(finalSnapshotResult.Error, "replay-final-metrics", "ArenaGS did not provide a paused final authoritative metric snapshot for action replay.", checks);
            }

            BenchmarkMetricSnapshot actualFinalMetrics = BenchmarkMetricCollector.Capture(
                context.RunId,
                "replay-metric-final-1",
                "final",
                finalSnapshotResult.Snapshot,
                0,
                0);
            await artifacts.AppendMetricAsync(actualFinalMetrics, cancellationToken);
            ReplayMetricComparisonResult comparison = ReplayMetricComparator.Compare(
                _expectedFinalMetrics,
                actualFinalMetrics,
                _scenario.Scenario.ReplayTolerances);
            if (!comparison.Succeeded)
            {
                checks.Add(new Phase03BridgeCheck("replay-metrics", false, comparison.ErrorCode, comparison.Detail));
                return Phase03BridgeExtensionResult.Failure(
                    comparison.ErrorCode ?? ArenaErrorCodes.ReplayMetricsMismatch,
                    comparison.Detail,
                    checks);
            }

            ArenaError? finalResumeError = await gameScript.ResumeAsync(context.RequestTimeout, cancellationToken);
            if (finalResumeError is not null)
            {
                return Failure(finalResumeError, "replay-final-resume", "The replay server could not resume after final metric capture.", checks);
            }

            paused = false;
            checks.Add(Pass("replay-baseline", "The current fixed starting-save SHA-256 matches the sealed source benchmark baseline."));
            checks.Add(Pass("replay-actions", "Every sealed accepted action was replayed through the typed ArenaGS action boundary without a model provider."));
            checks.Add(Pass("replay-metrics", comparison.Detail));
            return Phase03BridgeExtensionResult.Success("The accepted-action replay reproduced the sealed benchmark's declared final metric vector within documented tolerances.", checks);
        }
        finally
        {
            if (paused)
            {
                _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
            }
        }
    }

    private static async Task<Phase03BridgeExtensionResult?> WaitForSettlementAsync(
        Phase03BridgeExtensionContext context,
        ArenaGameScriptClient gameScript,
        ObservationArtifactWriter artifacts,
        ActionRequest replayRequest,
        List<Phase03BridgeCheck> checks,
        CancellationToken cancellationToken)
    {
        bool projectObserved = false;
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
        {
            GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
            {
                return Failure(snapshotResult.Error, "replay-progress", "ArenaGS did not provide authoritative action-replay progress metrics.", checks);
            }

            foreach (NormalizedGameEvent eventEntry in snapshotResult.Snapshot.Events)
            {
                await artifacts.AppendEventAsync(eventEntry, cancellationToken);
            }

            GameProjectState? project = snapshotResult.Snapshot.Projects.SingleOrDefault(candidate =>
                string.Equals(candidate.ActionId, replayRequest.ActionId, StringComparison.Ordinal));
            if (project is not null)
            {
                projectObserved = true;
                if (string.Equals(project.State, "failed", StringComparison.Ordinal))
                {
                    return Phase03BridgeExtensionResult.Failure(
                        project.FailureCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        "ArenaGS safely failed an accepted replay action before it reached its terminal state.",
                        checks);
                }

                if (string.Equals(project.State, "completed", StringComparison.Ordinal))
                {
                    if (string.Equals(replayRequest.Tool, RoadToolCatalog.BuildTransportRoute, StringComparison.Ordinal) &&
                        !snapshotResult.Snapshot.Routes.Any(route =>
                            route.Operational &&
                            route.VehicleIds.Count > 0 &&
                            string.Equals(route.ActionId, replayRequest.ActionId, StringComparison.Ordinal)))
                    {
                        return Phase03BridgeExtensionResult.Failure(
                            ArenaErrorCodes.ArtifactVerificationFailed,
                            "A replayed route project completed without an operational route and vehicle-movement evidence.",
                            checks);
                    }

                    return null;
                }
            }
            else if (attempt >= 1)
            {
                return string.Equals(replayRequest.Tool, RoadToolCatalog.BuildTransportRoute, StringComparison.Ordinal)
                    ? Phase03BridgeExtensionResult.Failure(
                        ArenaErrorCodes.ArtifactVerificationFailed,
                        "A replayed route action was accepted without a persisted ArenaGS project.",
                        checks)
                    : null;
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return projectObserved
            ? Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ActionVerificationTimedOut,
                "A replayed persisted action did not reach a terminal state before the bounded verification window.",
                checks)
            : null;
    }

    private bool IsReplaySourceValid() =>
        _acceptedActions.Count > 0 &&
        string.Equals(_expectedFinalMetrics.SchemaVersion, ContractVersions.MetricV1, StringComparison.Ordinal) &&
        string.Equals(_expectedFinalMetrics.Kind, "final", StringComparison.Ordinal) &&
        string.Equals(_expectedFinalMetrics.RunId, _sourceRunId, StringComparison.Ordinal);

    private bool MatchesSourceScenario(RecordedAction action, ScenarioActionConstraintContext expectedConstraints) =>
        string.Equals(action.RunId, _sourceRunId, StringComparison.Ordinal) &&
        string.Equals(action.Result.Status, "accepted", StringComparison.Ordinal) &&
        action.Request.ConstraintContext is not null &&
        string.Equals(
            CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(action.Request.ConstraintContext)),
            CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(expectedConstraints)),
            StringComparison.OrdinalIgnoreCase);

    private static ActionRequest CreateReplayRequest(
        ActionRequest source,
        ScenarioActionConstraintContext constraints,
        string runId,
        int ordinal) =>
        new()
        {
            ActionId = "replay-action-" + ordinal,
            RunId = runId,
            DecisionId = "replay-decision-" + ordinal,
            CorrelationId = "replay-correlation-" + ordinal,
            IdempotencyKey = "replay-key-" + ordinal,
            Tool = source.Tool,
            Arguments = source.Arguments.Clone(),
            ConstraintContext = constraints,
        };

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Phase03BridgeExtensionResult Failure(
        ArenaError? error,
        string checkId,
        string fallbackDetail,
        IReadOnlyList<Phase03BridgeCheck> checks)
    {
        List<Phase03BridgeCheck> failedChecks = checks.ToList();
        failedChecks.Add(new Phase03BridgeCheck(
            checkId,
            false,
            error?.Code ?? ArenaErrorCodes.ProtocolInvalidMessage,
            fallbackDetail));
        return Phase03BridgeExtensionResult.Failure(
            error?.Code ?? ArenaErrorCodes.ProtocolInvalidMessage,
            fallbackDetail,
            failedChecks);
    }

    private static Phase03BridgeCheck Pass(string id, string detail) => new(id, true, null, detail);
}
