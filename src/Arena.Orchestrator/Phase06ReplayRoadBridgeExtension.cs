using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;
using System.Text.Json;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Runs the fixed-map Phase 06 proof through the normal replay-provider,
/// observation, authorization, AdminPort, and GameScript execution boundaries.
/// The model fixture selects a high-level route only; ArenaGS owns all tile
/// search, construction, vehicle selection, orders, and movement verification.
/// </summary>
public sealed class Phase06ReplayRoadBridgeExtension : IPhase03BridgeExtension
{
    internal const string FixtureRelativePath = "replays/phase-06-road-smoke.v1.json";
    internal const string SpecialLinkFixtureRelativePath = "replays/phase-06-road-special-link-smoke.v1.json";
    private const int VerificationPollLimit = 150;
    private readonly bool _verifyFleetExpansion;
    private readonly string _fixtureRelativePath;
    private readonly string? _requiredNativeLinkEventCode;

    /// <summary>
    /// The normal road smoke proves the fixed replay decision can create one
    /// operational route. Fleet smoke enables a second replay decision after
    /// that route is live, exercising the persisted expand-route state machine
    /// without giving a provider any game-side capability.
    /// </summary>
    public Phase06ReplayRoadBridgeExtension(bool verifyFleetExpansion = false)
        : this(verifyFleetExpansion, FixtureRelativePath, null)
    {
    }

    /// <summary>
    /// Creates the fixed-map smoke extension that must create a native road
    /// bridge before it can verify the route is operational.
    /// </summary>
    public static Phase06ReplayRoadBridgeExtension CreateSpecialLinkSmoke() =>
        new(false, SpecialLinkFixtureRelativePath, "ARENA-BRIDGE-CREATED");

    private Phase06ReplayRoadBridgeExtension(
        bool verifyFleetExpansion,
        string fixtureRelativePath,
        string? requiredNativeLinkEventCode)
    {
        _verifyFleetExpansion = verifyFleetExpansion;
        _fixtureRelativePath = fixtureRelativePath;
        _requiredNativeLinkEventCode = requiredNativeLinkEventCode;
    }

