using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ProviderDecisionExecutionOptions(
    ObservationBuildContext ObservationContext,
    string DecisionId,
    string ProviderModel,
    TimeSpan RequestTimeout,
    int? MaximumSchemaCorrectionRetries = null,
    int MaximumActions = 8,
    bool ResumeAfterActionHandling = true,
    ScenarioActionConstraintContext? ConstraintContext = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ObservationContext);
        if (!ProtocolEnvelopeValidator.IsIdentifier(DecisionId) ||
            string.IsNullOrWhiteSpace(ProviderModel) ||
            ProviderModel.Length > 160 ||
            RequestTimeout is { TotalSeconds: < 8 or > 60 } ||
            MaximumSchemaCorrectionRetries is < 0 or > 1 ||
            (MaximumSchemaCorrectionRetries is { } configuredRetries && configuredRetries > ObservationContext.RemainingRetries) ||
            MaximumActions is < 1 or > 8)
        {
            throw new ArgumentException("The provider decision execution options are outside the supported v1 bounds.");
        }
    }
}

public sealed record ProviderDecisionExecutionResult(
    bool Succeeded,
    ObservationBuildResult? Observation,
    ModelDecision? Decision,
    IReadOnlyList<ActionResult> ActionResults,
    ArenaError? Error)
{
    public static ProviderDecisionExecutionResult Failure(
        ArenaError error,
        ObservationBuildResult? observation = null,
        IReadOnlyList<ActionResult>? actionResults = null) =>
        new(false, observation, null, actionResults ?? [], error);
}

