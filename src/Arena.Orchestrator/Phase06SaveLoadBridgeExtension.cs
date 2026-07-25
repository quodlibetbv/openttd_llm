using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Proves a real OpenTTD save/load at one exact persisted Phase 06 project
/// stage. The checkpoint pause is armed through a supervisor-only AdminPort
/// request; the replay provider still selects the same high-level route and
/// never receives process, filesystem, or checkpoint access.
/// </summary>
public sealed class Phase06SaveLoadBridgeExtension : IPhase03BridgeExtension
{
    private const int VerificationPollLimit = 180;
    private readonly string _checkpointStage;

    public Phase06SaveLoadBridgeExtension(string checkpointStage)
    {
        if (!RoadProjectCheckpointStages.All.Contains(checkpointStage))
        {
            throw new ArgumentException("The requested Phase 06 checkpoint stage is not supported.", nameof(checkpointStage));
        }

        _checkpointStage = checkpointStage;
    }

    public async Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SaveLoadController is null)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.RunPreparationFailed,
                "The trusted Phase 03 server session did not expose its supervisor-only save/load control.");
        }

        List<Phase03BridgeCheck> checks = [];
        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        using ObservationArtifactWriter artifacts = new(context.Paths, context.RunId);
        bool shouldResume = false;
        try
        {
            ReplayFixture fixture = Phase06ReplayRoadBridgeExtension.LoadFixture(context.Configuration.RepositoryRoot);
            if (!string.Equals(fixture.Provider, "replay", StringComparison.Ordinal) ||
                fixture.Steps.Count != 1 ||
                string.IsNullOrWhiteSpace(fixture.Model))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ProviderConfigurationInvalid,
                    "The fixed Phase 06 replay fixture does not declare one replay-provider route decision.",
                    checks);
            }

            ProviderDecisionExecutionResult decisionResult = await ProviderDecisionExecutor.ExecuteAsync(
                gameScript,
                new ReplayModelProvider(fixture),
                artifacts,
                new ProviderDecisionExecutionOptions(
                    ArenaRoadSmokeObservationContext.Create(context.RunId),
                    fixture.Steps[0].Decision.DecisionId,
                    fixture.Model,
                    context.RequestTimeout,
                    ResumeAfterActionHandling: false),
                cancellationToken);
            shouldResume = true;
            if (!decisionResult.Succeeded ||
                decisionResult.ActionResults.Count != 1 ||
                !string.Equals(decisionResult.ActionResults[0].Status, "accepted", StringComparison.Ordinal))
            {
                ActionResult? action = decisionResult.ActionResults.Count == 0 ? null : decisionResult.ActionResults[0];
                return Failure(
                    decisionResult.Error ?? new ArenaError(
                        action?.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        action?.Message ?? "The replay route decision was not accepted.",
                        "The provider-neutral route decision did not reach a safe GameScript boundary.",
                        false),
                    "replay-decision",
                    "The replay provider decision did not create one persisted route project while the simulation remained paused.",
                    checks);
            }

            ActionResult acceptedAction = decisionResult.ActionResults[0];
            GameScriptSnapshotResult acceptedSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!acceptedSnapshotResult.Succeeded || acceptedSnapshotResult.Snapshot is null || !acceptedSnapshotResult.Snapshot.Paused)
            {
                return Failure(
                    acceptedSnapshotResult.Error,
                    "project-proposed",
                    "ArenaGS did not retain the accepted route project at the required paused safe boundary.",
                    checks);
            }

            GameProjectState? acceptedProject = FindProject(acceptedSnapshotResult.Snapshot, acceptedAction.ActionId);
            if (acceptedProject is null || !string.Equals(acceptedProject.State, RoadProjectCheckpointStages.Proposed, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "ArenaGS accepted a route action without retaining its initial proposed project state.",
                    checks);
            }

            checks.Add(Pass("replay-decision", "The replay provider created one typed route project inside the paused provider-decision boundary."));
            ArenaError? armError = await gameScript.ArmProjectCheckpointAsync(
                acceptedProject.ProjectId,
                _checkpointStage,
                context.RequestTimeout,
                cancellationToken);
            if (armError is not null)
            {
                return Failure(
                    armError,
                    "checkpoint-arm",
                    "ArenaGS did not accept the trusted supervisor checkpoint for the requested project stage.",
                    checks);
            }

            GameScriptSnapshot? checkpointSnapshot;
            if (string.Equals(_checkpointStage, RoadProjectCheckpointStages.Proposed, StringComparison.Ordinal))
            {
                checkpointSnapshot = acceptedSnapshotResult.Snapshot;
            }
            else
            {
                ArenaError? resumeError = await gameScript.ResumeAsync(context.RequestTimeout, cancellationToken);
                if (resumeError is not null)
                {
                    return Failure(
                        resumeError,
                        "checkpoint-advance",
                        "The simulation could not advance to the requested persisted project checkpoint.",
                        checks);
                }

                checkpointSnapshot = await WaitForCheckpointAsync(
                    gameScript,
                    acceptedAction.ActionId,
                    _checkpointStage,
                    context.RequestTimeout,
                    cancellationToken);
            }

            if (checkpointSnapshot is null)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionVerificationTimedOut,
                    "The persisted route project did not reach the requested save/load checkpoint stage before the bounded timeout.",
                    checks);
            }

            GameProjectState? checkpointProject = FindProject(checkpointSnapshot, acceptedAction.ActionId);
            if (checkpointProject is null || !checkpointSnapshot.Paused ||
                !string.Equals(checkpointProject.State, _checkpointStage, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The requested save/load checkpoint did not hold the intended persisted project state at a paused boundary.",
                    checks);
            }

            checks.Add(Pass(
                "checkpoint-" + _checkpointStage,
                "ArenaGS paused exactly at the requested persisted project stage before the supervisor saved the isolated run."));
            string checkpointName = "phase06-save-load-" + _checkpointStage.Replace('_', '-');
            Phase03SaveLoadResult saveLoadResult = await context.SaveLoadController.SaveAndReloadAsync(
                checkpointName,
                cancellationToken);
            if (!saveLoadResult.Succeeded)
            {
                return Phase03BridgeExtensionResult.Failure(
                    saveLoadResult.ErrorCode ?? ArenaErrorCodes.RunArtifactMissing,
                    saveLoadResult.Detail,
                    checks);
            }

            if (!File.Exists(context.Paths.Resolve(Path.Combine("checkpoints", checkpointName + ".sav"))))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.RunArtifactMissing,
                    "The supervisor reported a save/load checkpoint without preserving its run-local save artifact.",
                    checks);
            }

            checks.Add(Pass("checkpoint-saved", "The generated OpenTTD checkpoint was finalized under the isolated run root before reload."));
            GameScriptSnapshot? restoredSnapshot = await WaitForRestoredCheckpointAsync(
                gameScript,
                acceptedAction.ActionId,
                _checkpointStage,
                context.RequestTimeout,
                cancellationToken);
            if (restoredSnapshot is null || !RestoredStateMatches(checkpointSnapshot, restoredSnapshot, acceptedAction.ActionId))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The reloaded GameScript state did not preserve the checkpointed project identity, budget accounting, or normalized event identities.",
                    checks);
            }

            checks.Add(Pass("checkpoint-restored", "Reload preserved the paused project identity, state, budget accounting, and normalized event IDs without synthetic duplicates."));
            ArenaError? finalResumeError = await gameScript.ResumeAsync(context.RequestTimeout, cancellationToken);
            if (finalResumeError is not null)
            {
                return Failure(
                    finalResumeError,
                    "checkpoint-resume",
                    "The reloaded project could not resume after its verified save/load boundary.",
                    checks);
            }

            shouldResume = false;
            GameScriptSnapshotResult resumedSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!resumedSnapshotResult.Succeeded || resumedSnapshotResult.Snapshot is null || resumedSnapshotResult.Snapshot.Paused)
            {
                return Failure(
                    resumedSnapshotResult.Error ?? new ArenaError(
                        ArenaErrorCodes.ArtifactVerificationFailed,
                        "The reloaded project remained paused after the trusted resume request.",
                        "The supervisor checkpoint boundary did not release after its verified reload.",
                        false),
                    "checkpoint-resume",
                    "The reloaded project did not leave its paused save/load boundary after resume.",
                    checks);
            }

            HashSet<string> eventIds = [];
            return await VerifyOperationalRouteAsync(
                context,
                gameScript,
                artifacts,
                acceptedAction,
                eventIds,
                checks,
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ProtocolInvalidMessage,
                ArtifactTextRedactor.Redact(exception.Message),
                checks);
        }
        finally
        {
            if (shouldResume)
            {
                _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
            }
        }
    }

    private static async Task<GameScriptSnapshot?> WaitForCheckpointAsync(
        ArenaGameScriptClient gameScript,
        string actionId,
        string stage,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
        {
            GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(requestTimeout, cancellationToken);
            if (snapshotResult.Succeeded && snapshotResult.Snapshot is { } snapshot)
            {
                GameProjectState? project = FindProject(snapshot, actionId);
                if (project is not null && snapshot.Paused && string.Equals(project.State, stage, StringComparison.Ordinal))
                {
                    return snapshot;
                }

                if (project is not null && (string.Equals(project.State, "failed", StringComparison.Ordinal) ||
                                            string.Equals(project.State, "completed", StringComparison.Ordinal)))
                {
                    return null;
                }
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return null;
    }

    private static async Task<GameScriptSnapshot?> WaitForRestoredCheckpointAsync(
        ArenaGameScriptClient gameScript,
        string actionId,
        string stage,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
        {
            GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(requestTimeout, cancellationToken);
            if (snapshotResult.Succeeded && snapshotResult.Snapshot is { } snapshot)
            {
                GameProjectState? project = FindProject(snapshot, actionId);
                if (project is not null && snapshot.Paused && string.Equals(project.State, stage, StringComparison.Ordinal))
                {
                    return snapshot;
                }
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return null;
    }

    private static bool RestoredStateMatches(
        GameScriptSnapshot checkpoint,
        GameScriptSnapshot restored,
        string actionId)
    {
        GameProjectState? checkpointProject = FindProject(checkpoint, actionId);
        GameProjectState? restoredProject = FindProject(restored, actionId);
        if (checkpointProject is null || restoredProject is null ||
            checkpointProject.ProjectId != restoredProject.ProjectId ||
            checkpointProject.ActionId != restoredProject.ActionId ||
            checkpointProject.State != restoredProject.State ||
            checkpointProject.Spent != restoredProject.Spent ||
            checkpointProject.MaximumBudget != restoredProject.MaximumBudget ||
            checkpointProject.FailureCode != restoredProject.FailureCode)
        {
            return false;
        }

        if (restored.Events.Select(entry => entry.EventId).Distinct(StringComparer.Ordinal).Count() != restored.Events.Count)
        {
            return false;
        }

        return checkpoint.Events.Select(EventIdentity).SequenceEqual(restored.Events.Select(EventIdentity));
    }

    private static string EventIdentity(NormalizedGameEvent entry) =>
        entry.EventId + "\u001f" + entry.EventCode + "\u001f" + entry.CorrelationId;

    private static async Task<Phase03BridgeExtensionResult> VerifyOperationalRouteAsync(
        Phase03BridgeExtensionContext context,
        ArenaGameScriptClient gameScript,
        ObservationArtifactWriter artifacts,
        ActionResult acceptedAction,
        HashSet<string> persistedEventIds,
        List<Phase03BridgeCheck> checks,
        CancellationToken cancellationToken)
    {
        string? lastProjectState = null;
        bool? lastPaused = null;
        string? lastEventCode = null;
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
        {
            GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
            {
                return Failure(
                    snapshotResult.Error,
                    "route-progress",
                    "ArenaGS did not return an authoritative snapshot while the restored route project was advancing.",
                    checks);
            }

            foreach (NormalizedGameEvent eventEntry in snapshotResult.Snapshot.Events)
            {
                if (persistedEventIds.Add(eventEntry.EventId))
                {
                    await artifacts.AppendEventAsync(eventEntry, cancellationToken);
                }
            }

            GameProjectState? project = FindProject(snapshotResult.Snapshot, acceptedAction.ActionId);
            lastProjectState = project?.State;
            lastPaused = snapshotResult.Snapshot.Paused;
            lastEventCode = snapshotResult.Snapshot.Events.Count == 0
                ? null
                : snapshotResult.Snapshot.Events[^1].EventCode;
            if (snapshotResult.Snapshot.Paused)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The restored route project remained paused after its trusted resume request.",
                    checks);
            }

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
                        "A reloaded route project completed without operational topology or within-budget evidence.",
                        checks);
                }

                checks.Add(Pass("route-operational", "The reloaded project resumed to a within-budget route with valid stations, depot access, orders, and demonstrated movement."));
                checks.Add(Pass("route-events", "The restored route progress and final normalized events were persisted without duplicate event IDs."));
                return Phase03BridgeExtensionResult.Success("The Phase 06 save/load road proof completed.", checks);
            }

            if (project is not null && string.Equals(project.State, "failed", StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    project.FailureCode ?? ArenaErrorCodes.ActionConstraintViolation,
                    "ArenaGS safely failed the reloaded route project before it became operational.",
                    checks);
            }

            if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return Phase03BridgeExtensionResult.Failure(
            ArenaErrorCodes.ActionVerificationTimedOut,
            "The reloaded route project did not become operational within the bounded verification window (last state=" +
            (lastProjectState ?? "missing") + "; paused=" +
            (lastPaused.HasValue ? lastPaused.Value ? "true" : "false" : "unknown") + "; last event=" +
            (lastEventCode ?? "none") + ").",
            checks);
    }

    private static GameProjectState? FindProject(GameScriptSnapshot snapshot, string actionId) =>
        snapshot.Projects.SingleOrDefault(candidate => string.Equals(candidate.ActionId, actionId, StringComparison.Ordinal));

    private static Phase03BridgeCheck Pass(string id, string detail) => new(id, true, null, detail);

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
}
