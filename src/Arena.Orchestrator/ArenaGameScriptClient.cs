using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.AdminProtocol;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record GameScriptSnapshotResult(GameScriptSnapshot? Snapshot, ArenaError? Error)
{
    public bool Succeeded => Snapshot is not null && Error is null;
}

public sealed record GameScriptActionResult(ActionResult? Action, ArenaError? Error)
{
    public bool Succeeded => Action is not null && Error is null;
}

/// <summary>
/// Trusted orchestrator-side façade for the closed ArenaGS protocol. Providers
/// never receive this type: they can return typed model actions only, while
/// this client constructs the correlated AdminPort envelope after authorization.
/// </summary>
public sealed class ArenaGameScriptClient
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AdminPortBridgeClient _bridge;
    private readonly string _runId;
    private int _sequence;

    public string RunId => _runId;

    public ArenaGameScriptClient(AdminPortBridgeClient bridge, string runId)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        if (!ProtocolEnvelopeValidator.IsIdentifier(runId))
        {
            throw new ArgumentException("The run identifier is invalid.", nameof(runId));
        }

        _bridge = bridge;
        _runId = runId;
    }

    public async Task<ArenaError?> HelloAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        AdminPortRequestResult result = await RequestAsync(
            ProtocolMessageTypes.Hello,
            EmptyPayload(),
            null,
            timeout,
            cancellationToken);
        if (!result.Succeeded || result.Response is null)
        {
            return TransportError(result);
        }

        return result.Response.Payload.ValueKind == JsonValueKind.Object &&
            result.Response.Payload.TryGetProperty("capabilities", out JsonElement capabilities) &&
            capabilities.ValueKind == JsonValueKind.Array
            ? null
            : ProtocolError("ArenaGS did not return the negotiated capability payload.");
    }

    public async Task<ArenaError?> PauseAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await RequestControlAsync(ProtocolMessageTypes.PauseRequest, timeout, cancellationToken);

    public async Task<ArenaError?> ResumeAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await RequestControlAsync(ProtocolMessageTypes.ResumeRequest, timeout, cancellationToken);

    /// <summary>
    /// Arms a trusted supervisor-only save/load checkpoint for a persisted
    /// road project. This method is intentionally not part of any provider
    /// contract or model tool surface.
    /// </summary>
    public async Task<ArenaError?> ArmProjectCheckpointAsync(
        string projectId,
        string pauseAfterState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!ProtocolEnvelopeValidator.IsIdentifier(projectId) ||
            !RoadProjectCheckpointStages.All.Contains(pauseAfterState))
        {
            return new ArenaError(
                ArenaErrorCodes.ActionConstraintViolation,
                "The requested save/load checkpoint is not a supported persisted road-project stage.",
                "The trusted orchestrator rejected an invalid supervisor checkpoint request before AdminPort.",
                false);
        }

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            project_id = projectId,
            pause_after_state = pauseAfterState,
        });
        AdminPortRequestResult result = await RequestAsync(
            ProtocolMessageTypes.CheckpointRequest,
            payload,
            null,
            timeout,
            cancellationToken);
        if (!result.Succeeded || result.Response is null)
        {
            return TransportError(result);
        }

        JsonElement response = result.Response.Payload;
        return response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("status", out JsonElement status) &&
            status.ValueKind == JsonValueKind.String &&
            (string.Equals(status.GetString(), "armed", StringComparison.Ordinal) ||
             string.Equals(status.GetString(), "paused", StringComparison.Ordinal)) &&
            response.TryGetProperty("paused", out JsonElement paused) &&
            (paused.ValueKind == JsonValueKind.True || paused.ValueKind == JsonValueKind.False)
            ? null
            : ProtocolError("ArenaGS did not return a typed supervisor checkpoint result.");
    }

    public async Task<GameScriptSnapshotResult> GetSnapshotAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        AdminPortRequestResult result = await RequestAsync(
            ProtocolMessageTypes.SnapshotRequest,
            EmptyPayload(),
            null,
            timeout,
            cancellationToken);
        if (!result.Succeeded || result.Response is null)
        {
            return new GameScriptSnapshotResult(null, TransportError(result));
        }

        try
        {
            JsonElement payload = result.Response.Payload;
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("game_state", out JsonElement gameState) ||
                gameState.ValueKind != JsonValueKind.Object)
            {
                return new GameScriptSnapshotResult(null, ProtocolError("ArenaGS did not return the authoritative observation payload."));
            }

            GameScriptSnapshot? snapshot = JsonSerializer.Deserialize<GameScriptSnapshot>(gameState.GetRawText(), ContractJsonOptions);
            ArenaError? validationError = ValidateSnapshot(snapshot);
            return validationError is null
                ? new GameScriptSnapshotResult(snapshot, null)
                : new GameScriptSnapshotResult(null, validationError);
        }
        catch (JsonException)
        {
            return new GameScriptSnapshotResult(null, ProtocolError("ArenaGS returned an observation that did not match the v1 contract."));
        }
    }

    public async Task<GameScriptActionResult> ExecuteActionAsync(
        ActionRequest action,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!string.Equals(action.RunId, _runId, StringComparison.Ordinal) ||
            !ProtocolEnvelopeValidator.IsIdentifier(action.ActionId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(action.DecisionId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(action.CorrelationId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(action.IdempotencyKey) ||
            !RoadToolCatalog.All.Contains(action.Tool, StringComparer.Ordinal) ||
            action.Arguments.ValueKind != JsonValueKind.Object ||
            !JsonValueBounds.IsWithinBounds(action.Arguments))
        {
            return new GameScriptActionResult(null, new ArenaError(
                ArenaErrorCodes.ActionConstraintViolation,
                "The authorized action did not satisfy the typed road-tool contract.",
                "The orchestrator rejected an invalid ActionRequest before it crossed AdminPort.",
                false));
        }

        JsonElement payload = JsonSerializer.SerializeToElement(action, ObservationJsonContext.Default.ActionRequest);
        AdminPortRequestResult result = await RequestAsync(
            ProtocolMessageTypes.ActionRequest,
            payload,
            action.IdempotencyKey,
            timeout,
            cancellationToken,
            action.CorrelationId);
        if (!result.Succeeded || result.Response is null)
        {
            return new GameScriptActionResult(null, TransportError(result));
        }

        try
        {
            ActionResult? actionResult = JsonSerializer.Deserialize<ActionResult>(
                result.Response.Payload.GetRawText(),
                ContractJsonOptions);
            if (actionResult is null ||
                !string.Equals(actionResult.ActionId, action.ActionId, StringComparison.Ordinal) ||
                !string.Equals(actionResult.RunId, _runId, StringComparison.Ordinal) ||
                !string.Equals(actionResult.CorrelationId, action.CorrelationId, StringComparison.Ordinal))
            {
                return new GameScriptActionResult(null, ProtocolError("ArenaGS returned an action result for a different request."));
            }

            return new GameScriptActionResult(actionResult, null);
        }
        catch (JsonException)
        {
            return new GameScriptActionResult(null, ProtocolError("ArenaGS returned an action result that did not match the v1 contract."));
        }
    }

    private async Task<ArenaError?> RequestControlAsync(
        string messageType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AdminPortRequestResult result = await RequestAsync(
            messageType,
            EmptyPayload(),
            null,
            timeout,
            cancellationToken);
        if (!result.Succeeded || result.Response is null)
        {
            return TransportError(result);
        }

        return result.Response.Payload.ValueKind == JsonValueKind.Object &&
            result.Response.Payload.TryGetProperty("paused", out JsonElement paused) &&
            (paused.ValueKind == JsonValueKind.True || paused.ValueKind == JsonValueKind.False)
            ? null
            : ProtocolError("ArenaGS did not return a typed control result.");
    }

    private async Task<AdminPortRequestResult> RequestAsync(
        string messageType,
        JsonElement payload,
        string? idempotencyKey,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        int sequence = Interlocked.Increment(ref _sequence);
        string correlation = correlationId ?? $"arena-correlation-{sequence}";
        ProtocolEnvelope request = new()
        {
            ProtocolVersion = ContractVersions.ProtocolV1,
            MessageType = messageType,
            RunId = _runId,
            MessageId = $"arena-message-{sequence}",
            CorrelationId = correlation,
            IdempotencyKey = idempotencyKey ?? $"arena-key-{sequence}",
            Payload = payload,
        };
        return await _bridge.RequestAsync(request, timeout, cancellationToken);
    }

    private static JsonElement EmptyPayload()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static ArenaError? ValidateSnapshot(GameScriptSnapshot? snapshot)
    {
        if (snapshot is null ||
            !string.Equals(snapshot.SchemaVersion, ContractVersions.ObservationV1, StringComparison.Ordinal) ||
            !IsGameDate(snapshot.GameDate) ||
            snapshot.GameTick < 0 ||
            snapshot.Company.CompanyId < 0 ||
            snapshot.Towns.Count > 32 ||
            snapshot.Industries.Count > 32 ||
            snapshot.Stations.Count > 32 ||
            snapshot.Vehicles.Count > 32 ||
            snapshot.Routes.Count > 32 ||
            snapshot.Projects.Count > 16 ||
            snapshot.Events.Count > 64)
        {
            return ProtocolError("ArenaGS returned an observation outside the v1 bounds.");
        }

        return null;
    }

    private static bool IsGameDate(string value) =>
        value.Length == 10 &&
        value[4] == '-' &&
        value[7] == '-' &&
        value.Where((_, index) => index is not 4 and not 7).All(char.IsAsciiDigit);

    private static ArenaError TransportError(AdminPortRequestResult result) => new(
        result.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable,
        result.UserMessage,
        "The trusted AdminPort transport did not return a correlated ArenaGS response.",
        false);

    private static ArenaError ProtocolError(string message) => new(
        ArenaErrorCodes.ProtocolInvalidMessage,
        message,
        "ArenaGS returned a payload outside the versioned contract.",
        false);
}