/// <summary>
/// Executes the provider-neutral decision flow while holding the game at the
/// required pause boundary. It owns neither credentials nor a provider
/// implementation; those remain behind IModelProvider and its resolver.
/// </summary>
public sealed class ProviderDecisionExecutor
{
    public static async Task<ProviderDecisionExecutionResult> ExecuteAsync(
        ArenaGameScriptClient gameScript,
        IModelProvider provider,
        ObservationArtifactWriter artifacts,
        ProviderDecisionExecutionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameScript);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!string.Equals(options.ObservationContext.RunId, gameScript.RunId, StringComparison.Ordinal))
        {
            return ProviderDecisionExecutionResult.Failure(new ArenaError(
                ArenaErrorCodes.ActionConstraintViolation,
                "The decision context does not belong to the active run.",
                "The orchestrator rejected a cross-run decision context before provider invocation.",
                false));
        }

        ArenaError? pauseError = await gameScript.PauseAsync(options.RequestTimeout, cancellationToken);
        if (pauseError is not null)
        {
            return ProviderDecisionExecutionResult.Failure(pauseError);
        }

        ProviderDecisionExecutionResult executionResult = ProviderDecisionExecutionResult.Failure(
            ProtocolError("The provider decision did not reach a safe completion boundary."));
        ArenaError? resumeError = null;
        try
        {
            executionResult = await ExecuteWhilePausedAsync(gameScript, provider, artifacts, options, cancellationToken);
        }
        finally
        {
            if (options.ResumeAfterActionHandling)
            {
                resumeError = await gameScript.ResumeAsync(options.RequestTimeout, CancellationToken.None);
            }
        }

        return resumeError is null
            ? executionResult
            : ProviderDecisionExecutionResult.Failure(resumeError, executionResult.Observation, executionResult.ActionResults);
    }

    private static async Task<ProviderDecisionExecutionResult> ExecuteWhilePausedAsync(
        ArenaGameScriptClient gameScript,
        IModelProvider provider,
        ObservationArtifactWriter artifacts,
        ProviderDecisionExecutionOptions options,
        CancellationToken cancellationToken)
    {
        GameScriptSnapshotResult snapshotResult = await gameScript.GetSnapshotAsync(options.RequestTimeout, cancellationToken);
        if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
        {
            return ProviderDecisionExecutionResult.Failure(snapshotResult.Error ?? ProtocolError("ArenaGS did not return a typed snapshot."));
        }

        if (!snapshotResult.Snapshot.Paused)
        {
            return ProviderDecisionExecutionResult.Failure(ProtocolError("ArenaGS returned an observation outside the required provider-call pause boundary."));
        }

        ObservationBuildResult observation = ObservationBuilder.Build(snapshotResult.Snapshot, options.ObservationContext);
        await artifacts.AppendObservationAsync(
            new ObservationBuildRecord(observation.Snapshot, observation.Sha256, observation.ReplaySha256),
            cancellationToken);
        foreach (NormalizedGameEvent eventEntry in snapshotResult.Snapshot.Events)
        {
            await artifacts.AppendEventAsync(eventEntry, cancellationToken);
        }

        ModelRequest request = new()
        {
            RunId = options.ObservationContext.RunId,
            DecisionId = options.DecisionId,
            ObservationHash = observation.Sha256,
            ReplayObservationHash = observation.ReplaySha256,
            Observation = observation.CanonicalJson,
            AvailableTools = options.ObservationContext.AllowedTools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray(),
            RemainingModelCalls = options.ObservationContext.RemainingModelCalls,
            RemainingOutputTokens = options.ObservationContext.RemainingOutputTokens,
            MaximumActions = options.MaximumActions,
            PromptTemplateVersion = ArenaPromptTemplate.Version,
            PromptTemplateSha256 = ArenaPromptTemplate.Sha256,
        };
        ProviderDecisionLoopResult providerResult = await ProviderDecisionLoop.GetDecisionAsync(
            provider,
            request,
            new ProviderDecisionLoopOptions(options.MaximumSchemaCorrectionRetries ?? options.ObservationContext.RemainingRetries),
            cancellationToken);
        await AppendUsageAsync(
            artifacts,
            request,
            provider,
            options.ProviderModel,
            providerResult,
            cancellationToken);

        if (!providerResult.FinalResult.IsSuccess || providerResult.FinalResult.Decision is null)
        {
            return ProviderDecisionExecutionResult.Failure(
                providerResult.FinalResult.Error ?? ProtocolError("The provider returned no decision."),
                observation);
        }

        ModelDecision decision = providerResult.FinalResult.Decision;
        if (!string.Equals(decision.DecisionId, request.DecisionId, StringComparison.Ordinal))
        {
            return ProviderDecisionExecutionResult.Failure(new ArenaError(
                ArenaErrorCodes.ProviderInvalidOutput,
                "The provider returned a decision for a different correlation identifier.",
                "The decision_id did not match the trusted request correlation.",
                true),
                observation);
        }

        if (decision.Actions.Count > options.MaximumActions)
        {
            return ProviderDecisionExecutionResult.Failure(new ArenaError(
                ArenaErrorCodes.ProviderSchemaMismatch,
                "The provider returned more actions than this decision boundary permits.",
                "The trusted decision policy rejected the provider action count before any action was dispatched.",
                true),
                observation);
        }

        await artifacts.AppendDecisionAsync(new RecordedDecision
        {
            SchemaVersion = ContractVersions.ObservationV1,
            RunId = request.RunId,
            DecisionId = decision.DecisionId,
            Provider = provider.Descriptor.ProviderId,
            Model = options.ProviderModel,
            ObservationSha256 = request.ObservationHash,
            PromptTemplateVersion = request.PromptTemplateVersion,
            PromptTemplateSha256 = request.PromptTemplateSha256 ?? string.Empty,
            Decision = decision,
        }, cancellationToken);

        List<ActionResult> actionResults = [];
        for (int index = 0; index < decision.Actions.Count; index++)
        {
            ModelAction modelAction = decision.Actions[index];
            ActionRequest actionRequest = CreateActionRequest(
                request.RunId,
                decision.DecisionId,
                modelAction,
                index,
                options.ConstraintContext);
            RoadActionValidationResult validation = RoadActionValidator.Validate(
                modelAction,
                observation.Snapshot,
                request.AvailableTools.ToHashSet(StringComparer.Ordinal));
            if (validation.IsValid)
            {
                validation = ScenarioActionConstraintValidator.Validate(
                    modelAction,
                    observation.Snapshot,
                    options.ConstraintContext);
            }
            ActionResult actionResult;
            if (!validation.IsValid)
            {
                actionResult = new ActionResult
                {
                    ActionId = actionRequest.ActionId,
                    RunId = request.RunId,
                    CorrelationId = actionRequest.CorrelationId,
                    Status = "rejected",
                    ErrorCode = validation.ErrorCode,
                    Message = validation.Message,
                };
            }
            else
            {
                GameScriptActionResult actionResponse = await gameScript.ExecuteActionAsync(
                    actionRequest,
                    options.RequestTimeout,
                    cancellationToken);
                actionResult = actionResponse.Action ?? new ActionResult
                {
                    ActionId = actionRequest.ActionId,
                    RunId = request.RunId,
                    CorrelationId = actionRequest.CorrelationId,
                    Status = "failed",
                    ErrorCode = actionResponse.Error?.Code ?? ArenaErrorCodes.AdminPortUnavailable,
                    Message = actionResponse.Error?.UserMessage ?? "The trusted GameScript action response was unavailable.",
                };
            }

            await artifacts.AppendActionAsync(new RecordedAction
            {
                SchemaVersion = ContractVersions.ObservationV1,
                RunId = request.RunId,
                DecisionId = decision.DecisionId,
                Request = actionRequest,
                Result = actionResult,
            }, cancellationToken);
            actionResults.Add(actionResult);
        }

        return new ProviderDecisionExecutionResult(true, observation, decision, actionResults, null);
    }

    private static async Task AppendUsageAsync(
        ObservationArtifactWriter artifacts,
        ModelRequest request,
        IModelProvider provider,
        string model,
        ProviderDecisionLoopResult providerResult,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < providerResult.Attempts.Count; index++)
        {
            ProviderUsage usage = providerResult.Attempts[index];
            bool isFinalAttempt = index == providerResult.Attempts.Count - 1;
            await artifacts.AppendProviderUsageAsync(new ProviderUsageRecord
            {
                SchemaVersion = ContractVersions.ObservationV1,
                RunId = request.RunId,
                DecisionId = request.DecisionId,
                Provider = provider.Descriptor.ProviderId,
                Model = model,
                InputTokens = Math.Max(0, usage.InputTokens),
                OutputTokens = Math.Max(0, usage.OutputTokens),
                LatencyMilliseconds = Math.Max(0, (long)usage.Latency.TotalMilliseconds),
                ProviderRequestId = usage.ProviderRequestId,
                EstimatedCost = usage.EstimatedCost,
                ErrorCode = isFinalAttempt ? providerResult.FinalResult.Error?.Code : null,
            }, cancellationToken);
        }
    }

    private static ActionRequest CreateActionRequest(
        string runId,
        string decisionId,
        ModelAction action,
        int index,
        ScenarioActionConstraintContext? constraintContext)
    {
        string suffix = $"-{index + 1}";
        string prefix = decisionId.Length > 110 ? decisionId[..110] : decisionId;
        string actionId = "action-" + prefix + suffix;
        string correlationId = "correlation-" + prefix + suffix;
        return new ActionRequest
        {
            ActionId = actionId,
            RunId = runId,
            DecisionId = decisionId,
            CorrelationId = correlationId,
            IdempotencyKey = "idempotency-" + prefix + suffix,
            Tool = action.Tool,
            Arguments = action.Arguments.Clone(),
            ConstraintContext = constraintContext,
        };
    }

    private static ArenaError ProtocolError(string message) => new(
        ArenaErrorCodes.ProtocolInvalidMessage,
        message,
        "The trusted GameScript response did not meet the versioned decision-loop contract.",
        false);
}
