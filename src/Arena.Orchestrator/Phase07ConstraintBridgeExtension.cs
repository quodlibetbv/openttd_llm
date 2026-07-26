using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Demonstrates that a scenario constraint is blocked both before dispatch by
/// the orchestrator and independently by ArenaGS. The deliberately rejected
/// commands are retained as public action artifacts for inspection.
/// </summary>
public sealed class Phase07ConstraintBridgeExtension : IPhase03BridgeExtension
{
    private readonly ScenarioDocument _scenario;

    public Phase07ConstraintBridgeExtension(ScenarioDocument scenario)
    {
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
    }

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
                return Failure(pauseError, "constraint-pause", "The simulation could not be paused for the scenario constraint proof.", checks);
            }

            paused = true;
            GameScriptSnapshotResult initialSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!initialSnapshotResult.Succeeded || initialSnapshotResult.Snapshot is null || !initialSnapshotResult.Snapshot.Paused)
            {
                return Failure(initialSnapshotResult.Error, "constraint-observation", "ArenaGS did not provide a paused authoritative snapshot for the scenario constraint proof.", checks);
            }

            ObservationBuildContext observationContext = ScenarioLoader.CreateObservationContext(
                _scenario,
                context.RunId,
                _scenario.Scenario.ModelBudget.MaximumCalls,
                _scenario.Scenario.ModelBudget.MaximumOutputTokens,
                _scenario.Scenario.ModelBudget.MaximumRetries);
            ObservationBuildResult initialObservation = ObservationBuilder.Build(initialSnapshotResult.Snapshot, observationContext);
            await artifacts.AppendObservationAsync(
                new ObservationBuildRecord(initialObservation.Snapshot, initialObservation.Sha256, initialObservation.ReplaySha256),
                cancellationToken);
            foreach (NormalizedGameEvent eventEntry in initialSnapshotResult.Snapshot.Events)
            {
                await artifacts.AppendEventAsync(eventEntry, cancellationToken);
            }

            IReadOnlyList<GameTownState> towns = initialObservation.Snapshot.Sections.CandidateOpportunities.Towns;
            long availableBudget = initialObservation.Snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget;
            if (towns.Count < 2 || availableBudget < 1)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionConstraintViolation,
                    "The fixed smoke save does not expose two towns and a positive scenario-safe route budget for constraint verification.",
                    checks);
            }

            ScenarioActionConstraintContext constraints = ScenarioLoader.CreateActionConstraintContext(_scenario);
            ScenarioActionConstraintContext toolRestrictedConstraints = constraints with
            {
                AllowedTools = constraints.AllowedTools
                    .Where(tool => !string.Equals(tool, RoadToolCatalog.Wait, StringComparison.Ordinal))
                    .ToArray(),
            };
            ModelAction disallowedWaitAction = new()
            {
                Tool = RoadToolCatalog.Wait,
                Arguments = JsonSerializer.SerializeToElement(new { game_days = 1 }),
            };
            RoadActionValidationResult toolAllowlistValidation = ScenarioActionConstraintValidator.Validate(
                disallowedWaitAction,
                initialObservation.Snapshot,
                toolRestrictedConstraints);
            if (toolAllowlistValidation.IsValid ||
                !string.Equals(toolAllowlistValidation.ErrorCode, ArenaErrorCodes.ActionConstraintViolation, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The deliberate wait action did not isolate the orchestrator's scenario tool-allowlist constraint.",
                    checks);
            }

            ActionRequest toolOrchestratorRejectedRequest = CreateRequest(
                context.RunId,
                "constraint-tool-orchestrator",
                disallowedWaitAction,
                toolRestrictedConstraints);
            ActionResult toolOrchestratorRejectedResult = new()
            {
                ActionId = toolOrchestratorRejectedRequest.ActionId,
                RunId = context.RunId,
                CorrelationId = toolOrchestratorRejectedRequest.CorrelationId,
                Status = "rejected",
                ErrorCode = toolAllowlistValidation.ErrorCode,
                Message = toolAllowlistValidation.Message,
            };
            await artifacts.AppendActionAsync(
                CreateRecord(context.RunId, toolOrchestratorRejectedRequest, toolOrchestratorRejectedResult),
                cancellationToken);

            ActionRequest toolGameRejectedRequest = CreateRequest(
                context.RunId,
                "constraint-tool-gamescript",
                disallowedWaitAction,
                toolRestrictedConstraints);
            GameScriptActionResult toolGameExecution = await gameScript.ExecuteActionAsync(
                toolGameRejectedRequest,
                context.RequestTimeout,
                cancellationToken);
            ActionResult toolGameRejectedResult = toolGameExecution.Action ?? FailedResult(toolGameRejectedRequest, toolGameExecution.Error);
            await artifacts.AppendActionAsync(CreateRecord(context.RunId, toolGameRejectedRequest, toolGameRejectedResult), cancellationToken);
            if (!string.Equals(toolGameRejectedResult.Status, "rejected", StringComparison.Ordinal) ||
                !string.Equals(toolGameRejectedResult.ErrorCode, ArenaErrorCodes.ActionConstraintViolation, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    toolGameRejectedResult.ErrorCode ?? ArenaErrorCodes.ArtifactVerificationFailed,
                    "ArenaGS did not independently reject a scenario-disallowed wait action.",
                    checks);
            }

            ModelAction routeAction = new()
            {
                Tool = RoadToolCatalog.BuildTransportRoute,
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    mode = "road",
                    source_town_id = towns[0].TownId,
                    destination_town_id = towns[1].TownId,
                    cargo = "passengers",
                    initial_vehicle_count = 1,
                    maximum_budget = availableBudget,
                }),
            };
            IReadOnlySet<string> allowedTools = initialObservation.Snapshot.Sections.GoalContext.AllowedTools.ToHashSet(StringComparer.Ordinal);
            if (!RoadActionValidator.Validate(routeAction, initialObservation.Snapshot, allowedTools).IsValid ||
                !ScenarioActionConstraintValidator.Validate(routeAction, initialObservation.Snapshot, constraints).IsValid)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionConstraintViolation,
                    "The trusted setup route is not valid under the immutable road-profit scenario constraints.",
                    checks);
            }

            ActionRequest setupRequest = CreateRequest(context.RunId, "constraint-setup", routeAction, constraints);
            GameScriptActionResult setupExecution = await gameScript.ExecuteActionAsync(setupRequest, context.RequestTimeout, cancellationToken);
            ActionResult setupResult = setupExecution.Action ?? FailedResult(setupRequest, setupExecution.Error);
            await artifacts.AppendActionAsync(CreateRecord(context.RunId, setupRequest, setupResult), cancellationToken);
            if (!string.Equals(setupResult.Status, "accepted", StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    setupResult.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                    "ArenaGS did not accept the trusted setup route required to create one active project.",
                    checks);
            }

            GameScriptSnapshotResult activeProjectSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!activeProjectSnapshotResult.Succeeded || activeProjectSnapshotResult.Snapshot is null ||
                !activeProjectSnapshotResult.Snapshot.Projects.Any(project =>
                    string.Equals(project.ActionId, setupRequest.ActionId, StringComparison.Ordinal) &&
                    !string.Equals(project.State, "completed", StringComparison.Ordinal) &&
                    !string.Equals(project.State, "failed", StringComparison.Ordinal)))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "ArenaGS did not retain the accepted setup route as an active project while simulation was paused.",
                    checks);
            }

            ObservationBuildResult blockedObservation = ObservationBuilder.Build(activeProjectSnapshotResult.Snapshot, observationContext);
            RoadActionValidationResult typedValidation = RoadActionValidator.Validate(routeAction, blockedObservation.Snapshot, allowedTools);
            RoadActionValidationResult orchestratorValidation = ScenarioActionConstraintValidator.Validate(routeAction, blockedObservation.Snapshot, constraints);
            if (!typedValidation.IsValid || orchestratorValidation.IsValid ||
                !string.Equals(orchestratorValidation.ErrorCode, ArenaErrorCodes.ActionConstraintViolation, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The deliberate second route did not isolate the orchestrator's maximum-active-project scenario constraint.",
                    checks);
            }

            ActionRequest orchestratorRejectedRequest = CreateRequest(context.RunId, "constraint-orchestrator", routeAction, constraints);
            ActionResult orchestratorRejectedResult = new()
            {
                ActionId = orchestratorRejectedRequest.ActionId,
                RunId = context.RunId,
                CorrelationId = orchestratorRejectedRequest.CorrelationId,
                Status = "rejected",
                ErrorCode = orchestratorValidation.ErrorCode,
                Message = orchestratorValidation.Message,
            };
            await artifacts.AppendActionAsync(CreateRecord(context.RunId, orchestratorRejectedRequest, orchestratorRejectedResult), cancellationToken);

            ActionRequest gameRejectedRequest = CreateRequest(context.RunId, "constraint-gamescript", routeAction, constraints);
            GameScriptActionResult gameExecution = await gameScript.ExecuteActionAsync(gameRejectedRequest, context.RequestTimeout, cancellationToken);
            ActionResult gameRejectedResult = gameExecution.Action ?? FailedResult(gameRejectedRequest, gameExecution.Error);
            await artifacts.AppendActionAsync(CreateRecord(context.RunId, gameRejectedRequest, gameRejectedResult), cancellationToken);
            if (!string.Equals(gameRejectedResult.Status, "rejected", StringComparison.Ordinal) ||
                !string.Equals(gameRejectedResult.ErrorCode, ArenaErrorCodes.ActionConstraintViolation, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    gameRejectedResult.ErrorCode ?? ArenaErrorCodes.ArtifactVerificationFailed,
                    "ArenaGS did not independently reject the second route for the immutable maximum-active-project scenario constraint.",
                    checks);
            }

            GameScriptSnapshotResult finalSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!finalSnapshotResult.Succeeded || finalSnapshotResult.Snapshot is null ||
                finalSnapshotResult.Snapshot.Projects.Any(project =>
                    string.Equals(project.ActionId, gameRejectedRequest.ActionId, StringComparison.Ordinal)))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The ArenaGS-rejected scenario action unexpectedly created a persisted project.",
                    checks);
            }

            checks.Add(Pass("constraint-orchestrator", "The orchestrator recorded a scenario maximum-active-project rejection before any second action crossed AdminPort."));
            checks.Add(Pass("constraint-gamescript", "ArenaGS independently rejected the same scenario-constrained second route and created no project."));
            checks.Add(Pass("constraint-tool-orchestrator", "The orchestrator recorded a scenario tool-allowlist rejection before a disallowed wait action crossed AdminPort."));
            checks.Add(Pass("constraint-tool-gamescript", "ArenaGS independently rejected the same scenario-disallowed wait action."));
            return Phase03BridgeExtensionResult.Success("The Phase 07 scenario constraint boundary was enforced and recorded by both trusted layers.", checks);
        }
        finally
        {
            if (paused)
            {
                _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
            }
        }
    }

    private static ActionRequest CreateRequest(
        string runId,
        string prefix,
        ModelAction action,
        ScenarioActionConstraintContext constraints) =>
        new()
        {
            ActionId = prefix + "-action-1",
            RunId = runId,
            DecisionId = prefix + "-decision-1",
            CorrelationId = prefix + "-correlation-1",
            IdempotencyKey = prefix + "-key-1",
            Tool = action.Tool,
            Arguments = action.Arguments.Clone(),
            ConstraintContext = constraints,
        };

    private static RecordedAction CreateRecord(string runId, ActionRequest request, ActionResult result) => new()
    {
        SchemaVersion = ContractVersions.ObservationV1,
        RunId = runId,
        DecisionId = request.DecisionId,
        Request = request,
        Result = result,
    };

    private static ActionResult FailedResult(ActionRequest request, ArenaError? error) => new()
    {
        ActionId = request.ActionId,
        RunId = request.RunId,
        CorrelationId = request.CorrelationId,
        Status = "failed",
        ErrorCode = error?.Code ?? ArenaErrorCodes.AdminPortUnavailable,
        Message = error?.UserMessage ?? "ArenaGS did not return a typed action result.",
    };

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