    public async Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<Phase03BridgeCheck> checks = [];
        ReplayFixture fixture;
        try
        {
            fixture = LoadFixture(context.Configuration.RepositoryRoot, _fixtureRelativePath);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ProviderConfigurationInvalid,
                ArtifactTextRedactor.Redact(exception.Message),
                checks);
        }

        if (!string.Equals(fixture.Provider, "replay", StringComparison.Ordinal) ||
            fixture.Steps.Count != 1 ||
            string.IsNullOrWhiteSpace(fixture.Model))
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ProviderConfigurationInvalid,
                "The fixed Phase 06 replay fixture does not declare one replay-provider decision.",
                checks);
        }

        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        using ObservationArtifactWriter artifacts = new(context.Paths, context.RunId);
        ProviderDecisionExecutionResult decisionResult = await ProviderDecisionExecutor.ExecuteAsync(
            gameScript,
            new ReplayModelProvider(fixture),
            artifacts,
            new ProviderDecisionExecutionOptions(
                ArenaRoadSmokeObservationContext.Create(context.RunId),
                fixture.Steps[0].Decision.DecisionId,
                fixture.Model,
                context.RequestTimeout),
            cancellationToken);

        if (!decisionResult.Succeeded)
        {
            return Failure(
                decisionResult.Error,
                "replay-decision",
                "The replay provider decision did not pass the paused authorization boundary.",
                checks);
        }

        if (decisionResult.ActionResults.Count != 1 ||
            !string.Equals(decisionResult.ActionResults[0].Status, "accepted", StringComparison.Ordinal))
        {
            ActionResult? action = decisionResult.ActionResults.Count == 0 ? null : decisionResult.ActionResults[0];
            return Phase03BridgeExtensionResult.Failure(
                action?.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                action?.Message ?? "The replay decision did not produce exactly one accepted route project.",
                checks);
        }

        ActionResult acceptedAction = decisionResult.ActionResults[0];
        checks.Add(Pass("replay-decision", "The replay provider returned a common ModelDecision while the simulation remained paused."));
        checks.Add(Pass("route-action-accepted", "The typed build_transport_route request crossed AdminPort and created one persisted GameScript project."));

        IReadOnlyCollection<string> initialEventIds = decisionResult.Observation?.Snapshot.Sections.RecentEvents
            .Select(eventEntry => eventEntry.EventId)
            .ToArray() ?? [];
        return await VerifyRouteAsync(
            context,
            gameScript,
            artifacts,
            acceptedAction,
            new HashSet<string>(initialEventIds, StringComparer.Ordinal),
            checks,
            cancellationToken);
    }

    private async Task<Phase03BridgeExtensionResult> VerifyRouteAsync(
        Phase03BridgeExtensionContext context,
        ArenaGameScriptClient gameScript,
        ObservationArtifactWriter artifacts,
        ActionResult acceptedAction,
        HashSet<string> persistedEventIds,
        List<Phase03BridgeCheck> checks,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
        {
            GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
            {
                string diagnostics = context.Bridge.SafeDiagnostics.Count == 0
                    ? "none"
                    : string.Join(",", context.Bridge.SafeDiagnostics.TakeLast(8));
                string message = ArtifactTextRedactor.Redact(
                    snapshotResult.Error?.UserMessage ?? "no ArenaGS error message was available");
                return Failure(
                    snapshotResult.Error,
                    "route-progress",
                    "ArenaGS did not return a valid project-progress snapshot (ArenaGS message=" + message + "; safe transport diagnostics=" + diagnostics + ").",
                    checks);
            }

            await AppendNewEventsAsync(
                artifacts,
                snapshotResult.Snapshot.Events,
                persistedEventIds,
                cancellationToken);

            GameProjectState? project = snapshotResult.Snapshot.Projects.SingleOrDefault(candidate =>
                string.Equals(candidate.ActionId, acceptedAction.ActionId, StringComparison.Ordinal));
            if (project is not null && string.Equals(project.State, "completed", StringComparison.Ordinal))
            {
                GameRouteState? route = snapshotResult.Snapshot.Routes.SingleOrDefault(candidate =>
                    candidate.Operational &&
                    candidate.VehicleIds.Count > 0 &&
                    string.Equals(candidate.ActionId, acceptedAction.ActionId, StringComparison.Ordinal));
                if (route is null || project.Spent > project.MaximumBudget)
                {
                    return Phase03BridgeExtensionResult.Failure(
                        ArenaErrorCodes.ArtifactVerificationFailed,
                        "ArenaGS marked a route project completed without an operational route or within-budget spend evidence.",
                        checks);
                }

                if (_requiredNativeLinkEventCode is not null)
                {
                    bool createdRequiredLink = snapshotResult.Snapshot.Events.Any(eventEntry =>
                        string.Equals(eventEntry.EventCode, _requiredNativeLinkEventCode, StringComparison.Ordinal) &&
                        string.Equals(eventEntry.CorrelationId, acceptedAction.CorrelationId, StringComparison.Ordinal));
                    if (!createdRequiredLink)
                    {
                        return Phase03BridgeExtensionResult.Failure(
                            ArenaErrorCodes.ArtifactVerificationFailed,
                            "The fixed special-link route completed without its required correlated native bridge event.",
                            checks);
                    }

                    checks.Add(Pass(
                        "native-special-link",
                        "The fixed obstacle route created its required correlated native road bridge before becoming operational."));
                }

                checks.Add(Pass("route-operational", "The project completed within its declared budget with valid route entities and demonstrated movement."));
                checks.Add(Pass("route-events", "The normalized GameScript route-progress and final events were persisted under the isolated run root."));
                if (_verifyFleetExpansion)
                {
                    return await VerifyFleetExpansionAsync(
                        context,
                        gameScript,
                        artifacts,
                        route,
                        persistedEventIds,
                        checks,
                        cancellationToken);
                }

                return Phase03BridgeExtensionResult.Success(
                    _requiredNativeLinkEventCode is null
                        ? "The Phase 06 replay road proof completed."
                        : "The Phase 06 replay native special-link proof completed.",
                    checks);
            }

            if (project is not null && string.Equals(project.State, "failed", StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    project.FailureCode ?? ArenaErrorCodes.ActionConstraintViolation,
                    "ArenaGS safely failed the persisted road project before it became operational: " +
                    (snapshotResult.Snapshot.Events.Count > 0
                        ? snapshotResult.Snapshot.Events[^1].PublicSummary
                        : "no public failure event was available."),
                    checks);
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return Phase03BridgeExtensionResult.Failure(
            ArenaErrorCodes.ActionVerificationTimedOut,
            "The replay road project did not complete within the bounded operational-verification window.",
            checks);
    }

    private static async Task<Phase03BridgeExtensionResult> VerifyFleetExpansionAsync(
        Phase03BridgeExtensionContext context,
        ArenaGameScriptClient gameScript,
        ObservationArtifactWriter artifacts,
        GameRouteState route,
        HashSet<string> persistedEventIds,
        List<Phase03BridgeCheck> checks,
        CancellationToken cancellationToken)
    {
        ArenaError? pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
        if (pauseError is not null)
        {
            return Failure(
                pauseError,
                "fleet-pause",
                "The simulation could not be paused before constructing the deterministic fleet replay fixture.",
                checks);
        }

        try
        {
            /* Build the replay fixture from a snapshot obtained after pause.
             * ProviderDecisionExecutor reads its observation in the same pause
             * boundary, so the expected replay hash cannot race a simulated
             * day, company transaction, or vehicle movement. */
            GameScriptSnapshotResult pausedRead = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!pausedRead.Succeeded || pausedRead.Snapshot is null || !pausedRead.Snapshot.Paused)
            {
                return Failure(
                    pausedRead.Error,
                    "fleet-observation",
                    "ArenaGS did not return a paused authoritative snapshot for the fleet replay fixture.",
                    checks);
            }

            GameRouteState? pausedRoute = pausedRead.Snapshot.Routes.SingleOrDefault(candidate =>
                candidate.Operational &&
                string.Equals(candidate.RouteId, route.RouteId, StringComparison.Ordinal) &&
                string.Equals(candidate.ActionId, route.ActionId, StringComparison.Ordinal));
            if (pausedRoute is null || pausedRoute.VehicleIds.Count is < 1 or >= 8)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The completed fixed-map route did not expose a safe target size for the fleet-expansion acceptance check.",
                    checks);
            }

            ObservationBuildContext observationContext = ArenaFleetSmokeObservationContext.Create(context.RunId);
            ObservationBuildResult sourceObservation = ObservationBuilder.Build(pausedRead.Snapshot, observationContext);
            long maximumBudget = sourceObservation.Snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget;
            if (maximumBudget < 1)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionConstraintViolation,
                    "The completed fixed-map route has no remaining bounded project budget for a fleet-expansion acceptance check.",
                    checks);
            }

            string decisionId = "decision-fleet-expand-1";
            JsonElement arguments = JsonSerializer.SerializeToElement(new
            {
                route_id = pausedRoute.RouteId,
                vehicle_count = pausedRoute.VehicleIds.Count + 1,
                maximum_budget = maximumBudget,
            });
            ReplayFixture fixture = new()
            {
                FixtureVersion = "1.0",
                Provider = "replay",
                Model = "phase-06-fleet-smoke",
                Steps =
                [
                    new ReplayStep
                    {
                        ExpectedObservationSha256 = sourceObservation.ReplaySha256,
                        Decision = new ModelDecision
                        {
                            DecisionId = decisionId,
                            PublicSummary = "Expand the verified route by one compatible passenger vehicle.",
                            Observations = ["The route is operational and has a bounded purchase budget."],
                            Actions =
                            [
                                new ModelAction
                                {
                                    Tool = RoadToolCatalog.ExpandRoute,
                                    Arguments = arguments,
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
            if (!decisionResult.Succeeded ||
                decisionResult.ActionResults.Count != 1 ||
                !string.Equals(decisionResult.ActionResults[0].Status, "accepted", StringComparison.Ordinal))
            {
                ActionResult? action = decisionResult.ActionResults.Count == 0 ? null : decisionResult.ActionResults[0];
                return Failure(
                    decisionResult.Error ?? new ArenaError(
                        action?.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        action?.Message ?? "The replay fleet decision did not produce one accepted expansion action.",
                        "The fleet-expansion acceptance decision stopped before a safe GameScript state boundary.",
                        false),
                    "fleet-action-accepted",
                    "The common replay decision path did not accept a bounded expand_route request for the operational route.",
                    checks);
            }

            ActionResult acceptedAction = decisionResult.ActionResults[0];
            checks.Add(Pass("fleet-action-accepted", "A second common replay decision accepted a bounded expand_route request while the simulation remained paused."));
            ActionRequest retryRequest = new()
            {
                ActionId = "action-" + decisionId + "-1",
                RunId = context.RunId,
                DecisionId = decisionId,
                CorrelationId = "correlation-" + decisionId + "-1",
                IdempotencyKey = "idempotency-" + decisionId + "-1",
                Tool = RoadToolCatalog.ExpandRoute,
                Arguments = arguments.Clone(),
            };
            GameScriptActionResult retry = await gameScript.ExecuteActionAsync(
                retryRequest,
                context.RequestTimeout,
                cancellationToken);
            ActionResult retryResult = retry.Action ?? new ActionResult
            {
                ActionId = retryRequest.ActionId,
                RunId = context.RunId,
                CorrelationId = retryRequest.CorrelationId,
                Status = "failed",
                ErrorCode = retry.Error?.Code ?? ArenaErrorCodes.AdminPortUnavailable,
                Message = retry.Error?.UserMessage ?? "The idempotent fleet retry did not return an ArenaGS result.",
            };
            await artifacts.AppendActionAsync(new RecordedAction
            {
                SchemaVersion = ContractVersions.ObservationV1,
                RunId = context.RunId,
                DecisionId = decisionId,
                Request = retryRequest,
                Result = retryResult,
            }, cancellationToken);
            if (!retry.Succeeded ||
                (retryResult.Status != "accepted" && retryResult.Status != "duplicate"))
            {
                return Failure(
                    retry.Error ?? new ArenaError(
                        retryResult.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        retryResult.Message,
                        "The duplicate fleet request did not reach the persisted idempotency boundary.",
                        false),
                    "fleet-idempotency",
                    "Repeating the accepted fleet action with its original idempotency key was not safely deduplicated.",
                    checks);
            }

            checks.Add(Pass("fleet-idempotency", "Repeating the accepted fleet action with its original idempotency key did not create a second persisted adjustment."));
            return await VerifyFleetExpansionProgressAsync(
                context,
                gameScript,
                artifacts,
                pausedRoute,
                acceptedAction,
                pausedRoute.VehicleIds.Count + 1,
                persistedEventIds,
                checks,
                cancellationToken);
        }
        finally
        {
            /* ProviderDecisionExecutor normally resumes itself. Repeating the
             * idempotent resume request also covers an early fixture failure
             * before it is invoked, leaving no fleet smoke run paused. */
            _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
        }
    }

    private static async Task<Phase03BridgeExtensionResult> VerifyFleetExpansionProgressAsync(
        Phase03BridgeExtensionContext context,
        ArenaGameScriptClient gameScript,
        ObservationArtifactWriter artifacts,
        GameRouteState originalRoute,
        ActionResult acceptedAction,
        int targetVehicleCount,
        HashSet<string> persistedEventIds,
        List<Phase03BridgeCheck> checks,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
        {
            GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
            {
                return Failure(
                    snapshotResult.Error,
                    "fleet-progress",
                    "ArenaGS did not return a valid snapshot while the accepted fleet expansion was advancing.",
                    checks);
            }

            await AppendNewEventsAsync(artifacts, snapshotResult.Snapshot.Events, persistedEventIds, cancellationToken);
            GameProjectState? project = snapshotResult.Snapshot.Projects.SingleOrDefault(candidate =>
                string.Equals(candidate.ActionId, originalRoute.ActionId, StringComparison.Ordinal));
            GameRouteState? route = snapshotResult.Snapshot.Routes.SingleOrDefault(candidate =>
                candidate.Operational &&
                string.Equals(candidate.RouteId, originalRoute.RouteId, StringComparison.Ordinal) &&
                string.Equals(candidate.ActionId, originalRoute.ActionId, StringComparison.Ordinal));

            if (project is not null &&
                string.Equals(project.State, "completed", StringComparison.Ordinal) &&
                route is not null &&
                route.VehicleIds.Count == targetVehicleCount)
            {
                bool emittedCompletion = snapshotResult.Snapshot.Events.Any(eventEntry =>
                    string.Equals(eventEntry.EventCode, "ARENA-ROUTE-FLEET-UPDATED", StringComparison.Ordinal) &&
                    string.Equals(eventEntry.CorrelationId, acceptedAction.CorrelationId, StringComparison.Ordinal));
                if (!emittedCompletion)
                {
                    return Phase03BridgeExtensionResult.Failure(
                        ArenaErrorCodes.ArtifactVerificationFailed,
                        "ArenaGS changed the fleet count without a correlated normalized fleet-completion event.",
                        checks);
                }

                checks.Add(Pass("fleet-operational", "The persisted native fleet expansion completed with one additional operational route vehicle and a correlated public event."));
                return Phase03BridgeExtensionResult.Success("The Phase 06 replay road and fleet proof completed.", checks);
            }

            NormalizedGameEvent? failureEvent = snapshotResult.Snapshot.Events.LastOrDefault(eventEntry =>
                string.Equals(eventEntry.EventCode, "ARENA-ROUTE-FLEET-FAILED", StringComparison.Ordinal) &&
                string.Equals(eventEntry.CorrelationId, acceptedAction.CorrelationId, StringComparison.Ordinal));
            if (failureEvent is not null)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionConstraintViolation,
                    "ArenaGS safely stopped the persisted fleet expansion: " + failureEvent.PublicSummary,
                    checks);
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return Phase03BridgeExtensionResult.Failure(
            ArenaErrorCodes.ActionVerificationTimedOut,
            "The accepted persisted fleet expansion did not complete within the bounded operational-verification window.",
            checks);
    }

    private static async Task AppendNewEventsAsync(
        ObservationArtifactWriter artifacts,
        IReadOnlyList<NormalizedGameEvent> events,
        HashSet<string> persistedEventIds,
        CancellationToken cancellationToken)
    {
        foreach (NormalizedGameEvent eventEntry in events)
        {
            if (persistedEventIds.Add(eventEntry.EventId))
            {
                await artifacts.AppendEventAsync(eventEntry, cancellationToken);
            }
        }
    }

    internal static ReplayFixture LoadFixture(string repositoryRoot)
        => LoadFixture(repositoryRoot, FixtureRelativePath);

    private static ReplayFixture LoadFixture(string repositoryRoot, string fixtureRelativePath)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string fixturePath = Path.GetFullPath(Path.Combine(root, fixtureRelativePath));
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fixturePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(fixturePath))
        {
            throw new InvalidOperationException("The checked-in Phase 06 replay fixture is unavailable.");
        }

        using FileStream stream = File.OpenRead(fixturePath);
        return ReplayFixtureReader.Read(stream);
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
