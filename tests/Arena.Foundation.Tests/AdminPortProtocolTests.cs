using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.AdminProtocol;
using OpenTtd.ModelArena.Contracts;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class AdminPortProtocolTests
{
    [Fact]
    public void EncodesAnAuthenticatedAdminJoinWithoutExposingTheCredentialInProtocolContracts()
    {
        byte[] packet = AdminPortPacketCodec.EncodeAdminJoin(Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"));

        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet));
        Assert.Equal((byte)AdminPortPacketType.AdminJoin, packet[2]);
        Assert.Equal(0, packet[^1]);
        Assert.Contains("OpenTTD Model Arena", Encoding.UTF8.GetString(packet), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains=equals")]
    [InlineData("12345678901234567890123456789012")]
    public void RejectsUnsafeAdminPortCredentials(string value)
    {
        Assert.Throws<ArgumentException>(() => AdminPortPacketCodec.EncodeAdminJoin(Encoding.ASCII.GetBytes(value)));
    }

    [Fact]
    public void ParsesTheOpenTtdGameScriptProtocolCapability()
    {
        byte[] payload =
        [
            AdminPortPacketCodec.OpenTtdAdminProtocolVersion,
            1,
            9,
            0,
            0x40,
            0,
            0,
        ];
        AdminPortPacket packet = new(AdminPortPacketType.ServerProtocol, payload);

        Assert.True(AdminPortPacketCodec.TryParseServerProtocol(packet, out AdminPortProtocolInfo? protocol));
        Assert.NotNull(protocol);
        Assert.Equal(AdminPortPacketCodec.OpenTtdAdminProtocolVersion, protocol.Version);
        Assert.True(protocol.SupportedUpdates[AdminPortUpdateType.GameScript].HasFlag(AdminPortUpdateFrequency.Automatic));
    }

    [Fact]
    public void RekeysEachSecureAdminPortRecordAndRejectsTampering()
    {
        byte[] key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] nonce = Enumerable.Range(32, 24).Select(value => (byte)value).ToArray();
        byte[] firstPlaintext = Encoding.ASCII.GetBytes("first authenticated record");
        byte[] secondPlaintext = Encoding.ASCII.GetBytes("second authenticated record");
        byte[] firstWire = firstPlaintext.ToArray();
        byte[] secondWire = secondPlaintext.ToArray();
        byte[] firstMac = new byte[16];
        byte[] secondMac = new byte[16];
        try
        {
            using OpenTtdAdminRecordCipher sender = new(key, nonce);
            using OpenTtdAdminRecordCipher receiver = new(key, nonce);

            sender.Encrypt(firstWire, firstMac);
            sender.Encrypt(secondWire, secondMac);

            Assert.True(receiver.TryDecrypt(firstMac, firstWire));
            Assert.Equal(firstPlaintext, firstWire);
            Assert.True(receiver.TryDecrypt(secondMac, secondWire));
            Assert.Equal(secondPlaintext, secondWire);
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
            Array.Clear(nonce, 0, nonce.Length);
            Array.Clear(firstPlaintext, 0, firstPlaintext.Length);
            Array.Clear(secondPlaintext, 0, secondPlaintext.Length);
            Array.Clear(firstWire, 0, firstWire.Length);
            Array.Clear(secondWire, 0, secondWire.Length);
            Array.Clear(firstMac, 0, firstMac.Length);
            Array.Clear(secondMac, 0, secondMac.Length);
        }

        byte[] tamperKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        byte[] tamperNonce = Enumerable.Range(33, 24).Select(value => (byte)value).ToArray();
        byte[] tamperedWire = Encoding.ASCII.GetBytes("tampered record");
        byte[] tamperedMac = new byte[16];
        try
        {
            using OpenTtdAdminRecordCipher sender = new(tamperKey, tamperNonce);
            using OpenTtdAdminRecordCipher receiver = new(tamperKey, tamperNonce);
            sender.Encrypt(tamperedWire, tamperedMac);
            tamperedMac[0] ^= 0x80;

            Assert.False(receiver.TryDecrypt(tamperedMac, tamperedWire));
        }
        finally
        {
            Array.Clear(tamperKey, 0, tamperKey.Length);
            Array.Clear(tamperNonce, 0, tamperNonce.Length);
            Array.Clear(tamperedWire, 0, tamperedWire.Length);
            Array.Clear(tamperedMac, 0, tamperedMac.Length);
        }
    }

    [Fact]
    public void RejectsAnEnvelopeWithUnknownFieldsOrMissingIdempotency()
    {
        string unknownField =
            "{\"protocol_version\":\"1.0\",\"message_type\":\"heartbeat\",\"run_id\":\"run-1\",\"message_id\":\"message-1\",\"correlation_id\":\"correlation-1\",\"idempotency_key\":\"key-1\",\"payload\":{},\"unexpected\":true}";
        ProtocolValidationResult unknown = ProtocolEnvelopeValidator.TryParse(Encoding.UTF8.GetBytes(unknownField), out _);
        ProtocolValidationResult noKey = ProtocolEnvelopeValidator.Validate(CreateEnvelope(ProtocolMessageTypes.PauseRequest, null));

        Assert.False(unknown.IsValid);
        Assert.Equal(ArenaErrorCodes.ProtocolInvalidMessage, unknown.ErrorCode);
        Assert.False(noKey.IsValid);
        Assert.Equal(ArenaErrorCodes.ProtocolInvalidMessage, noKey.ErrorCode);
    }

    [Fact]
    public void ChunksAndReassemblesATenKilobyteLogicalPayload()
    {
        ProtocolEnvelope request = CreateEnvelope(
            ProtocolMessageTypes.SnapshotRequest,
            "snapshot-key",
            "{\"probe\":\"" + new string('x', 10 * 1024) + "\"}");
        IReadOnlyList<ProtocolEnvelope> chunks = AdminPortChunking.ChunkRequest(request);
        ManualTimeProvider clock = new();
        AdminPortChunkReassembler reassembler = new(TimeSpan.FromSeconds(10), clock);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(ProtocolEnvelopeValidator.Validate(chunk).IsValid));

        AdminPortChunkReassemblyResult result = default!;
        foreach (ProtocolEnvelope chunk in chunks)
        {
            result = reassembler.Accept(chunk);
        }

        Assert.True(result.Completed);
        Assert.NotNull(result.LogicalEnvelope);
        Assert.Equal(request.MessageType, result.LogicalEnvelope.MessageType);
        Assert.Equal(request.IdempotencyKey, result.LogicalEnvelope.IdempotencyKey);
        Assert.Equal(request.Payload.GetRawText(), result.LogicalEnvelope.Payload.GetRawText());
    }

    [Fact]
    public void RejectsChecksumMismatchAndExpiresIncompleteTransfers()
    {
        ProtocolEnvelope request = CreateEnvelope(
            ProtocolMessageTypes.SnapshotRequest,
            "snapshot-key",
            "{\"probe\":\"" + new string('z', 2 * 1024) + "\"}");
        IReadOnlyList<ProtocolEnvelope> chunks = AdminPortChunking.ChunkRequest(request);
        ManualTimeProvider clock = new();
        AdminPortChunkReassembler reassembler = new(TimeSpan.FromSeconds(1), clock);

        Assert.False(reassembler.Accept(chunks[0]).Completed);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(reassembler.PurgeExpired());

        AdminPortChunkReassemblyResult timedOut = reassembler.Accept(chunks[0]);
        Assert.False(timedOut.Completed);
        Assert.Equal(ArenaErrorCodes.ProtocolChunkTimeout, timedOut.ErrorCode);

        ProtocolEnvelope freshRequest = request with
        {
            MessageId = "message-2",
            CorrelationId = "correlation-2",
            IdempotencyKey = "snapshot-key-2",
        };
        IReadOnlyList<ProtocolEnvelope> freshChunks = AdminPortChunking.ChunkRequest(freshRequest);
        ProtocolEnvelope altered = freshChunks[0] with { Payload = ReplaceChunkData(freshChunks[0].Payload, "corrupted") };
        AdminPortChunkReassemblyResult result = reassembler.Accept(altered);
        foreach (ProtocolEnvelope chunk in freshChunks.Skip(1))
        {
            result = reassembler.Accept(chunk);
        }

        Assert.False(result.Completed);
        Assert.Equal(ArenaErrorCodes.ProtocolChunkInvalid, result.ErrorCode);
    }

    [Fact]
    public void AppliesTheSharedPhaseThreeValidAndInvalidFixtures()
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "protocol",
            "phase03-adminport-fixtures.v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixturePath));

        foreach (JsonElement testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = testCase.GetProperty("id").GetString()!;
            JsonElement expectedError = testCase.GetProperty("expected_error_code");
            ProtocolValidationResult result = ProtocolEnvelopeValidator.TryParse(
                Encoding.UTF8.GetBytes(testCase.GetProperty("envelope").GetRawText().Replace("{{run_id}}", "run-20260724-01", StringComparison.Ordinal)),
                out ProtocolEnvelope? envelope);

            if (expectedError.ValueKind == JsonValueKind.Null)
            {
                Assert.True(result.IsValid, id);
                Assert.NotNull(envelope);
            }
            else
            {
                Assert.False(result.IsValid, id);
                Assert.Equal(expectedError.GetString(), result.ErrorCode);
                Assert.Null(envelope);
            }
        }
    }

    [Fact]
    public async Task RejectsAnIncompatibleOpenTtdAdminProtocolBeforeStartingRequests()
    {
        using TcpListener listener = StartListener();
        Task server = Task.Run(async () =>
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            NetworkStream stream = connection.GetStream();
            AdminPortPacket join = await AdminPortPacketCodec.ReadAsync(stream, CancellationToken.None);
            Assert.Equal(AdminPortPacketType.AdminJoin, join.Type);
            await SendPacketAsync(stream, AdminPortPacketType.ServerProtocol, CreateProtocolPayload(version: 2));
        });

        AdminPortWireException exception = await Assert.ThrowsAsync<AdminPortWireException>(() =>
            AdminPortBridgeClient.ConnectAsync(
                CreateOptions(GetPort(listener)),
                Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
                CancellationToken.None));

        Assert.Equal(ArenaErrorCodes.AdminPortProtocolIncompatible, exception.ErrorCode);
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FallsBackToLegacyAuthenticationOnlyWhenItIsExplicitlyAllowed()
    {
        using TcpListener listener = StartListener();
        Task server = Task.Run(async () =>
        {
            using (TcpClient secureAttempt = await listener.AcceptTcpClientAsync())
            {
                NetworkStream stream = secureAttempt.GetStream();
                AdminPortPacket secureJoin = await AdminPortPacketCodec.ReadAsync(stream, CancellationToken.None);
                Assert.Equal(AdminPortPacketType.AdminJoinSecure, secureJoin.Type);
            }

            using TcpClient legacyAttempt = await listener.AcceptTcpClientAsync();
            NetworkStream legacyStream = await CompleteHandshakeAsync(legacyAttempt);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            _ = legacyStream;
        });

        await using AdminPortBridgeClient client = await AdminPortBridgeClient.ConnectAsync(
            CreateSecureOptions(GetPort(listener), allowLegacyFallback: true),
            Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
            CancellationToken.None);

        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DoesNotDowngradeAuthenticationWithoutAnExplicitLegacyCompatibilityDecision()
    {
        using TcpListener listener = StartListener();
        Task server = Task.Run(async () =>
        {
            using TcpClient secureAttempt = await listener.AcceptTcpClientAsync();
            NetworkStream stream = secureAttempt.GetStream();
            AdminPortPacket secureJoin = await AdminPortPacketCodec.ReadAsync(stream, CancellationToken.None);
            Assert.Equal(AdminPortPacketType.AdminJoinSecure, secureJoin.Type);
        });

        AdminPortWireException exception = await Assert.ThrowsAsync<AdminPortWireException>(() =>
            AdminPortBridgeClient.ConnectAsync(
                CreateSecureOptions(GetPort(listener), allowLegacyFallback: false),
                Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
                CancellationToken.None));

        Assert.Equal(ArenaErrorCodes.AdminPortAuthenticationFailed, exception.ErrorCode);
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReconnectsAndResendsAnIdempotentHeartbeatAfterTheTransportDrops()
    {
        using TcpListener listener = StartListener();
        List<string> receivedKeys = [];
        Task server = Task.Run(async () =>
        {
            for (int connectionIndex = 0; connectionIndex < 2; connectionIndex++)
            {
                using TcpClient connection = await listener.AcceptTcpClientAsync();
                NetworkStream stream = await CompleteHandshakeAsync(connection);
                ProtocolEnvelope request = await ReadRequestAsync(stream);
                receivedKeys.Add(request.IdempotencyKey!);
                if (connectionIndex == 0)
                {
                    continue;
                }

                await SendEnvelopeAsync(stream, CreateResponse(request, ProtocolMessageTypes.Heartbeat, "{}"));
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        });

        await using AdminPortBridgeClient client = await AdminPortBridgeClient.ConnectAsync(
            CreateOptions(GetPort(listener)),
            Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
            CancellationToken.None);
        ProtocolEnvelope heartbeat = CreateEnvelope(ProtocolMessageTypes.Heartbeat, "heartbeat-key");
        AdminPortRequestResult result = await client.RequestAsync(
            heartbeat,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        await server.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.UserMessage}; stale={string.Join(',', client.StaleCorrelations)}");
        Assert.Equal(ProtocolMessageTypes.Heartbeat, result.Response?.MessageType);
        Assert.Equal(["heartbeat-key", "heartbeat-key"], receivedKeys);
    }

    [Fact]
    public async Task DeliberatelyReconnectsAHealthyTransportBeforeContinuingWithANewRequest()
    {
        using TcpListener listener = StartListener();
        Task server = Task.Run(async () =>
        {
            using (TcpClient firstConnection = await listener.AcceptTcpClientAsync())
            {
                NetworkStream firstStream = await CompleteHandshakeAsync(firstConnection);
                byte[] closed = new byte[1];
                try
                {
                    Assert.Equal(0, await firstStream.ReadAsync(closed, CancellationToken.None));
                }
                finally
                {
                    Array.Clear(closed, 0, closed.Length);
                }
            }

            using TcpClient secondConnection = await listener.AcceptTcpClientAsync();
            NetworkStream secondStream = await CompleteHandshakeAsync(secondConnection);
            ProtocolEnvelope request = await ReadRequestAsync(secondStream);
            await SendEnvelopeAsync(secondStream, CreateResponse(request, ProtocolMessageTypes.Heartbeat, "{}"));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        });

        await using AdminPortBridgeClient client = await AdminPortBridgeClient.ConnectAsync(
            CreateOptions(GetPort(listener)),
            Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
            CancellationToken.None);
        AdminPortSendResult reconnect = await client.ReconnectForValidationAsync(CancellationToken.None);
        Assert.True(reconnect.Accepted, $"{reconnect.ErrorCode}: {reconnect.TechnicalMessage}");

        AdminPortRequestResult result = await client.RequestAsync(
            CreateEnvelope(ProtocolMessageTypes.Heartbeat, "reconnected-heartbeat-key"),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await server.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.UserMessage}");
        Assert.Equal(ProtocolMessageTypes.Heartbeat, result.Response?.MessageType);
    }

    [Fact]
    public async Task RecordsAStaleCorrelationWithoutFailingTheMatchingRequest()
    {
        using TcpListener listener = StartListener();
        Task server = Task.Run(async () =>
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            NetworkStream stream = await CompleteHandshakeAsync(connection);
            ProtocolEnvelope request = await ReadRequestAsync(stream);
            ProtocolEnvelope stale = CreateResponse(request, ProtocolMessageTypes.Heartbeat, "{}") with
            {
                CorrelationId = "stale-correlation",
            };
            await SendEnvelopeAsync(stream, stale);
            await SendEnvelopeAsync(stream, CreateResponse(request, ProtocolMessageTypes.Heartbeat, "{}"));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        });

        await using AdminPortBridgeClient client = await AdminPortBridgeClient.ConnectAsync(
            CreateOptions(GetPort(listener)),
            Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
            CancellationToken.None);
        ProtocolEnvelope heartbeat = CreateEnvelope(ProtocolMessageTypes.Heartbeat, "heartbeat-key");
        AdminPortRequestResult result = await client.RequestAsync(
            heartbeat,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.UserMessage}; stale={string.Join(',', client.StaleCorrelations)}");
        Assert.Contains("stale-correlation", client.StaleCorrelations);
    }

    [Fact]
    public async Task ClassifiesAResponseTimeoutDeterministically()
    {
        using TcpListener listener = StartListener();
        Task server = Task.Run(async () =>
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            NetworkStream stream = await CompleteHandshakeAsync(connection);
            _ = await ReadRequestAsync(stream);
            await Task.Delay(TimeSpan.FromMilliseconds(350));
        });

        await using AdminPortBridgeClient client = await AdminPortBridgeClient.ConnectAsync(
            CreateOptions(GetPort(listener)),
            Encoding.ASCII.GetBytes("A9-b_C!dEfGhJkLmNpQrStUv"),
            CancellationToken.None);
        AdminPortRequestResult result = await client.RequestAsync(
            CreateEnvelope(ProtocolMessageTypes.Heartbeat, "heartbeat-key"),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ArenaErrorCodes.AdminPortRequestTimedOut, result.ErrorCode);
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static ProtocolEnvelope CreateEnvelope(string messageType, string? idempotencyKey, string payload = "{}")
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return new ProtocolEnvelope
        {
            ProtocolVersion = ContractVersions.ProtocolV1,
            MessageType = messageType,
            RunId = "run-1",
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            IdempotencyKey = idempotencyKey,
            Payload = document.RootElement.Clone(),
        };
    }

    private static JsonElement ReplaceChunkData(JsonElement payload, string replacement)
    {
        Dictionary<string, object?> values = [];
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            values[property.Name] = property.Name == "data" ? replacement : JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(values));
        return document.RootElement.Clone();
    }

    private static TcpListener StartListener()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int GetPort(TcpListener listener) =>
        ((IPEndPoint)listener.LocalEndpoint).Port;

    private static AdminPortClientOptions CreateOptions(int port) =>
        new(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            PreferSecureAuthentication: false);

    private static AdminPortClientOptions CreateSecureOptions(int port, bool allowLegacyFallback) =>
        new(
            "127.0.0.1",
            port,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            PreferSecureAuthentication: true,
            AllowLegacyPasswordAuthentication: allowLegacyFallback);

    private static async Task<NetworkStream> CompleteHandshakeAsync(TcpClient connection)
    {
        NetworkStream stream = connection.GetStream();
        AdminPortPacket join = await AdminPortPacketCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(AdminPortPacketType.AdminJoin, join.Type);
        await SendPacketAsync(
            stream,
            AdminPortPacketType.ServerProtocol,
            CreateProtocolPayload(AdminPortPacketCodec.OpenTtdAdminProtocolVersion));
        await SendPacketAsync(stream, AdminPortPacketType.ServerWelcome, Array.Empty<byte>());

        AdminPortPacket subscription = await AdminPortPacketCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(AdminPortPacketType.AdminUpdateFrequency, subscription.Type);
        return stream;
    }

    private static async Task<ProtocolEnvelope> ReadRequestAsync(NetworkStream stream)
    {
        while (true)
        {
            AdminPortPacket packet = await AdminPortPacketCodec.ReadAsync(stream, CancellationToken.None);
            if (packet.Type == AdminPortPacketType.AdminPing)
            {
                await SendPacketAsync(stream, AdminPortPacketType.ServerPong, packet.Payload);
                continue;
            }

            Assert.Equal(AdminPortPacketType.AdminGameScript, packet.Type);
            byte[] payload = packet.Payload.ToArray();
            try
            {
                Assert.True(payload.Length > 1 && payload[^1] == 0);
                ProtocolValidationResult parsed = ProtocolEnvelopeValidator.TryParse(payload.AsSpan(0, payload.Length - 1), out ProtocolEnvelope? request);
                Assert.True(parsed.IsValid, parsed.UserMessage);
                return request!;
            }
            finally
            {
                Array.Clear(payload, 0, payload.Length);
            }
        }
    }

    private static async Task SendEnvelopeAsync(NetworkStream stream, ProtocolEnvelope envelope)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        byte[] payload = new byte[json.Length + 1];
        try
        {
            Buffer.BlockCopy(json, 0, payload, 0, json.Length);
            await SendPacketAsync(stream, AdminPortPacketType.ServerGameScript, payload);
        }
        finally
        {
            Array.Clear(json, 0, json.Length);
            Array.Clear(payload, 0, payload.Length);
        }
    }

    private static async Task SendPacketAsync(
        NetworkStream stream,
        AdminPortPacketType type,
        ReadOnlyMemory<byte> payload)
    {
        byte[] packet = new byte[payload.Length + 3];
        try
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet, checked((ushort)packet.Length));
            packet[2] = (byte)type;
            payload.Span.CopyTo(packet.AsSpan(3));
            await stream.WriteAsync(packet, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        finally
        {
            Array.Clear(packet, 0, packet.Length);
        }
    }

    private static byte[] CreateProtocolPayload(byte version) =>
    [
        version,
        1,
        (byte)AdminPortUpdateType.GameScript,
        0,
        (byte)AdminPortUpdateFrequency.Automatic,
        0,
        0,
    ];

    private static ProtocolEnvelope CreateResponse(
        ProtocolEnvelope request,
        string messageType,
        string payloadJson)
    {
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return new ProtocolEnvelope
        {
            ProtocolVersion = ContractVersions.ProtocolV1,
            MessageType = messageType,
            RunId = request.RunId,
            MessageId = "server-response-01",
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            Payload = document.RootElement.Clone(),
        };
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
