using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.AdminProtocol;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record Phase03BridgeSmokeOptions(
    TimeSpan StartupTimeout,
    TimeSpan RequestTimeout,
    TimeSpan ShutdownTimeout)
{
    public void Validate()
    {
        if (StartupTimeout is { TotalSeconds: < 5 or > 300 } ||
            RequestTimeout is { TotalSeconds: < 8 or > 60 } ||
            ShutdownTimeout is { TotalSeconds: < 2 or > 120 })
        {
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout), "Phase 03 bridge-smoke timeouts are outside their supported bounds.");
        }
    }
}

public sealed record Phase03BridgeCheck(
    string Id,
    bool Passed,
    string? ErrorCode,
    string Detail);

public sealed record Phase03BridgeSmokeResult(
    string SchemaVersion,
    string RunId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset CompletedUtc,
    bool Succeeded,
    string? ErrorCode,
    IReadOnlyList<Phase03BridgeCheck> Checks);

/// <summary>
/// Supplies a narrowly-scoped post-protocol proof while the trusted bridge has
/// an authenticated, live GameScript session. Extensions are deliberately
/// given only the AdminPort client, a run-root path policy, and an optional
/// supervisor-only save/load operation. A model provider never receives any
/// of these capabilities.
/// </summary>
public sealed record Phase03BridgeExtensionContext(
    string RunId,
    RunPathPolicy Paths,
    ArenaLocalConfiguration Configuration,
    AdminPortBridgeClient Bridge,
    TimeSpan RequestTimeout,
    IPhase03SaveLoadController? SaveLoadController = null);

public sealed record Phase03BridgeExtensionResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    IReadOnlyList<Phase03BridgeCheck> Checks)
{
    public static Phase03BridgeExtensionResult Success(
        string detail,
        IReadOnlyList<Phase03BridgeCheck> checks) =>
        new(true, null, detail, checks);

    public static Phase03BridgeExtensionResult Failure(
        string errorCode,
        string detail,
        IReadOnlyList<Phase03BridgeCheck>? checks = null) =>
        new(false, errorCode, detail, checks ?? []);
}

public interface IPhase03BridgeExtension
{
    Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the provider-free, real OpenTTD Phase 03 transport proof. It owns the
/// isolated server process and AdminPort credential material, while all game
/// operations pass through the authenticated ArenaGS protocol.
/// </summary>
public sealed class Phase03BridgeService
{
    private const string ServerComponentId = "server";
    private const string ResultFileName = "bridge-result.json";
    private const string GameScriptActiveMarker = "ARENA_PHASE03_GAMESCRIPT_ACTIVE";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
    private readonly RunDirectoryAllocator _runDirectoryAllocator;
    private readonly IArenaProcessFactory _processFactory;
    private readonly IOpenTtdConsoleBridge _consoleBridge;
    private readonly ILoopbackReadinessProbe _readinessProbe;
    private readonly ICredentialStore _credentialStore;
    private readonly TimeProvider _timeProvider;

