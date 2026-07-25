using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Exercises the lowest valid native project budget through the normal replay
/// provider and authorization path. ArenaGS must accept the typed request,
/// stop before its first unaffordable build command, and leave no new route
/// assets behind.
/// </summary>
public sealed class Phase06BudgetBoundaryBridgeExtension : IPhase03BridgeExtension
{
    private const int VerificationPollLimit = 90;

    public async Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<Phase03BridgeCheck> checks = [];
        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        using ObservationArtifactWriter artifacts = new(context.Paths, context.RunId);
        bool paused = false;
        try
        {
            ArenaError? pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
            if (pauseError is not null)
            {
                return Failure(pauseError, "budget-pause", "The simulation could not be paused before constructing the bounded budget fixture.", checks);
            }

            paused = true;
            GameScriptSnapshotResult sourceSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!sourceSnapshotResult.Succeeded || sourceSnapshotResult.Snapshot is null || !sourceSnapshotResult.Snapshot.Paused)
            {
                return Failure(sourceSnapshotResult.Error, "budget-observation", "ArenaGS did not return a paused authoritative snapshot for the budget-boundary fixture.", checks);
            }

            GameScriptSnapshot sourceSnapshot = sourceSnapshotResult.Snapshot;
            if (sourceSnapshot.Towns.Count < 2)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionConstraintViolation,
                    "The fixed smoke save does not expose two authoritative towns for the budget-boundary route request.",
                    checks);
            }

            ObservationBuildContext observationContext = ArenaRoadSmokeObservationContext.Create(context.RunId);
            ObservationBuildResult observation = ObservationBuilder.Build(sourceSnapshot, observationContext);
            GameTownState sourceTown = sourceSnapshot.Towns[0];
            GameTownState destinationTown = sourceSnapshot.Towns[1];
            const long minimumValidBudget = 1;
            string decisionId = "decision-budget-boundary-1";
            ReplayFixture fixture = new()
            {
                FixtureVersion = "1.0",
                Provider = "replay",
                Model = "phase-06-budget-boundary",
                Steps =
                [
                    new ReplayStep
                    {
                        ExpectedObservationSha256 = observation.ReplaySha256,
                        Decision = new ModelDecision
                        {
                            DecisionId = decisionId,
                            PublicSummary = "Attempt one passenger route with the smallest permitted project budget.",
                            Observations = ["The route request is intentionally bounded at one currency unit to verify native budget recovery."],
                            Actions =
                            [
                                new ModelAction
                                {
                                    Tool = RoadToolCatalog.BuildTransportRoute,
                                    Arguments = JsonSerializer.SerializeToElement(new
                                    {
                                        mode = "road",
                                        source_town_id = sourceTown.TownId,
                                        destination_town_id = destinationTown.TownId,
                                        cargo = "passengers",
                                        initial_vehicle_count = 1,
                                        maximum_budget = minimumValidBudget,
                                    }),
                                },
                            ],
                            NextReviewGameDays = 30,
                        },
                        Usage = new ReplayUsage
                        {
                            InputTokens = 0,
                            OutputTokens = 0,
                            LatencyMilliseconds = 0,
                        },
                    },
                ],
            };

            ProviderDecisionExecutionResult decisionResult = await ProviderDecisionExecutor.ExecuteAsync(
                gameScript,
                new ReplayModelProvider(fixture),
                artifacts,
                new ProviderDecisionExecutionOptions(
                    observationContext,
                    decisionId,
                    fixture.Model,
                    context.RequestTimeout),
                cancellationToken);
            paused = false;
            if (!decisionResult.Succeeded ||
                decisionResult.ActionResults.Count != 1 ||
                !string.Equals(decisionResult.ActionResults[0].Status, "accepted", StringComparison.Ordinal))
            {
                ActionResult? action = decisionResult.ActionResults.Count == 0 ? null : decisionResult.ActionResults[0];
                return Failure(
                    decisionResult.Error ?? new ArenaError(
                        action?.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        action?.Message ?? "The bounded budget route request was not accepted.",
                        "The common provider path did not deliver the valid minimum-budget route request.",
                        false),
                    "budget-action-accepted",
                    "The common replay decision did not accept the smallest valid maximum_budget value.",
                    checks);
            }

            ActionResult acceptedAction = decisionResult.ActionResults[0];
            checks.Add(Pass("budget-action-accepted", "The smallest schema-valid route budget crossed the normal replay, authorization, and GameScript boundary."));
            HashSet<string> persistedEventIds = [];
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
            {
                GameScriptSnapshotResult result = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
                if (!result.Succeeded || result.Snapshot is null)
                {
                    return Failure(result.Error, "budget-progress", "ArenaGS did not return a typed snapshot while the low-budget project recovered.", checks);
                }

                foreach (NormalizedGameEvent eventEntry in result.Snapshot.Events)
                {
                    if (persistedEventIds.Add(eventEntry.EventId))
                    {
                        await artifacts.AppendEventAsync(eventEntry, cancellationToken);
                    }
                }

                GameProjectState? project = result.Snapshot.Projects.SingleOrDefault(candidate =>
                    string.Equals(candidate.ActionId, acceptedAction.ActionId, StringComparison.Ordinal));
                if (project is not null && string.Equals(project.State, "failed", StringComparison.Ordinal))
                {
                    bool routeCreated = result.Snapshot.Routes.Any(candidate =>
                        string.Equals(candidate.ActionId, acceptedAction.ActionId, StringComparison.Ordinal));
                    bool failureEvent = result.Snapshot.Events.Any(entry =>
                        string.Equals(entry.EventCode, "ARENA-PROJECT-FAILED", StringComparison.Ordinal) &&
                        string.Equals(entry.CorrelationId, acceptedAction.CorrelationId, StringComparison.Ordinal));
                    if (!string.Equals(project.FailureCode, ArenaErrorCodes.ActionBudgetExceeded, StringComparison.Ordinal) ||
                        project.Spent != 0 ||
                        project.MaximumBudget != minimumValidBudget ||
                        routeCreated ||
                        result.Snapshot.Stations.Count != sourceSnapshot.Stations.Count ||
                        result.Snapshot.Vehicles.Count != sourceSnapshot.Vehicles.Count ||
                        !failureEvent)
                    {
                        return Phase03BridgeExtensionResult.Failure(
                            ArenaErrorCodes.ArtifactVerificationFailed,
                            "The low-budget route project did not stop with exact zero spend and no created route assets.",
                            checks);
                    }

                    checks.Add(Pass("budget-recovery", "ArenaGS classified the first unaffordable native command as a budget failure with zero spend, no stations, no vehicles, and no route."));
                    return Phase03BridgeExtensionResult.Success("The Phase 06 budget-boundary proof completed.", checks);
                }

                if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }

            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ActionVerificationTimedOut,
                "The minimum-budget route project did not reach its deterministic recovery result before the bounded timeout.",
                checks);
        }
        finally
        {
            if (paused)
            {
                _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
            }
        }
    }

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
