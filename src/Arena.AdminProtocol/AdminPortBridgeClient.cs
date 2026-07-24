using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.AdminProtocol;

public sealed record AdminPortClientOptions(
    string Host,
    int Port,
    TimeSpan ConnectTimeout,
    TimeSpan KeepAliveInterval,
    TimeSpan KeepAliveTimeout,
    int QueueCapacity = 32,
    int ReconnectAttempts = 3,
    bool PreferSecureAuthentication = true,
    bool AllowLegacyPasswordAuthentication = false)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(Host, out IPAddress? address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("AdminPort connections must use a loopback address.", nameof(Host));
        }

        if (Port is < 1024 or > 65535 ||
            ConnectTimeout is { TotalSeconds: <= 0 or > 60 } ||
            KeepAliveInterval is { TotalSeconds: <= 0 or > 60 } ||
            KeepAliveTimeout is { TotalSeconds: <= 0 or > 30 } ||
            QueueCapacity is < 1 or > 256 ||
            ReconnectAttempts is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "AdminPort client options are outside their bounded limits.");
        }
    }
}

public sealed record AdminPortRequestResult(
    bool Succeeded,
    ProtocolEnvelope? Response,
    string? ErrorCode,
    string UserMessage)
{
    public static AdminPortRequestResult Failure(string code, string message) => new(false, null, code, message);
}

/// <summary>
/// Authenticated, loopback-only AdminPort client. It owns no gameplay policy:
/// it transports only already-validated Arena envelopes to ArenaGS and matches
/// replies by correlation ID. Bounded queues, reconnect, and resend are kept
/// here so the orchestrator cannot accidentally bypass the protocol boundary.
/// </summary>
public sealed class AdminPortBridgeClient : IAdminPortTransport, IAsyncDisposable
{
    // NETWORK_ERROR_ILLEGAL_PACKET. OpenTTD 14.x returns this for the unknown
    // ADMIN_JOIN_SECURE packet, so it is the only safe automatic legacy signal.
    private const byte NetworkErrorIllegalPacket = 4;
    private readonly AdminPortClientOptions _options;
    private readonly byte[] _password;
    private readonly Channel<OutboundEnvelope> _outbound;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<bool>> _pings = new();
    private readonly ConcurrentQueue<string> _staleCorrelations = new();
    private readonly ConcurrentQueue<string> _safeDiagnostics = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AdminPortChunkReassembler _chunkReassembler;
    private readonly TimeProvider _timeProvider;
    private ActiveTransport? _transport;
    private Task? _senderTask;
    private Task? _receiverTask;
    private Task? _keepAliveTask;
    private int _nextPingNonce;
    private bool _disposed;