    public Phase03BridgeService(
        RunDirectoryAllocator runDirectoryAllocator,
        IArenaProcessFactory processFactory,
        IOpenTtdConsoleBridge consoleBridge,
        ILoopbackReadinessProbe readinessProbe,
        ICredentialStore credentialStore,
        TimeProvider? timeProvider = null)
    {
        _runDirectoryAllocator = runDirectoryAllocator ?? throw new ArgumentNullException(nameof(runDirectoryAllocator));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _consoleBridge = consoleBridge ?? throw new ArgumentNullException(nameof(consoleBridge));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Phase03BridgeSmokeResult> RunAsync(
        ArenaLocalConfiguration configuration,
        Phase03BridgeSmokeOptions options,
        CancellationToken cancellationToken)
        => await RunAsync(configuration, options, null, cancellationToken);

    /// <summary>
    /// Runs the Phase 03 transport proof and, optionally, an immediately
    /// downstream capability proof before finalizing the GameScript session.
    /// This preserves the finalized protocol boundary for ordinary bridge
    /// smoke runs while allowing later phases to use the exact same trusted
    /// process lifecycle.
    /// </summary>
    public async Task<Phase03BridgeSmokeResult> RunAsync(
        ArenaLocalConfiguration configuration,
        Phase03BridgeSmokeOptions options,
        IPhase03BridgeExtension? extension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        RunDirectoryAllocation allocation = await _runDirectoryAllocator.AllocateAsync(
            configuration.Runtime.Runs,
            "bridge",
            cancellationToken);
        DateTimeOffset createdUtc = _timeProvider.GetUtcNow();
        List<Phase03BridgeCheck> checks = [];
        using RunLifecycleJournal journal = new(allocation.RunId, allocation.Paths);
        IManagedArenaProcess? server = null;
        AdminPortBridgeClient? client = null;
        IPhase03SaveLoadController? saveLoadController = null;
        string? secretPath = null;
        string? errorCode = null;
        bool succeeded = false;
        ArenaRunState state = ArenaRunState.Created;

        try
        {
            await journal.InitializeAsync(createdUtc, cancellationToken);
            state = await TransitionAsync(journal, ArenaRunState.Preparing, null, cancellationToken);
            EnsureRuntimeIsReady(configuration);
            Phase02ServerWorkspace workspace = await Phase02RunPreparation.PrepareServerWorkspaceAsync(
                configuration,
                allocation.Paths,
                "server",
                ServerComponentId,
                cancellationToken);

            CredentialReadResult credential = await _credentialStore.ReadAsync(
                configuration.OpenTtd.AdminCredentialReference,
                cancellationToken);
            try
            {
                if (!credential.Succeeded || credential.Secret is null)
                {
                    throw new BridgeSmokeException(
                        credential.ErrorCode ?? ArenaErrorCodes.CredentialMissing,
                        "The dedicated AdminPort credential is unavailable.");
                }

                secretPath = await AdminPortSecretFile.WriteAsync(
                    allocation.Paths,
                    "server",
                    credential.Secret.Bytes,
                    cancellationToken);

                state = await TransitionAsync(journal, ArenaRunState.StartingServer, ServerComponentId, cancellationToken);
                server = await StartServerAsync(configuration, workspace, cancellationToken);
                state = await TransitionAsync(journal, ArenaRunState.WaitingForGameScript, ServerComponentId, cancellationToken);
                bool listening = await _readinessProbe.WaitForPortAsync(
                    configuration.Network.BindAddress,
                    configuration.OpenTtd.AdminPort,
                    options.StartupTimeout,
                    cancellationToken);
                if (!listening)
                {
                    throw new BridgeSmokeException(
                        server.HasExited ? ArenaErrorCodes.RunServerExited : ArenaErrorCodes.RunStartupTimedOut,
                        "OpenTTD did not open its isolated AdminPort before the startup timeout.");
                }

                bool gameScriptReady = await _consoleBridge.WaitForSignalsAsync(
                    server.ProcessId,
                    [Phase02SmokeDefaults.GameScriptReadyMarker],
                    options.StartupTimeout,
                    cancellationToken);
                if (!gameScriptReady)
                {
                    throw new BridgeSmokeException(
                        server.HasExited ? ArenaErrorCodes.RunServerExited : ArenaErrorCodes.RunGameScriptNotReady,
                        "ArenaGS did not publish its explicit readiness signal before authenticated AdminPort checks began.");
                }

                // A dedicated OpenTTD server can begin from a paused game
                // state. Advance it under trusted supervisor control so the
                // GameScript event loop can consume the authenticated request;
                // later pause/resume checks exercise the game-side protocol.
                await _consoleBridge.SendAsync(server.ProcessId, OpenTtdConsoleCommand.Unpause, cancellationToken);

                bool gameScriptActive = await _consoleBridge.WaitForSignalsAsync(
                    server.ProcessId,
                    [GameScriptActiveMarker],
                    options.StartupTimeout,
                    cancellationToken);
                if (!gameScriptActive)
                {
                    throw new BridgeSmokeException(
                        ArenaErrorCodes.RunGameScriptNotReady,
                        "ArenaGS did not enter its active event loop after the dedicated server was unpaused.");
                }

                client = await AdminPortBridgeClient.ConnectAsync(
                    new AdminPortClientOptions(
                        configuration.Network.BindAddress,
                        configuration.OpenTtd.AdminPort,
                        options.StartupTimeout,
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromSeconds(5),
                        AllowLegacyPasswordAuthentication: UsesLegacyAdminPortAuthentication(configuration.OpenTtd.Executable)),
                    credential.Secret.Bytes,
                    cancellationToken,
                    _timeProvider);
                saveLoadController = new Phase03SaveLoadController(
                    allocation.Paths,
                    workspace.SaveDirectory,
                    server,
                    _consoleBridge,
                    options.StartupTimeout,
                    _timeProvider);
            }
            finally
            {
                credential.Secret?.Dispose();
            }

            state = await TransitionAsync(journal, ArenaRunState.Ready, ServerComponentId, cancellationToken);
            await VerifyGameScriptEventLoopAsync(
                client,
                allocation.RunId,
                options.StartupTimeout,
                cancellationToken);
            checks.Add(Pass("gamescript-event-loop", "ArenaGS consumed a real authenticated AdminPort event and accepted a bounded response."));
            await RunProtocolChecksAsync(
                client,
                configuration.RepositoryRoot,
                allocation.RunId,
                checks,
                options.RequestTimeout,
                cancellationToken);
            if (extension is not null)
            {
                Phase03BridgeExtensionResult extensionResult = await extension.RunAsync(
                    new Phase03BridgeExtensionContext(
                        allocation.RunId,
                        allocation.Paths,
                        configuration,
                        client,
                        options.RequestTimeout,
                        saveLoadController),
                    cancellationToken);
                checks.AddRange(extensionResult.Checks);
                if (!extensionResult.Succeeded || extensionResult.Checks.Any(check => !check.Passed))
                {
                    throw new BridgeSmokeException(
                        extensionResult.ErrorCode ?? extensionResult.Checks.FirstOrDefault(check => !check.Passed)?.ErrorCode ?? ArenaErrorCodes.RunPreparationFailed,
                        extensionResult.Detail);
                }
            }

            await FinalizeProtocolAsync(
                client,
                allocation.RunId,
                checks,
                options.RequestTimeout,
                cancellationToken);
            state = await TransitionAsync(journal, ArenaRunState.Running, ServerComponentId, cancellationToken);
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorCode = ArenaErrorCodes.RunCancelled;
            checks.Add(new Phase03BridgeCheck("cancelled", false, errorCode, "The caller cancelled the bridge smoke run."));
        }
        catch (BridgeSmokeException exception)
        {
            errorCode = exception.ErrorCode;
            checks.Add(new Phase03BridgeCheck("bridge", false, errorCode, SafeDetail(exception.Message)));
        }
        catch (AdminPortWireException exception)
        {
            errorCode = exception.ErrorCode;
            checks.Add(new Phase03BridgeCheck("adminport", false, errorCode, SafeDetail(exception.Message)));
        }
        catch (IOException exception)
        {
            errorCode = ArenaErrorCodes.AdminPortUnavailable;
            checks.Add(new Phase03BridgeCheck("adminport", false, errorCode, SafeDetail(exception.Message)));
        }
        catch (InvalidOperationException exception)
        {
            errorCode = exception.Message.StartsWith(ArenaErrorCodes.AdminPortSecretInvalid, StringComparison.Ordinal)
                ? ArenaErrorCodes.AdminPortSecretInvalid
                : ArenaErrorCodes.RunPreparationFailed;
            checks.Add(new Phase03BridgeCheck("bridge", false, errorCode, SafeDetail(exception.Message)));
        }
        catch (Exception exception)
        {
            errorCode = ArenaErrorCodes.RunPreparationFailed;
            checks.Add(new Phase03BridgeCheck("bridge", false, errorCode, SafeDetail(exception.Message)));
        }
        finally
        {
            try
            {
                if (state == ArenaRunState.Created)
                {
                    state = await TransitionAsync(journal, ArenaRunState.Preparing, null, CancellationToken.None);
                }

                if (state != ArenaRunState.Finalizing)
                {
                    state = await TransitionAsync(journal, ArenaRunState.Finalizing, ServerComponentId, CancellationToken.None);
                }

                if (client is not null)
                {
                    await client.DisposeAsync();
                }

                if (server is not null)
                {
                    await StopServerAsync(server, options.ShutdownTimeout, CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                succeeded = false;
                errorCode ??= ArenaErrorCodes.RunFinalizationFailed;
                checks.Add(new Phase03BridgeCheck("cleanup", false, errorCode, SafeDetail(exception.Message)));
            }
            finally
            {
                try
                {
                    if (secretPath is not null)
                    {
                        AdminPortSecretFile.Delete(allocation.Paths, secretPath);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    succeeded = false;
                    errorCode ??= ArenaErrorCodes.RunFinalizationFailed;
                    checks.Add(new Phase03BridgeCheck("secret-cleanup", false, errorCode, SafeDetail(exception.Message)));
                }

                if (server is not null)
                {
                    await server.DisposeAsync();
                }
            }
        }

        DateTimeOffset completedUtc = _timeProvider.GetUtcNow();
        ArenaRunState finalState = succeeded ? ArenaRunState.Completed : ArenaRunState.Failed;
        ArenaRunExitReason exitReason = succeeded ? ArenaRunExitReason.Completed : ArenaRunExitReason.PreparationFailed;
        await journal.TransitionAsync(
            finalState,
            completedUtc,
            null,
            exitReason,
            errorCode,
            errorCode is null ? null : "See bridge-result.json for redacted protocol check evidence.",
            CancellationToken.None);

        Phase03BridgeSmokeResult result = new(
            ContractVersions.BridgeResultV1,
            allocation.RunId,
            createdUtc,
            completedUtc,
            succeeded,
            errorCode,
            checks);
        await WriteResultAsync(allocation.Paths, result, CancellationToken.None);
        return result;
    }

    private static async Task RunProtocolChecksAsync(
        AdminPortBridgeClient client,
        string repositoryRoot,
        string runId,
        List<Phase03BridgeCheck> checks,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        await RunSharedFixtureChecksAsync(client, repositoryRoot, runId, checks, requestTimeout, cancellationToken);

        ProtocolEnvelope hello = CreateRequest(ProtocolMessageTypes.Hello, runId, 1, "{}");
        AdminPortRequestResult capabilities = await RequireResultAsync(client, hello, requestTimeout, cancellationToken);
        RequirePayloadProperty(capabilities.Response!, "max_logical_payload_bytes");
        checks.Add(Pass("hello-capabilities", "Authenticated protocol negotiation returned ArenaGS capabilities."));

        ProtocolEnvelope incompatible = CreateRequest(ProtocolMessageTypes.Heartbeat, runId, 9, "{}") with
        {
            ProtocolVersion = "9.9",
        };
        AdminPortSendResult incompatibleSend = await client.SendAsync(incompatible, cancellationToken);
        if (incompatibleSend.Accepted || !string.Equals(incompatibleSend.ErrorCode, ArenaErrorCodes.ProtocolVersionMismatch, StringComparison.Ordinal))
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolVersionMismatch, "The bridge did not block an incompatible protocol envelope before it reached OpenTTD.");
        }

        checks.Add(Pass("version-gate", "An incompatible protocol envelope was blocked before gameplay transport."));

        ProtocolEnvelope staleRun = CreateRequest(ProtocolMessageTypes.Heartbeat, "other-run", 10, "{}");
        AdminPortRequestResult staleResult = await client.RequestAsync(staleRun, requestTimeout, cancellationToken);
        if (staleResult.Succeeded || !string.Equals(staleResult.ErrorCode, ArenaErrorCodes.ProtocolStaleCorrelation, StringComparison.Ordinal))
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolStaleCorrelation, "ArenaGS did not classify a request for a stale run deterministically.");
        }

        checks.Add(Pass("stale-run", "ArenaGS rejected a correlated request for a different run."));

        ProtocolEnvelope heartbeat = CreateRequest(ProtocolMessageTypes.Heartbeat, runId, 2, "{}");
        AdminPortRequestResult heartbeatResult = await RequireResultAsync(client, heartbeat, requestTimeout, cancellationToken);
        RequirePayloadProperty(heartbeatResult.Response!, "ready");
        checks.Add(Pass("heartbeat", "ArenaGS returned a correlated heartbeat."));

        AdminPortSendResult reconnect = await client.ReconnectForValidationAsync(cancellationToken);
        if (!reconnect.Accepted)
        {
            throw new BridgeSmokeException(
                reconnect.ErrorCode ?? ArenaErrorCodes.AdminPortReconnectFailed,
                reconnect.TechnicalMessage ?? "The authenticated AdminPort connection could not be restored.");
        }

        ProtocolEnvelope reconnectedHeartbeat = CreateRequest(ProtocolMessageTypes.Heartbeat, runId, 14, "{}");
        AdminPortRequestResult reconnectedHeartbeatResult = await RequireResultAsync(client, reconnectedHeartbeat, requestTimeout, cancellationToken);
        RequirePayloadProperty(reconnectedHeartbeatResult.Response!, "ready");
        checks.Add(Pass("reconnect", "The client deliberately reconnected to the live OpenTTD AdminPort and safely continued with a new heartbeat."));

        ProtocolEnvelope pause = CreateRequest(ProtocolMessageTypes.PauseRequest, runId, 3, "{}");
        AdminPortRequestResult pauseResult = await RequireResultAsync(client, pause, requestTimeout, cancellationToken);
        AdminPortRequestResult duplicatePauseResult = await RequireResultAsync(client, pause, requestTimeout, cancellationToken);
        if (!string.Equals(pauseResult.Response!.MessageId, duplicatePauseResult.Response!.MessageId, StringComparison.Ordinal))
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolStaleCorrelation, "ArenaGS did not return the original result for a duplicate idempotent command.");
        }

