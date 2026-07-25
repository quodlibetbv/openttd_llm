using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Scoring;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Runs the first immutable road-profit benchmark through the normal common
/// provider boundary, then seals independently verifiable metrics, score, and
/// input evidence. A replay fixture can be constructed from the paused
/// observation; a live provider can ignore that factory argument and receive
/// the exact same common request later in the executor.
/// </summary>
public sealed class Phase07RoadProfitBridgeExtension : IPhase03BridgeExtension
{
    private const int VerificationPollLimit = 180;
    private const int MetricSampleInterval = 5;
    private readonly ScenarioDocument _scenario;
    private readonly string _model;
    private readonly Func<ObservationBuildResult, IModelProvider> _providerFactory;

    public Phase07RoadProfitBridgeExtension(
        ScenarioDocument scenario,
        string model,
        Func<ObservationBuildResult, IModelProvider> providerFactory)
    {
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        _model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("The benchmark model identifier is required.", nameof(model))
            : model;
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
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
                "The trusted benchmark server session did not expose supervisor-only final-save control.");
        }

        List<Phase03BridgeCheck> checks = [];
        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        ObservationArtifactWriter? artifacts = null;
        bool paused = false;
        try
        {
            BenchmarkInputCapture inputCapture = await BenchmarkInputSnapshotter.CaptureAsync(
                context.Configuration.RepositoryRoot,
                context.Configuration,
                context.Paths,
                _scenario,
                context.StartingSavePath,
                context.GameSettingsPath,
                cancellationToken);
            artifacts = new ObservationArtifactWriter(context.Paths, context.RunId);
            ObservationBuildContext observationContext = ScenarioLoader.CreateObservationContext(
                _scenario,
                context.RunId,
                _scenario.Scenario.ModelBudget.MaximumCalls,
                _scenario.Scenario.ModelBudget.MaximumOutputTokens,
                _scenario.Scenario.ModelBudget.MaximumRetries);
            ScenarioActionConstraintContext actionConstraints = ScenarioLoader.CreateActionConstraintContext(_scenario);

            ArenaError? pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
            if (pauseError is not null)
            {
                return Failure(pauseError, "benchmark-pause", "The simulation could not be paused before the benchmark provider request.", checks);
            }

            paused = true;
            GameScriptSnapshotResult initialSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!initialSnapshotResult.Succeeded || initialSnapshotResult.Snapshot is null || !initialSnapshotResult.Snapshot.Paused)
            {
                return Failure(initialSnapshotResult.Error, "benchmark-observation", "ArenaGS did not return a paused authoritative snapshot for the benchmark request.", checks);
            }

            ObservationBuildResult normalizedInitial = ObservationBuilder.Build(initialSnapshotResult.Snapshot, observationContext);
            await artifacts.AppendMetricAsync(
                BenchmarkMetricCollector.Capture(context.RunId, "metric-initial-1", "initial", initialSnapshotResult.Snapshot, 0, 0),
                cancellationToken);
            IModelProvider provider = _providerFactory(normalizedInitial)
                ?? throw new InvalidOperationException("The benchmark provider factory returned no provider.");
            ProviderDecisionExecutionResult decisionResult = await ProviderDecisionExecutor.ExecuteAsync(
                gameScript,
                provider,
                artifacts,
                new ProviderDecisionExecutionOptions(
                    observationContext,
                    "decision-road-profit-1",
                    _model,
                    context.RequestTimeout,
                    MaximumActions: 1,
                    ResumeAfterActionHandling: false,
                    ConstraintContext: actionConstraints),
                cancellationToken);
            if (!decisionResult.Succeeded ||
                decisionResult.Decision is null ||
                decisionResult.ActionResults.Count != 1 ||
                !string.Equals(decisionResult.ActionResults[0].Status, "accepted", StringComparison.Ordinal))
            {
                ActionResult? action = decisionResult.ActionResults.Count == 0 ? null : decisionResult.ActionResults[0];
                return Failure(
                    decisionResult.Error ?? new ArenaError(
                        action?.ErrorCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        action?.Message ?? "The benchmark provider did not produce one accepted route action.",
                        "The common provider boundary stopped before an accepted benchmark route action.",
                        false),
                    "benchmark-action-accepted",
                    "The benchmark provider did not produce exactly one accepted build_transport_route action while simulation was paused.",
                    checks);
            }

            ActionResult acceptedAction = decisionResult.ActionResults[0];
            if (!string.Equals(decisionResult.Decision.Actions[0].Tool, RoadToolCatalog.BuildTransportRoute, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ActionConstraintViolation,
                    "The initial road-profit scenario requires one accepted build_transport_route action.",
                    checks);
            }

            int invalidDecisionCount = decisionResult.ActionResults.Count(result => string.Equals(result.Status, "rejected", StringComparison.Ordinal));
            int constraintViolationCount = decisionResult.ActionResults.Count(result =>
                string.Equals(result.ErrorCode, ArenaErrorCodes.ActionConstraintViolation, StringComparison.Ordinal));
            checks.Add(Pass("benchmark-inputs", "The scenario, fixed starting save, content, settings, prompt, schemas, retry policy, and end condition were captured before provider execution."));
            checks.Add(Pass("benchmark-request", "The replay or live provider received the common normalized observation and typed scenario tool surface while simulation was paused."));
            checks.Add(Pass("benchmark-action-accepted", "ArenaGS accepted one scenario-constrained passenger route project through the normal AdminPort action boundary."));

            ArenaError? resumeError = await gameScript.ResumeAsync(context.RequestTimeout, cancellationToken);
            if (resumeError is not null)
            {
                return Failure(resumeError, "benchmark-resume", "The simulation could not advance the accepted benchmark route project.", checks);
            }

            paused = false;
            HashSet<string> persistedEvents = decisionResult.Observation?.Snapshot.Sections.RecentEvents
                .Select(entry => entry.EventId)
                .ToHashSet(StringComparer.Ordinal) ?? [];
            List<BenchmarkMetricSnapshot> periodicMetrics = [];
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            for (int attempt = 0; attempt < VerificationPollLimit; attempt++)
            {
                GameScriptSnapshotResult progress = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
                if (!progress.Succeeded || progress.Snapshot is null)
                {
                    return Failure(progress.Error, "benchmark-progress", "ArenaGS did not return authoritative progress metrics while the benchmark route was executing.", checks);
                }

                foreach (NormalizedGameEvent eventEntry in progress.Snapshot.Events)
                {
                    if (persistedEvents.Add(eventEntry.EventId))
                    {
                        await artifacts.AppendEventAsync(eventEntry, cancellationToken);
                    }
                }

                if ((attempt + 1) % MetricSampleInterval == 0)
                {
                    BenchmarkMetricSnapshot periodic = BenchmarkMetricCollector.Capture(
                        context.RunId,
                        "metric-periodic-" + (periodicMetrics.Count + 1),
                        "periodic",
                        progress.Snapshot,
                        invalidDecisionCount,
                        constraintViolationCount);
                    periodicMetrics.Add(periodic);
                    await artifacts.AppendMetricAsync(periodic, cancellationToken);
                }

                GameProjectState? project = progress.Snapshot.Projects.SingleOrDefault(candidate =>
                    string.Equals(candidate.ActionId, acceptedAction.ActionId, StringComparison.Ordinal));
                GameRouteState? route = progress.Snapshot.Routes.SingleOrDefault(candidate =>
                    string.Equals(candidate.ActionId, acceptedAction.ActionId, StringComparison.Ordinal) &&
                    candidate.Operational &&
                    candidate.VehicleIds.Count > 0);
                if (project is not null && string.Equals(project.State, "completed", StringComparison.Ordinal) && route is not null)
                {
                    if (project.Spent > project.MaximumBudget)
                    {
                        return Phase03BridgeExtensionResult.Failure(
                            ArenaErrorCodes.ArtifactVerificationFailed,
                            "ArenaGS completed the benchmark project without within-budget spend evidence.",
                            checks);
                    }

                    return await FinalizeBenchmarkAsync(
                        context,
                        gameScript,
                        artifacts,
                        provider,
                        inputCapture,
                        periodicMetrics,
                        invalidDecisionCount,
                        constraintViolationCount,
                        checks,
                        cancellationToken);
                }

                if (project is not null && string.Equals(project.State, "failed", StringComparison.Ordinal))
                {
                    return Phase03BridgeExtensionResult.Failure(
                        project.FailureCode ?? ArenaErrorCodes.ActionConstraintViolation,
                        "ArenaGS safely failed the benchmark road project before it met the declared end condition.",
                        checks);
                }

                if (attempt + 1 < VerificationPollLimit && !await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }

            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ActionVerificationTimedOut,
                "The road-profit benchmark did not reach its declared completed-route end condition before the bounded verification window.",
                checks);
        }
        catch (ArgumentException exception)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ScenarioInvalid,
                ArtifactTextRedactor.Redact(exception.Message),
                checks);
        }
        catch (InvalidOperationException exception)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.RunFinalizationFailed,
                ArtifactTextRedactor.Redact(exception.Message),
                checks);
        }
        finally
        {
            artifacts?.Dispose();
            if (paused)
            {
                _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
            }
        }
    }

    private async Task<Phase03BridgeExtensionResult> FinalizeBenchmarkAsync(
        Phase03BridgeExtensionContext context,
        ArenaGameScriptClient gameScript,
        ObservationArtifactWriter artifacts,
        IModelProvider provider,
        BenchmarkInputCapture inputCapture,
        IReadOnlyList<BenchmarkMetricSnapshot> periodicMetrics,
        int invalidDecisionCount,
        int constraintViolationCount,
        List<Phase03BridgeCheck> checks,
        CancellationToken cancellationToken)
    {
        ArenaError? pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
        if (pauseError is not null)
        {
            return Failure(pauseError, "benchmark-final-pause", "The simulation could not be paused before final authoritative metric capture.", checks);
        }

        bool finalPaused = true;
        try
        {
            GameScriptSnapshotResult finalSnapshotResult = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!finalSnapshotResult.Succeeded || finalSnapshotResult.Snapshot is null || !finalSnapshotResult.Snapshot.Paused)
            {
                return Failure(finalSnapshotResult.Error, "benchmark-final-metrics", "ArenaGS did not return a paused authoritative final metric snapshot.", checks);
            }

            BenchmarkMetricSnapshot finalMetrics = BenchmarkMetricCollector.Capture(
                context.RunId,
                "metric-final-1",
                "final",
                finalSnapshotResult.Snapshot,
                invalidDecisionCount,
                constraintViolationCount);
            if (!AreObjectivesSatisfied(finalMetrics.Metrics) ||
                !string.Equals(_scenario.Scenario.EndCondition.Type, "goal_completed", StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ArtifactVerificationFailed,
                    "The benchmark route completed without satisfying the scenario's declared goal-completed end condition.",
                    checks);
            }

            await artifacts.AppendMetricAsync(finalMetrics, cancellationToken);
            await BenchmarkArtifactStore.WriteFinalMetricsAsync(context.Paths, finalMetrics, cancellationToken);
            ScoreResult score = new RoadProfitScoreCalculator().Calculate(new ScoreInput(
                _scenario.Scenario,
                finalMetrics,
                periodicMetrics));
            await BenchmarkArtifactStore.WriteScoreAsync(context.Paths, score, cancellationToken);
            Phase03SaveLoadResult finalSave = await context.SaveLoadController!.SaveFinalAsync(cancellationToken);
            if (!finalSave.Succeeded || !File.Exists(context.Paths.Resolve("final-save.sav")))
            {
                return Phase03BridgeExtensionResult.Failure(
                    finalSave.ErrorCode ?? ArenaErrorCodes.RunArtifactMissing,
                    "The benchmark could not preserve its final OpenTTD save artifact.",
                    checks);
            }

            ArenaError? resumeError = await gameScript.ResumeAsync(context.RequestTimeout, cancellationToken);
            if (resumeError is not null)
            {
                return Failure(resumeError, "benchmark-final-resume", "The benchmark server could not resume after final save capture.", checks);
            }

            finalPaused = false;

            IReadOnlyCollection<string> artifactPaths = inputCapture.ArtifactRelativePaths
                .Concat(
                [
                    ObservationArtifactWriter.ObservationsFileName,
                    ObservationArtifactWriter.GameEventsFileName,
                    ObservationArtifactWriter.DecisionsFileName,
                    ObservationArtifactWriter.ProviderUsageFileName,
                    ObservationArtifactWriter.ActionsFileName,
                    ObservationArtifactWriter.MetricsFileName,
                    BenchmarkArtifactStore.FinalMetricsFileName,
                    BenchmarkArtifactStore.ScoreFileName,
                    "final-save.sav",
                ])
                .ToArray();
            _ = await RunManifestFinalizer.FinalizeAsync(
                context.Paths,
                new RunManifestDraft(
                    context.RunId,
                    DateTimeOffset.UtcNow,
                    "0.7.0",
                    GitRevisionResolver.Resolve(context.Configuration.RepositoryRoot),
                    provider.Descriptor.ProviderId,
                    _model,
                    _scenario.Scenario.ScenarioId,
                    _scenario.Scenario.Version,
                    new ContractVersionsUsed
                    {
                        Protocol = ContractVersions.ProtocolV1,
                        Observation = ContractVersions.ObservationV1,
                        Action = ContractVersions.ActionV1,
                        Goal = ContractVersions.ScenarioV1,
                        Score = ContractVersions.ScoreV1,
                        Manifest = ContractVersions.RunManifestV1,
                    },
                    inputCapture.Hashes),
                artifactPaths,
                cancellationToken);
            RunVerificationResult verification = await RunVerifier.VerifyAsync(context.Paths.Root, cancellationToken);
            if (!verification.Succeeded)
            {
                return Phase03BridgeExtensionResult.Failure(
                    verification.ErrorCode ?? ArenaErrorCodes.ArtifactVerificationFailed,
                    verification.Detail,
                    checks);
            }

            checks.Add(Pass("benchmark-objective", "The scenario's operational-route objective and goal-completed end condition were satisfied from authoritative GameScript metrics."));
            checks.Add(Pass("benchmark-score", "The pure road-profit scorer wrote a detailed score breakdown from periodic and final authoritative metrics only."));
            checks.Add(Pass("benchmark-manifest", "The final save, logs, metrics, score, and every benchmark-defining input were hash-sealed and independently verified."));
            return Phase03BridgeExtensionResult.Success("The immutable Phase 07 road-profit benchmark completed and its evidence artifacts verified.", checks);
        }
        finally
        {
            if (finalPaused)
            {
                _ = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
            }
        }
    }

    private bool AreObjectivesSatisfied(BenchmarkMetrics metrics) =>
        _scenario.Scenario.Objectives.All(objective => objective.Metric switch
        {
            "operational_route_count" => metrics.OperationalRouteCount >= objective.Minimum,
            "cargo_delivered" => metrics.QuarterlyCargoDelivered >= objective.Minimum,
            "operating_profit" => metrics.OperatingProfit >= objective.Minimum,
            _ => false,
        });

    public static IModelProvider CreateReplayProvider(ObservationBuildResult observation, ScenarioDocument scenario)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(scenario);
        IReadOnlyList<ObservationOpportunity> opportunities = observation.Snapshot.Sections.CandidateOpportunities.Opportunities;
        ObservationOpportunity? opportunity = opportunities.Count == 0 ? null : opportunities[0];
        long maximumBudget = observation.Snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget;
        if (opportunity is null || maximumBudget < 1)
        {
            throw new InvalidOperationException("The fixed smoke save does not expose a bounded authoritative passenger opportunity for the replay benchmark.");
        }

        ReplayFixture fixture = new()
        {
            FixtureVersion = "1.0",
            Provider = "replay",
            Model = "road-profit-replay-v1",
            Steps =
            [
                new ReplayStep
                {
                    ExpectedObservationSha256 = observation.ReplaySha256,
                    Decision = new ModelDecision
                    {
                        DecisionId = "decision-road-profit-1",
                        PublicSummary = "Build one affordable passenger route between the highest-ranked authoritative towns while preserving the scenario cash reserve.",
                        Observations = ["The fixed scenario exposes a bounded passenger opportunity and one project budget."],
                        Actions =
                        [
                            new ModelAction
                            {
                                Tool = RoadToolCatalog.BuildTransportRoute,
                                Arguments = JsonSerializer.SerializeToElement(new
                                {
                                    mode = "road",
                                    source_town_id = opportunity.SourceTownId,
                                    destination_town_id = opportunity.DestinationTownId,
                                    cargo = "passengers",
                                    initial_vehicle_count = 1,
                                    maximum_budget = maximumBudget,
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
        return new ReplayModelProvider(fixture);
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