    private AdminPortBridgeClient(
        AdminPortClientOptions options,
        ReadOnlySpan<byte> password,
        TimeProvider? timeProvider)
    {
        options.Validate();
        if (!AdminPortPacketCodec.IsSupportedPassword(password))
        {
            throw new ArgumentException("The AdminPort credential must be a dedicated 1 to 31 character printable ASCII secret.", nameof(password));
        }

        _options = options;
        _password = password.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chunkReassembler = new AdminPortChunkReassembler(TimeSpan.FromSeconds(10), _timeProvider);
        _outbound = Channel.CreateBounded<OutboundEnvelope>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public static async Task<AdminPortBridgeClient> ConnectAsync(
        AdminPortClientOptions options,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        AdminPortBridgeClient client = new(options, password.Span, timeProvider);
        try
        {
            await client.ConnectTransportAsync(cancellationToken);
            client._senderTask = Task.Run(client.RunSenderAsync);
            client._receiverTask = Task.Run(client.RunReceiverAsync);
            client._keepAliveTask = Task.Run(client.RunKeepAliveAsync);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public IReadOnlyList<string> StaleCorrelations => _staleCorrelations.ToArray();

    /// <summary>
    /// Bounded packet-class diagnostics for run artifacts. Entries deliberately
    /// contain neither credentials nor provider/GameScript payload text.
    /// </summary>
    public IReadOnlyList<string> SafeDiagnostics => _safeDiagnostics.ToArray();

    public async Task<AdminPortSendResult> SendAsync(
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ProtocolValidationResult validation = ProtocolEnvelopeValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            return new AdminPortSendResult(false, validation.ErrorCode, validation.UserMessage);
        }

        return await EnqueueAsync([envelope], cancellationToken);
    }

    public Task<AdminPortRequestResult> RequestAsync(
        ProtocolEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RequestAsync([request], request.CorrelationId, ExpectedResponseTypes(request.MessageType), timeout, cancellationToken);

    public Task<AdminPortRequestResult> RequestChunkedAsync(
        ProtocolEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ProtocolEnvelope> chunks = AdminPortChunking.ChunkRequest(request);
            return RequestAsync(chunks, request.CorrelationId, ExpectedResponseTypes(request.MessageType), timeout, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(AdminPortRequestResult.Failure(
                ArenaErrorCodes.ProtocolMessageTooLarge,
                exception.Message));
        }
    }

    /// <summary>
    /// Sends one checked-in protocol fixture without applying the local envelope
    /// validator first. This exists only so the orchestrator can prove that the
    /// C# and ArenaGS validators reject the same malformed fixture bytes. It is
    /// internal to the trusted orchestration boundary and never receives model
    /// or provider data.
    /// </summary>
    internal async Task<AdminPortRequestResult> RequestFixtureAsync(
        ReadOnlyMemory<byte> rawEnvelope,
        string correlationId,
        IReadOnlySet<string> expectedTypes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (rawEnvelope.Length is 0 or >= AdminPortPacketCodec.MaximumGameScriptJsonBytes ||
            !ProtocolEnvelopeValidator.IsIdentifier(correlationId) ||
            expectedTypes.Count == 0 ||
            timeout is { TotalSeconds: <= 0 or > 60 })
        {
            return AdminPortRequestResult.Failure(
                ArenaErrorCodes.ProtocolInvalidMessage,
                "The checked-in protocol fixture has invalid transport bounds.");
        }

        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return AdminPortRequestResult.Failure(ArenaErrorCodes.AdminPortUnavailable, "The AdminPort client is not active.");
        }

        PendingRequest pending = new([], expectedTypes);
        if (!_pending.TryAdd(correlationId, pending))
        {
            return AdminPortRequestResult.Failure(
                ArenaErrorCodes.ProtocolStaleCorrelation,
                "A protocol fixture reused a correlation ID that is still pending.");
        }

        try
        {
            try
            {
                await SendRawFixtureAsync(rawEnvelope, cancellationToken);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                if (!await ReconnectAsync(cancellationToken))
                {
                    return AdminPortRequestResult.Failure(
                        ArenaErrorCodes.AdminPortReconnectFailed,
                        "The AdminPort connection could not be restored for the protocol fixture.");
                }

                await SendRawFixtureAsync(rawEnvelope, cancellationToken);
            }

            try
            {
                return await pending.Completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return AdminPortRequestResult.Failure(
                    ArenaErrorCodes.AdminPortRequestTimedOut,
                    "ArenaGS did not return a fixture result before the protocol timeout.");
            }
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>
    /// Deliberately replaces an otherwise healthy connection for the trusted
    /// Phase 03 smoke proof. It refuses to run while a request is pending, so
    /// the validation action cannot cause an application command to execute
    /// twice outside the regular reconnect/resend path.
    /// </summary>
    internal async Task<AdminPortSendResult> ReconnectForValidationAsync(CancellationToken cancellationToken)
    {
        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return new AdminPortSendResult(false, ArenaErrorCodes.AdminPortUnavailable, "The AdminPort client is not active.");
        }

        if (!_pending.IsEmpty)
        {
            return new AdminPortSendResult(
                false,
                ArenaErrorCodes.AdminPortReconnectFailed,
                "A validation reconnect cannot begin while a protocol request is pending.");
        }

        ActiveTransport transport;
        try
        {
            transport = GetActiveTransport();
        }
        catch (IOException)
        {
            return new AdminPortSendResult(false, ArenaErrorCodes.AdminPortUnavailable, "The AdminPort client is not connected.");
        }

        return await ReconnectAsync(cancellationToken, transport)
            ? new AdminPortSendResult(true, null, null)
            : new AdminPortSendResult(false, ArenaErrorCodes.AdminPortReconnectFailed, "The AdminPort connection could not be restored.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outbound.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            await SendQuitBestEffortAsync();
        }
        catch (IOException)
        {
            // The supervised server may have already stopped.
        }

        CloseTransport();
        Task?[] startedTasks = [_senderTask, _receiverTask, _keepAliveTask];
        Task[] tasks = startedTasks
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal.
        }
        catch (TimeoutException)
        {
            // All transports are already disposed; do not prolong shutdown.
        }

        foreach (PendingRequest pending in _pending.Values)
        {
            pending.Complete(AdminPortRequestResult.Failure(
                ArenaErrorCodes.AdminPortUnavailable,
                "The AdminPort client was disposed before the request completed."));
        }

        foreach (TaskCompletionSource<bool> ping in _pings.Values)
        {
            ping.TrySetCanceled();
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_password);
        _lifetime.Dispose();
        _writeLock.Dispose();
        _reconnectLock.Dispose();
    }

    private async Task<AdminPortRequestResult> RequestAsync(
        IReadOnlyList<ProtocolEnvelope> envelopes,
        string correlationId,
        IReadOnlySet<string> expectedTypes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (envelopes.Count == 0 || timeout is { TotalSeconds: <= 0 or > 60 })
        {
            return AdminPortRequestResult.Failure(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol request has invalid bounds.");
        }

        foreach (ProtocolEnvelope envelope in envelopes)
        {
            ProtocolValidationResult validation = ProtocolEnvelopeValidator.Validate(envelope);
            if (!validation.IsValid)
            {
                return AdminPortRequestResult.Failure(validation.ErrorCode ?? ArenaErrorCodes.ProtocolInvalidMessage, validation.UserMessage);
            }
        }

        PendingRequest pending = new(envelopes, expectedTypes);
        if (!_pending.TryAdd(correlationId, pending))
        {
            return AdminPortRequestResult.Failure(
                ArenaErrorCodes.ProtocolStaleCorrelation,
                "A request with the same correlation ID is still pending.");
        }

        try
        {
            AdminPortSendResult sent = await EnqueueAsync(envelopes, cancellationToken);
            if (!sent.Accepted)
            {
                return AdminPortRequestResult.Failure(
                    sent.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable,
                    sent.TechnicalMessage ?? "The AdminPort request could not be queued.");
            }

            try
            {
                return await pending.Completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return AdminPortRequestResult.Failure(
                    ArenaErrorCodes.AdminPortRequestTimedOut,
                    "ArenaGS did not return a correlated result before the protocol timeout.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    private async Task<AdminPortSendResult> EnqueueAsync(
        IReadOnlyList<ProtocolEnvelope> envelopes,
        CancellationToken cancellationToken)
    {
        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return new AdminPortSendResult(false, ArenaErrorCodes.AdminPortUnavailable, "The AdminPort client is not active.");
        }

        AdminPortSendResult? preflightFailure = ValidateOutboundEnvelopes(envelopes);
        if (preflightFailure is not null)
        {
            return preflightFailure;
        }

        TaskCompletionSource<AdminPortSendResult> delivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OutboundEnvelope outbound = new(envelopes, delivery);
        if (!_outbound.Writer.TryWrite(outbound))
        {
            return new AdminPortSendResult(false, ArenaErrorCodes.AdminPortQueueFull, "The bounded AdminPort outbound queue is full.");
        }

        return await delivery.Task.WaitAsync(cancellationToken);
    }

    private async Task RunSenderAsync()
    {
        try
        {
            await foreach (OutboundEnvelope outbound in _outbound.Reader.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    await SendEnvelopesAsync(outbound.Envelopes, _lifetime.Token);
                    outbound.Delivery.TrySetResult(new AdminPortSendResult(true, null, null));
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    bool reconnected = await ReconnectAsync(_lifetime.Token);
                    if (reconnected)
                    {
                        try
                        {
                            await SendEnvelopesAsync(outbound.Envelopes, _lifetime.Token);
                            outbound.Delivery.TrySetResult(new AdminPortSendResult(true, null, null));
                            continue;
                        }
                        catch (Exception retryException) when (IsTransportFailure(retryException))
                        {
                            AdminPortSendResult failure = ClassifySendFailure(retryException);
                            outbound.Delivery.TrySetResult(failure);
                            FailPendingFor(outbound.Envelopes, failure.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable, failure.TechnicalMessage ?? "The AdminPort connection failed while sending a request.");
                            continue;
                        }
                    }

                    outbound.Delivery.TrySetResult(new AdminPortSendResult(false, ArenaErrorCodes.AdminPortReconnectFailed, "The AdminPort connection could not be restored."));
                    FailPendingFor(outbound.Envelopes, ArenaErrorCodes.AdminPortReconnectFailed, "The AdminPort connection could not be restored.");
                }
                catch (Exception exception)
                {
                    AdminPortSendResult failure = ClassifySendFailure(exception);
                    outbound.Delivery.TrySetResult(failure);
                    FailPendingFor(outbound.Envelopes, failure.ErrorCode ?? ArenaErrorCodes.AdminPortUnavailable, failure.TechnicalMessage ?? "The AdminPort request could not be sent.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected client shutdown.
        }
        finally
        {
            while (_outbound.Reader.TryRead(out OutboundEnvelope? pending))
            {
                pending.Delivery.TrySetResult(new AdminPortSendResult(
                    false,
                    ArenaErrorCodes.AdminPortUnavailable,
                    "The AdminPort client stopped before the queued request was sent."));
            }
        }
    }

    private async Task RunReceiverAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            ActiveTransport? transport = null;
            try
            {
                transport = GetActiveTransport();
                AdminPortPacket packet = await AdminPortPacketCodec.ReadAsync(
                    transport.Stream,
                    transport.ReceiveEncryption,
                    _lifetime.Token);
                RecordDiagnostic($"packet-{(byte)packet.Type}");
                await HandleIncomingPacketAsync(packet);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException or AdminPortWireException or ObjectDisposedException)
            {
                RecordDiagnostic("transport-reconnect");
                if (!await ReconnectAsync(_lifetime.Token, transport))
                {
                    FailAllPending(ArenaErrorCodes.AdminPortReconnectFailed, "The AdminPort connection was lost and could not be restored.");
                    return;
                }
            }
            catch (Exception)
            {
                FailAllPending(ArenaErrorCodes.ProtocolInvalidMessage, "AdminPort sent a protocol packet that could not be processed safely.");
                return;
            }
        }
    }

    private async Task RunKeepAliveAsync()
    {
        try
        {
            using PeriodicTimer timer = new(_options.KeepAliveInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                ExpireIncomingTransfers();
                uint nonce = unchecked((uint)Interlocked.Increment(ref _nextPingNonce));
                TaskCompletionSource<bool> pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pings.TryAdd(nonce, pong))
                {
                    continue;
                }

                try
                {
                    await SendPacketAsync(AdminPortPacketCodec.EncodePing(nonce), _lifetime.Token);
                    await pong.Task.WaitAsync(_options.KeepAliveTimeout, _lifetime.Token);
                }
                catch (TimeoutException)
                {
                    if (!await ReconnectAsync(_lifetime.Token))
                    {
                        FailAllPending(ArenaErrorCodes.AdminPortReconnectFailed, "The AdminPort keepalive failed and the connection could not be restored.");
                        return;
                    }
                }
                catch (IOException)
                {
                    if (!await ReconnectAsync(_lifetime.Token))
                    {
                        FailAllPending(ArenaErrorCodes.AdminPortReconnectFailed, "The AdminPort keepalive failed and the connection could not be restored.");
                        return;
                    }
                }
                finally
                {
                    _pings.TryRemove(nonce, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected client shutdown.
        }
    }

    private Task HandleIncomingPacketAsync(AdminPortPacket packet)
    {
        if (AdminPortPacketCodec.TryReadPong(packet, out uint nonce))
        {
            if (_pings.TryRemove(nonce, out TaskCompletionSource<bool>? pong))
            {
                pong.TrySetResult(true);
            }

            return Task.CompletedTask;
        }

        if (AdminPortPacketCodec.TryReadServerError(packet, out _))
        {
            RecordDiagnostic("server-error");
            FailAllPending(ArenaErrorCodes.AdminPortUnavailable, "OpenTTD rejected an authenticated AdminPort packet.");
            return Task.CompletedTask;
        }

        if (!AdminPortPacketCodec.TryReadGameScriptJson(packet, out ReadOnlyMemory<byte> json))
        {
            RecordDiagnostic("non-gamescript");
            return Task.CompletedTask;
        }

        ProtocolValidationResult parsed = ProtocolEnvelopeValidator.TryParse(json.Span, out ProtocolEnvelope? envelope);
        if (!parsed.IsValid || envelope is null)
        {
            RecordDiagnostic("gamescript-invalid");
            return Task.CompletedTask;
        }

        RecordDiagnostic("gamescript-" + envelope.MessageType);

        if (string.Equals(envelope.MessageType, ProtocolMessageTypes.Chunk, StringComparison.Ordinal))
        {
            AdminPortChunkReassemblyResult reassembly = _chunkReassembler.Accept(envelope);
            if (!reassembly.Completed || reassembly.LogicalEnvelope is null)
            {
                if (reassembly.ErrorCode is not null &&
                    _pending.TryGetValue(envelope.CorrelationId, out PendingRequest? chunkPending))
                {
                    chunkPending.Complete(AdminPortRequestResult.Failure(reassembly.ErrorCode, reassembly.UserMessage));
                }

                return Task.CompletedTask;
            }

            envelope = reassembly.LogicalEnvelope;
        }

        if (!_pending.TryGetValue(envelope.CorrelationId, out PendingRequest? pending))
        {
            RecordDiagnostic("stale-correlation");
            RecordStaleCorrelation(envelope.CorrelationId);
            return Task.CompletedTask;
        }

        if (string.Equals(envelope.MessageType, ProtocolMessageTypes.Error, StringComparison.Ordinal))
        {
            RecordDiagnostic("matched-error");
            pending.Complete(AdminPortRequestResult.Failure(
                ReadErrorCode(envelope.Payload) ?? ArenaErrorCodes.ProtocolInvalidMessage,
                "ArenaGS rejected the correlated protocol request."));
            return Task.CompletedTask;
        }

        if (string.Equals(envelope.MessageType, ProtocolMessageTypes.ActionProgress, StringComparison.Ordinal) ||
            (string.Equals(envelope.MessageType, ProtocolMessageTypes.Heartbeat, StringComparison.Ordinal) &&
             !pending.ExpectedTypes.Contains(ProtocolMessageTypes.Heartbeat)))
        {
            // Progress and unsolicited health notices are non-terminal. They
            // must not turn a still-valid request into a stale correlation.
            return Task.CompletedTask;
        }

        if (!pending.ExpectedTypes.Contains(envelope.MessageType))
        {
            RecordDiagnostic("unexpected-response");
            pending.Complete(AdminPortRequestResult.Failure(
                ArenaErrorCodes.ProtocolStaleCorrelation,
                "ArenaGS returned a message type that does not match the pending request."));
            return Task.CompletedTask;
        }

        RecordDiagnostic("matched-response");
        pending.Complete(new AdminPortRequestResult(true, envelope, null, "ArenaGS returned a correlated result."));
        return Task.CompletedTask;
    }

    private async Task ConnectTransportAsync(CancellationToken cancellationToken)
    {
        IPAddress address = IPAddress.Parse(_options.Host);
        if (_options.PreferSecureAuthentication)
        {
            try
            {
                InstallTransport(await ConnectSecureTransportAsync(address, cancellationToken));
                return;
            }
            catch (SecureHandshakeNotSupportedException) when (_options.AllowLegacyPasswordAuthentication)
            {
                // OpenTTD 14.x does not implement ADMIN_JOIN_SECURE. Only its
                // explicit illegal-packet response permits the legacy path;
                // never downgrade after a secure server has started PAKE.
            }
        }

        InstallTransport(await ConnectLegacyTransportAsync(address, cancellationToken));
    }

    private async Task<ActiveTransport> ConnectSecureTransportAsync(IPAddress address, CancellationToken cancellationToken)
    {
        TcpClient? client = new(address.AddressFamily);
        NetworkStream? stream = null;
        OpenTtdAdminRecordCipher? sendEncryption = null;
        OpenTtdAdminRecordCipher? receiveEncryption = null;
        string handshakeStage = "opening the secure AdminPort connection";
        try
        {
            await client.ConnectAsync(address, _options.Port, cancellationToken).AsTask().WaitAsync(_options.ConnectTimeout, cancellationToken);
            stream = client.GetStream();
            using OpenTtdSecureAdminSession session = new(_password);
            using CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(_options.ConnectTimeout);

            handshakeStage = "waiting for OpenTTD's secure authentication request";
            await WritePacketAsync(stream, OpenTtdSecureAdminSession.CreateJoinPacket(), encryption: null, handshakeTimeout.Token);
            AdminPortPacket authenticationRequest = await AdminPortPacketCodec.ReadAsync(stream, handshakeTimeout.Token);
            if (AdminPortPacketCodec.TryReadServerError(authenticationRequest, out byte error))
            {
                if (error == NetworkErrorIllegalPacket)
                {
                    throw new SecureHandshakeNotSupportedException();
                }

                throw new AdminPortWireException(
                    ArenaErrorCodes.AdminPortAuthenticationFailed,
                    "OpenTTD rejected secure AdminPort authentication before the credential exchange.");
            }

            ThrowIfConnectionRejected(authenticationRequest);
            if (authenticationRequest.Type != AdminPortPacketType.ServerAuthenticationRequest)
            {
                throw new AdminPortWireException(
                    ArenaErrorCodes.AdminPortProtocolIncompatible,
                    "OpenTTD did not begin the required secure AdminPort authentication handshake.");
            }

            handshakeStage = "waiting for OpenTTD's encryption transition";
            await WritePacketAsync(
                stream,
                session.CreateAuthenticationResponse(authenticationRequest),
                encryption: null,
                handshakeTimeout.Token);

            AdminPortPacket encryptionTransition = await AdminPortPacketCodec.ReadAsync(stream, handshakeTimeout.Token);
            if (AdminPortPacketCodec.TryReadServerError(encryptionTransition, out _))
            {
                throw new AdminPortWireException(
                    ArenaErrorCodes.AdminPortAuthenticationFailed,
                    "OpenTTD rejected the dedicated AdminPort credential.");
            }

            ThrowIfConnectionRejected(encryptionTransition);
            (sendEncryption, receiveEncryption) = session.CreateRecordCiphers(encryptionTransition);
            handshakeStage = "waiting for OpenTTD's encrypted protocol announcement";
            await CompleteProtocolHandshakeAsync(stream, receiveEncryption, sendEncryption, handshakeTimeout.Token);

            ActiveTransport connected = new(client, stream, sendEncryption, receiveEncryption);
            client = null;
            stream = null;
            sendEncryption = null;
            receiveEncryption = null;
            return connected;
        }
        catch (EndOfStreamException) when (
            _options.AllowLegacyPasswordAuthentication &&
            string.Equals(
                handshakeStage,
                "waiting for OpenTTD's secure authentication request",
                StringComparison.Ordinal))
        {
            throw new SecureHandshakeNotSupportedException();
        }
        catch (EndOfStreamException)
        {
            throw new AdminPortWireException(
                ArenaErrorCodes.AdminPortAuthenticationFailed,
                $"OpenTTD closed the secure AdminPort connection while {handshakeStage}.");
        }
        finally
        {
            receiveEncryption?.Dispose();
            sendEncryption?.Dispose();
            stream?.Dispose();
            client?.Dispose();
        }
    }

    private async Task<ActiveTransport> ConnectLegacyTransportAsync(IPAddress address, CancellationToken cancellationToken)
    {
        TcpClient? client = new(address.AddressFamily);
        NetworkStream? stream = null;
        try
        {
            await client.ConnectAsync(address, _options.Port, cancellationToken).AsTask().WaitAsync(_options.ConnectTimeout, cancellationToken);
            stream = client.GetStream();
            using CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(_options.ConnectTimeout);
            await WritePacketAsync(stream, AdminPortPacketCodec.EncodeAdminJoin(_password), encryption: null, handshakeTimeout.Token);
            await CompleteProtocolHandshakeAsync(stream, receiveEncryption: null, sendEncryption: null, handshakeTimeout.Token);

            ActiveTransport connected = new(client, stream, sendEncryption: null, receiveEncryption: null);
            client = null;
            stream = null;
            return connected;
        }
        finally
        {
            stream?.Dispose();
            client?.Dispose();
        }
    }

    private static async Task CompleteProtocolHandshakeAsync(
        NetworkStream stream,
        OpenTtdAdminRecordCipher? receiveEncryption,
        OpenTtdAdminRecordCipher? sendEncryption,
        CancellationToken cancellationToken)
    {
        AdminPortProtocolInfo? protocol = null;
        bool welcomed = false;
        while (!welcomed)
        {
            AdminPortPacket packet = await AdminPortPacketCodec.ReadAsync(stream, receiveEncryption, cancellationToken);
            if (AdminPortPacketCodec.TryReadServerError(packet, out _))
            {
                throw new AdminPortWireException(ArenaErrorCodes.AdminPortAuthenticationFailed, "OpenTTD rejected the AdminPort connection.");
            }

            ThrowIfConnectionRejected(packet);
            if (packet.Type == AdminPortPacketType.ServerProtocol)
            {
                if (!AdminPortPacketCodec.TryParseServerProtocol(packet, out protocol) || protocol is null)
                {
                    throw new AdminPortWireException(ArenaErrorCodes.AdminPortProtocolIncompatible, "OpenTTD sent an invalid AdminPort protocol announcement.");
                }

                if (protocol.Version != AdminPortPacketCodec.OpenTtdAdminProtocolVersion ||
                    !protocol.SupportedUpdates.TryGetValue(AdminPortUpdateType.GameScript, out AdminPortUpdateFrequency frequencies) ||
                    !frequencies.HasFlag(AdminPortUpdateFrequency.Automatic))
                {
                    throw new AdminPortWireException(ArenaErrorCodes.AdminPortProtocolIncompatible, "The OpenTTD AdminPort protocol does not support the required GameScript bridge.");
                }
            }
            else if (packet.Type == AdminPortPacketType.ServerWelcome)
            {
                welcomed = protocol is not null;
            }
        }

        await WritePacketAsync(
            stream,
            AdminPortPacketCodec.EncodeUpdateFrequency(
                AdminPortUpdateType.GameScript,
                AdminPortUpdateFrequency.Automatic),
            sendEncryption,
            cancellationToken);
    }

    private static void ThrowIfConnectionRejected(AdminPortPacket packet)
    {
        if (packet.Type == AdminPortPacketType.ServerFull || packet.Type == AdminPortPacketType.ServerBanned)
        {
            throw new AdminPortWireException(ArenaErrorCodes.AdminPortUnavailable, "OpenTTD did not accept this AdminPort connection.");
        }
    }

    private async Task<bool> ReconnectAsync(
        CancellationToken cancellationToken,
        ActiveTransport? failedTransport = null)
    {
        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return false;
        }

        await _reconnectLock.WaitAsync(cancellationToken);
        try
        {
            ActiveTransport? activeTransport = Volatile.Read(ref _transport);
            if (failedTransport is not null && !ReferenceEquals(activeTransport, failedTransport))
            {
                return activeTransport is not null;
            }

            CloseTransport();
            for (int attempt = 1; attempt <= _options.ReconnectAttempts; attempt++)
            {
                try
                {
                    await ConnectTransportAsync(cancellationToken);
                    await ResendPendingAsync(cancellationToken);
                    return true;
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested &&
                    exception is IOException or SocketException or AdminPortWireException or TimeoutException or OperationCanceledException)
                {
                    if (attempt == _options.ReconnectAttempts)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), _timeProvider, cancellationToken);
                }
            }

            return false;
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task ResendPendingAsync(CancellationToken cancellationToken)
    {
        foreach (PendingRequest pending in _pending.Values)
        {
            await SendEnvelopesAsync(pending.Envelopes, cancellationToken);
        }
    }

    private async Task SendEnvelopesAsync(IReadOnlyList<ProtocolEnvelope> envelopes, CancellationToken cancellationToken)
    {
        foreach (ProtocolEnvelope envelope in envelopes)
        {
            byte[] packet = AdminPortPacketCodec.EncodeGameScript(envelope);
            await SendPacketAsync(packet, cancellationToken);
            RecordDiagnostic("sent-envelope");
        }
    }

    private async Task SendRawFixtureAsync(ReadOnlyMemory<byte> rawEnvelope, CancellationToken cancellationToken)
    {
        byte[] packet = AdminPortPacketCodec.EncodeGameScript(rawEnvelope.Span);
        await SendPacketAsync(packet, cancellationToken);
        RecordDiagnostic("sent-fixture");
    }

    private async Task SendPacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            ActiveTransport transport = GetActiveTransport();
            await WritePacketAsync(transport.Stream, packet, transport.SendEncryption, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendQuitBestEffortAsync()
    {
        if (Volatile.Read(ref _transport) is null)
        {
            return;
        }

        await SendPacketAsync(AdminPortPacketCodec.EncodeQuit(), CancellationToken.None);
    }

    private ActiveTransport GetActiveTransport() =>
        Volatile.Read(ref _transport) ?? throw new IOException("The AdminPort transport is not connected.");

    private static async Task WritePacketAsync(
        NetworkStream stream,
        byte[] packet,
        OpenTtdAdminRecordCipher? encryption,
        CancellationToken cancellationToken)
    {
        byte[]? wirePacket = null;
        try
        {
            wirePacket = encryption is null ? packet : AdminPortPacketCodec.EncryptPacket(packet, encryption);
            await stream.WriteAsync(wirePacket, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            if (wirePacket is not null && !ReferenceEquals(wirePacket, packet))
            {
                Array.Clear(wirePacket, 0, wirePacket.Length);
            }

            Array.Clear(packet, 0, packet.Length);
        }
    }

    private void InstallTransport(ActiveTransport transport)
    {
        ActiveTransport? prior = Interlocked.Exchange(ref _transport, transport);
        prior?.Dispose();
    }

    private void CloseTransport()
    {
        ActiveTransport? transport = Interlocked.Exchange(ref _transport, null);
        transport?.Dispose();
    }

    private void FailAllPending(string code, string message)
    {
        foreach (PendingRequest pending in _pending.Values)
        {
            pending.Complete(AdminPortRequestResult.Failure(code, message));
        }
    }

    private void FailPendingFor(IReadOnlyList<ProtocolEnvelope> envelopes, string code, string message)
    {
        foreach (ProtocolEnvelope envelope in envelopes)
        {
            if (_pending.TryGetValue(envelope.CorrelationId, out PendingRequest? pending))
            {
                pending.Complete(AdminPortRequestResult.Failure(code, message));
            }
        }
    }

    private void RecordStaleCorrelation(string correlationId)
    {
        _staleCorrelations.Enqueue(correlationId);
        while (_staleCorrelations.Count > 32)
        {
            _ = _staleCorrelations.TryDequeue(out _);
        }
    }

    private void RecordDiagnostic(string value)
    {
        _safeDiagnostics.Enqueue(value);
        while (_safeDiagnostics.Count > 32)
        {
            _ = _safeDiagnostics.TryDequeue(out _);
        }
    }

    private static HashSet<string> ExpectedResponseTypes(string requestType) =>
        requestType switch
        {
            ProtocolMessageTypes.Hello => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.Capabilities },
            ProtocolMessageTypes.Heartbeat => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.Heartbeat },
            ProtocolMessageTypes.PauseRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.PauseResult },
            ProtocolMessageTypes.ResumeRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.ResumeResult },
            ProtocolMessageTypes.SnapshotRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.SnapshotResult },
            ProtocolMessageTypes.ActionRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.ActionResult },
            ProtocolMessageTypes.CameraRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.CameraResult },
            ProtocolMessageTypes.CheckpointRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.CheckpointResult },
            ProtocolMessageTypes.FinalizeRequest => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.FinalizeResult },
            _ => new HashSet<string>(StringComparer.Ordinal) { ProtocolMessageTypes.Error },
        };

    private void ExpireIncomingTransfers()
    {
        foreach (AdminPortChunkExpiry expiry in _chunkReassembler.PurgeExpiredTransfers())
        {
            if (_pending.TryGetValue(expiry.CorrelationId, out PendingRequest? pending))
            {
                pending.Complete(AdminPortRequestResult.Failure(
                    ArenaErrorCodes.ProtocolChunkTimeout,
                    "ArenaGS did not complete a chunked response before the protocol timeout."));
            }
        }
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException or SocketException or AdminPortWireException or ObjectDisposedException;

    private static AdminPortSendResult ClassifySendFailure(Exception exception) =>
        exception is ArgumentException or ArgumentOutOfRangeException
            ? new AdminPortSendResult(false, ArenaErrorCodes.ProtocolMessageTooLarge, "The protocol message exceeds the safe AdminPort wire size.")
            : new AdminPortSendResult(false, ArenaErrorCodes.AdminPortUnavailable, "The AdminPort connection failed while sending a request.");

    private static AdminPortSendResult? ValidateOutboundEnvelopes(IReadOnlyList<ProtocolEnvelope> envelopes)
    {
        foreach (ProtocolEnvelope envelope in envelopes)
        {
            byte[] json = ProtocolJson.Serialize(envelope);
            try
            {
                if (json.Length is 0 or >= AdminPortPacketCodec.MaximumGameScriptJsonBytes)
                {
                    return new AdminPortSendResult(
                        false,
                        ArenaErrorCodes.ProtocolMessageTooLarge,
                        "The protocol message must use bounded chunking before it can cross AdminPort.");
                }
            }
            finally
            {
                Array.Clear(json, 0, json.Length);
            }
        }

        return null;
    }

    private static string? ReadErrorCode(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("error_code", out JsonElement errorCode) &&
        errorCode.ValueKind == JsonValueKind.String
            ? errorCode.GetString()
            : null;

    private sealed class SecureHandshakeNotSupportedException : IOException
    {
    }

    private sealed class ActiveTransport : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private bool _disposed;

        public ActiveTransport(
            TcpClient client,
            NetworkStream stream,
            OpenTtdAdminRecordCipher? sendEncryption,
            OpenTtdAdminRecordCipher? receiveEncryption)
        {
            _client = client;
            _stream = stream;
            SendEncryption = sendEncryption;
            ReceiveEncryption = receiveEncryption;
        }

        public NetworkStream Stream => _stream;

        public OpenTtdAdminRecordCipher? SendEncryption { get; }

        public OpenTtdAdminRecordCipher? ReceiveEncryption { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReceiveEncryption?.Dispose();
            SendEncryption?.Dispose();
            _stream.Dispose();
            _client.Dispose();
        }
    }

    private sealed record OutboundEnvelope(
        IReadOnlyList<ProtocolEnvelope> Envelopes,
        TaskCompletionSource<AdminPortSendResult> Delivery);

    private sealed class PendingRequest
    {
        public PendingRequest(IReadOnlyList<ProtocolEnvelope> envelopes, IReadOnlySet<string> expectedTypes)
        {
            Envelopes = envelopes;
            ExpectedTypes = expectedTypes;
        }

        public IReadOnlyList<ProtocolEnvelope> Envelopes { get; }

        public IReadOnlySet<string> ExpectedTypes { get; }

        public TaskCompletionSource<AdminPortRequestResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(AdminPortRequestResult result) => Completion.TrySetResult(result);
    }
}