        checks.Add(Pass("idempotency", "Duplicate pause request returned the original ArenaGS result without a second execution."));

        ProtocolEnvelope resume = CreateRequest(ProtocolMessageTypes.ResumeRequest, runId, 4, "{}");
        _ = await RequireResultAsync(client, resume, requestTimeout, cancellationToken);
        checks.Add(Pass("pause-resume", "ArenaGS accepted correlated pause and resume requests."));

        ProtocolEnvelope snapshot = CreateRequest(ProtocolMessageTypes.SnapshotRequest, runId, 5, "{}");
        AdminPortRequestResult snapshotResult = await RequireResultAsync(client, snapshot, requestTimeout, cancellationToken);
        RequirePayloadProperty(snapshotResult.Response!, "game_date");
        checks.Add(Pass("snapshot", "ArenaGS returned a bounded snapshot response."));

        ProtocolEnvelope action = CreateRequest(ProtocolMessageTypes.ActionRequest, runId, 11, "{}");
        AdminPortRequestResult actionResult = await RequireResultAsync(client, action, requestTimeout, cancellationToken);
        RequirePayloadProperty(actionResult.Response!, "status");
        checks.Add(Pass("action-boundary", "ArenaGS returned the typed deferred action result without exposing gameplay execution."));

        ProtocolEnvelope camera = CreateRequest(ProtocolMessageTypes.CameraRequest, runId, 12, "{}");
        AdminPortRequestResult cameraResult = await RequireResultAsync(client, camera, requestTimeout, cancellationToken);
        RequirePayloadProperty(cameraResult.Response!, "status");
        checks.Add(Pass("camera-boundary", "ArenaGS returned the typed deferred camera result."));

