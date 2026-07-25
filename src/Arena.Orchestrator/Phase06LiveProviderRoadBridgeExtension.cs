using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Runs the same trusted route execution path as the replay smoke, but obtains
/// the one strategic decision from a configured common provider adapter. The
/// provider sees only the public observation and typed tool list; ArenaGS
/// remains the sole owner of route construction and verification.
/// </summary>
public sealed class Phase06LiveProviderRoadBridgeExtension : IPhase03BridgeExtension
{
    private const int VerificationPollLimit = 150;
    private readonly IModelProvider _provider;
    private readonly string _model;

    public Phase06LiveProviderRoadBridgeExtension(IModelProvider provider, string model)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("The configured provider model is required.", nameof(model))
            : model;
    }

    public async Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<Phase03BridgeCheck> checks = [];
        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        using ObservationArtifactWriter artifacts = new(context.Paths, context.RunId);
        ObservationBuildContext observationContext = ArenaRoadSmokeObservationContext.Create(context.RunId);
        ProviderDecisionExecutionResult decisionResult = await ProviderDecisionExecutor.ExecuteAsync(
            gameScript,
            _provider,
            artifacts,
            new ProviderDecisionExecutionOptions(
                observationContext,
                "decision-live-road-1",
                _model,
                context.RequestTimeout,
                MaximumActions: 1),
            cancellationToken);
        if (!decisionResult.Succeeded)
        {
            return Failure(
                decisionResult.Error,
                "live-provider-decision",
                "The configured provider decision did not complete the paused authorization boundary.",
                checks);
        }

        if (decisionResult.Decision is null ||
            decisionResult.Decision.Actions.Count != 1 ||
            !string.Equals(decisionResult.Decision.Actions[0].Tool, RoadToolCatalog.BuildTransportRoute, StringComparison.Ordinal) ||
            decisionResult.ActionResults.Count != 1 ||
            !string.Equals(decisionResult.ActionResults[0].Status, "accepted", StringComparison.Ordinal))
        {
            ActionResult? action = decisionResult.ActionResults.Count == 0 ? null : decisionResult.ActionResults[0];
            return Phase03BridgeExtensionResult.Failure(
                action?.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                "The configured provider did not select exactly one accepted build_transport_route action for the declared one-decision road objective.",
                checks);
        }

        ActionResult acceptedAction = decisionResult.ActionResults[0];
        checks.Add(Pass("live-provider-decision", "The configured provider returned the common ModelDecision contract while the simulation remained paused."));
        checks.Add(Pass("live-route-action-accepted", "The live provider's typed route action was authorized and persisted by ArenaGS without direct provider game access."));
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

    private static async Task<Phase03BridgeExtensionResult> VerifyRouteAsync(
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
                return Failure(
                    snapshotResult.Error,
                    "live-route-progress",
                    "ArenaGS did not return an authoritative project-progress snapshot after the live provider decision.",
                    checks);
            }

            foreach (NormalizedGameEvent eventEntry in snapshotResult.Snapshot.Events)
            {
                if (persistedEventIds.Add(eventEntry.EventId))
                {
                    await artifacts.AppendEventAsync(eventEntry, cancellationToken);
                }
            }

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
                        "ArenaGS completed the live-provider route without within-budget operational-route evidence.",
                        checks);
                }

                checks.Add(Pass("live-route-operational", "The live provider's route completed within budget with valid stations, depot access, orders, and demonstrated road movement."));
                checks.Add(Pass("live-route-events", "The live provider decision and all correlated normalized route events were finalized under the isolated run root."));
                return Phase03BridgeExtensionResult.Success("The live-provider Phase 06 road proof completed.", checks);
            }

            if (project is not null && string.Equals(project.State, "failed", StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    project.FailureCode ?? ArenaErrorCodes.ActionConstraintViolation,
                    "ArenaGS safely failed the live-provider route project before it became operational.",
                    checks);
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return Phase03BridgeExtensionResult.Failure(
            ArenaErrorCodes.ActionVerificationTimedOut,
            "The live-provider route project did not become operational within the bounded verification window.",
            checks);
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