        ProtocolEnvelope checkpoint = CreateRequest(ProtocolMessageTypes.CheckpointRequest, runId, 13, "{}");
        AdminPortRequestResult checkpointResult = await RequireResultAsync(client, checkpoint, requestTimeout, cancellationToken);
        RequirePayloadProperty(checkpointResult.Response!, "status");
        checks.Add(Pass("checkpoint-boundary", "ArenaGS returned the typed deferred checkpoint result."));

        ProtocolEnvelope inboundChunkedSnapshot = CreateRequest(
            ProtocolMessageTypes.SnapshotRequest,
            runId,
            6,
            "{\"probe\":\"" + new string('x', 10 * 1024) + "\"}");
        AdminPortRequestResult chunkedInboundResult = await client.RequestChunkedAsync(inboundChunkedSnapshot, requestTimeout, cancellationToken);
        EnsureSuccess(chunkedInboundResult);
        RequirePayloadProperty(chunkedInboundResult.Response!, "chunked_payload_bytes");
        checks.Add(Pass("chunk-inbound-10kb", "ArenaGS reassembled and verified a 10 KiB logical request payload."));

        ProtocolEnvelope outboundChunkedSnapshot = CreateRequest(
            ProtocolMessageTypes.SnapshotRequest,
            runId,
            7,
            "{\"chunk_probe_bytes\":10240}");
        AdminPortRequestResult chunkedOutboundResult = await RequireResultAsync(client, outboundChunkedSnapshot, requestTimeout, cancellationToken);
        if (!chunkedOutboundResult.Response!.Payload.TryGetProperty("chunk_probe", out JsonElement probe) ||
            probe.ValueKind != JsonValueKind.String ||
            probe.GetString()?.Length != 10 * 1024)
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolChunkInvalid, "ArenaGS did not produce a verified 10 KiB chunked response.");
        }

        checks.Add(Pass("chunk-outbound-10kb", "The .NET bridge reassembled and verified a 10 KiB ArenaGS response payload."));

        ProtocolEnvelope incompleteChunkSource = CreateRequest(
            ProtocolMessageTypes.SnapshotRequest,
            runId,
            15,
            "{\"probe\":\"" + new string('t', 1024) + "\"}");
        IReadOnlyList<ProtocolEnvelope> incompleteChunks = AdminPortChunking.ChunkRequest(incompleteChunkSource);
        if (incompleteChunks.Count < 2)
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolChunkInvalid, "The chunk timeout probe did not produce multiple bounded chunks.");
        }

        AdminPortRequestResult chunkTimeout = await client.RequestAsync(
            incompleteChunks[0],
            requestTimeout,
            cancellationToken);
        if (chunkTimeout.Succeeded || !string.Equals(chunkTimeout.ErrorCode, ArenaErrorCodes.ProtocolChunkTimeout, StringComparison.Ordinal))
        {
            throw new BridgeSmokeException(
                ArenaErrorCodes.ProtocolChunkTimeout,
                "ArenaGS did not deterministically expire the intentionally incomplete chunk transfer.");
        }

        checks.Add(Pass("chunk-timeout", "ArenaGS expired an intentionally incomplete chunk transfer with the deterministic timeout error."));

    }

    private static async Task FinalizeProtocolAsync(
        AdminPortBridgeClient client,
        string runId,
        List<Phase03BridgeCheck> checks,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        ProtocolEnvelope finalize = CreateRequest(ProtocolMessageTypes.FinalizeRequest, runId, 8, "{}");
        _ = await RequireResultAsync(client, finalize, requestTimeout, cancellationToken);
        checks.Add(Pass("finalize", "ArenaGS acknowledged a correlated finalization request."));
    }

    private static async Task VerifyGameScriptEventLoopAsync(
        AdminPortBridgeClient client,
        string runId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProtocolEnvelope probe = CreateRequest(ProtocolMessageTypes.Hello, runId, 0, "{}") with
        {
            MessageId = "bridge-probe-message",
            CorrelationId = "bridge-probe-correlation",
            IdempotencyKey = "bridge-probe-key",
        };
        AdminPortRequestResult hello = await client.RequestAsync(probe, timeout, cancellationToken);
        if (!hello.Succeeded || hello.Response is null)
        {
            throw new BridgeSmokeException(
                hello.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable,
                "ArenaGS did not return the initial authenticated event-loop probe result.");
        }

        if (IsPaused(hello.Response))
        {
            throw new BridgeSmokeException(
                ArenaErrorCodes.RunGameScriptNotReady,
                "OpenTTD paused before ArenaGS completed the initial authenticated event-loop probe.");
        }

        ProtocolEnvelope secondProbe = CreateRequest(ProtocolMessageTypes.Heartbeat, runId, 0, "{}") with
        {
            MessageId = "bridge-probe-message-2",
            CorrelationId = "bridge-probe-correlation-2",
            IdempotencyKey = "bridge-probe-key-2",
        };
        AdminPortRequestResult heartbeat = await client.RequestAsync(secondProbe, timeout, cancellationToken);
        if (!heartbeat.Succeeded || heartbeat.Response is null)
        {
            throw new BridgeSmokeException(
                heartbeat.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable,
                "ArenaGS did not return the successive authenticated event-loop probe result.");
        }

        if (IsPaused(heartbeat.Response))
        {
            throw new BridgeSmokeException(
                ArenaErrorCodes.RunGameScriptNotReady,
                "OpenTTD paused before ArenaGS completed the successive authenticated event-loop probe.");
        }

    }

    private static bool IsPaused(ProtocolEnvelope response) =>
        response.Payload.ValueKind == JsonValueKind.Object &&
        response.Payload.TryGetProperty("paused", out JsonElement paused) &&
        paused.ValueKind == JsonValueKind.True;

    private static async Task RunSharedFixtureChecksAsync(
        AdminPortBridgeClient client,
        string repositoryRoot,
        string runId,
        List<Phase03BridgeCheck> checks,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        const string relativeFixturePath = "schemas/protocol/examples/phase03-adminport-fixtures.v1.json";
        string normalizedRoot = Path.GetFullPath(repositoryRoot);
        string fixturePath = Path.GetFullPath(Path.Combine(normalizedRoot, relativeFixturePath));
        if (!fixturePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fixturePath))
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "The checked-in Phase 03 protocol fixtures are unavailable.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath, cancellationToken));
        }
        catch (JsonException exception)
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, $"The checked-in Phase 03 protocol fixtures are invalid: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("cases", out JsonElement cases) ||
                cases.ValueKind != JsonValueKind.Array ||
                cases.GetArrayLength() is < 1 or > 16)
            {
                throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "The checked-in Phase 03 protocol fixtures have an invalid shape.");
            }

            foreach (JsonElement testCase in cases.EnumerateArray())
            {
                if (testCase.ValueKind != JsonValueKind.Object ||
                    !testCase.TryGetProperty("id", out JsonElement id) ||
                    id.ValueKind != JsonValueKind.String ||
                    !ProtocolEnvelopeValidator.IsIdentifier(id.GetString()) ||
                    !testCase.TryGetProperty("expected_error_code", out JsonElement expectedError) ||
                    !testCase.TryGetProperty("envelope", out JsonElement fixtureEnvelope) ||
                    fixtureEnvelope.ValueKind != JsonValueKind.Object)
                {
                    throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "The checked-in Phase 03 protocol fixtures contain an invalid case.");
                }

                string rawFixture = fixtureEnvelope.GetRawText().Replace("{{run_id}}", runId, StringComparison.Ordinal);
                using JsonDocument materialized = JsonDocument.Parse(rawFixture);
                JsonElement envelope = materialized.RootElement;
                if (!envelope.TryGetProperty("correlation_id", out JsonElement correlation) ||
                    correlation.ValueKind != JsonValueKind.String ||
                    !ProtocolEnvelopeValidator.IsIdentifier(correlation.GetString()) ||
                    !envelope.TryGetProperty("message_type", out JsonElement messageType) ||
                    messageType.ValueKind != JsonValueKind.String)
                {
                    throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "A materialized Phase 03 protocol fixture has no safe correlation metadata.");
                }

                // OpenTTD's GameScript event conversion consumes JSON, not
                // source formatting. Canonicalize the checked-in object
                // without dropping its invalid-field cases before placing
                // it on the native AdminPort string boundary.
                byte[] rawBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
                try
                {
                    AdminPortRequestResult result = await client.RequestFixtureAsync(
                        rawBytes,
                        correlation.GetString()!,
                        expectedError.ValueKind == JsonValueKind.Null
                            ? FixtureExpectedResponseTypes(messageType.GetString()!)
                            : new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.Error },
                        requestTimeout,
                        cancellationToken);
                    if (expectedError.ValueKind == JsonValueKind.Null)
                    {
                        if (!result.Succeeded || result.Response is null)
                        {
                            string diagnostics = string.Join(",", client.SafeDiagnostics);
                            throw new BridgeSmokeException(
                                result.ErrorCode ?? ArenaErrorCodes.ProtocolInvalidMessage,
                                string.IsNullOrEmpty(diagnostics)
                                    ? "ArenaGS rejected a valid shared protocol fixture."
                                    : "ArenaGS rejected a valid shared protocol fixture (safe transport diagnostics: " + diagnostics + ").");
                        }
                    }
                    else if (expectedError.ValueKind != JsonValueKind.String ||
                             result.Succeeded ||
                             !string.Equals(result.ErrorCode, expectedError.GetString(), StringComparison.Ordinal))
                    {
                        throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "ArenaGS did not return the shared fixture's expected rejection code.");
                    }

                    checks.Add(Pass($"fixture-{id.GetString()}", "ArenaGS produced the expected result for the shared protocol fixture."));
                }
                finally
                {
                    Array.Clear(rawBytes, 0, rawBytes.Length);
                }
            }
        }
    }

    private static HashSet<string> FixtureExpectedResponseTypes(string requestType) =>
        requestType switch
        {
            ProtocolMessageTypes.Hello => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.Capabilities },
            ProtocolMessageTypes.SnapshotRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.SnapshotResult },
            _ => throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "The checked-in Phase 03 protocol fixture has no allowed response mapping."),
        };

    private static async Task<AdminPortRequestResult> RequireResultAsync(
        AdminPortBridgeClient client,
        ProtocolEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AdminPortRequestResult result = await client.RequestAsync(request, timeout, cancellationToken);
        EnsureSuccess(result);
        return result;
    }

    private static void EnsureSuccess(AdminPortRequestResult result)
    {
        if (!result.Succeeded || result.Response is null)
        {
            throw new BridgeSmokeException(
                result.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable,
                result.UserMessage);
        }
    }

    private static void RequirePayloadProperty(ProtocolEnvelope response, string property)
    {
        if (response.Payload.ValueKind != JsonValueKind.Object || !response.Payload.TryGetProperty(property, out _))
        {
            throw new BridgeSmokeException(ArenaErrorCodes.ProtocolInvalidMessage, "ArenaGS returned a response that did not satisfy the Phase 03 message contract.");
        }
    }

    private async Task<IManagedArenaProcess> StartServerAsync(
        ArenaLocalConfiguration configuration,
        Phase02ServerWorkspace workspace,
        CancellationToken cancellationToken) =>
        await _processFactory.StartAsync(
            new OpenTtdProcessStartRequest(
                workspace.ComponentId,
                configuration.OpenTtd.Executable,
                workspace.WorkingDirectory,
                [
                    "-D",
                    "-d",
                    "script=4,net=4",
                    "-c",
                    workspace.ConfigurationPath,
                    "-g",
                    "-G",
                    Phase02SmokeDefaults.StartingSaveSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                workspace.StandardOutputLogPath,
                workspace.StandardErrorLogPath,
                HasWindow: false),
            cancellationToken);

    private async Task StopServerAsync(
        IManagedArenaProcess server,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!server.HasExited)
        {
            try
            {
                await _consoleBridge.SendAsync(server.ProcessId, OpenTtdConsoleCommand.Quit, cancellationToken);
            }
            catch (OpenTtdConsoleControlException)
            {
                // Process supervision still has a force-termination fallback.
            }
        }

        if (!await server.WaitForExitAsync(timeout, cancellationToken))
        {
            await server.ForceTerminateAsync(cancellationToken);
        }
    }

    private static ProtocolEnvelope CreateRequest(string messageType, string runId, int sequence, string payloadJson)
    {
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return new ProtocolEnvelope
        {
            ProtocolVersion = ContractVersions.ProtocolV1,
            MessageType = messageType,
            RunId = runId,
            MessageId = $"bridge-message-{sequence}",
            CorrelationId = $"bridge-correlation-{sequence}",
            IdempotencyKey = $"bridge-key-{sequence}",
            Payload = document.RootElement.Clone(),
        };
    }

    private static Phase03BridgeCheck Pass(string id, string detail) => new(id, true, null, detail);

    private async Task<ArenaRunState> TransitionAsync(
        RunLifecycleJournal journal,
        ArenaRunState state,
        string? componentId,
        CancellationToken cancellationToken)
    {
        await journal.TransitionAsync(
            state,
            _timeProvider.GetUtcNow(),
            componentId,
            null,
            null,
            null,
            cancellationToken);
        return state;
    }

    private static void EnsureRuntimeIsReady(ArenaLocalConfiguration configuration)
    {
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            configuration.Runtime.Root,
            configuration.Network.BindAddress,
            configuration.OpenTtd.AdminPort);
        if (!inspection.IsValid || !File.Exists(configuration.OpenTtd.Executable))
        {
            throw new BridgeSmokeException(
                ArenaErrorCodes.RuntimeLayoutInvalid,
                "The isolated OpenTTD runtime is not valid. Rerun bootstrap before bridge-smoke.");
        }
    }

    private static bool UsesLegacyAdminPortAuthentication(string executable)
    {
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(executable);
        return version.FileMajorPart is >= 14 and < 15;
    }

    private static async Task WriteResultAsync(
        RunPathPolicy paths,
        Phase03BridgeSmokeResult result,
        CancellationToken cancellationToken)
    {
        string resultPath = paths.Resolve(ResultFileName);
        string temporaryPath = paths.Resolve(".bridge-result.pending.json");
        string content = JsonSerializer.Serialize(result, ResultJsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(temporaryPath, content, Utf8WithoutBom, cancellationToken);
        paths.EnsureSafePath(temporaryPath);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string SafeDetail(string detail)
    {
        string redacted = ArtifactTextRedactor.Redact(detail);
        return redacted.Length <= 512 ? redacted : redacted[..512];
    }

    private sealed class BridgeSmokeException : Exception
    {
        public BridgeSmokeException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public string ErrorCode { get; }
    }
}
