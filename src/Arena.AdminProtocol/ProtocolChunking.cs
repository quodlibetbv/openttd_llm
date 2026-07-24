using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.AdminProtocol;

public static class AdminPortChunking
{
    public const int MaximumChunkCount = 48;
    public const int MaximumChunkDataCharacters = 512;
    public const int MaximumLogicalPayloadBytes = ProtocolEnvelopeValidator.MaximumLogicalPayloadBytes;
    public const string EncodingName = "base64_utf8";

    public static IReadOnlyList<ProtocolEnvelope> ChunkRequest(ProtocolEnvelope logicalRequest)
    {
        ProtocolValidationResult validation = ProtocolEnvelopeValidator.Validate(logicalRequest);
        if (!validation.IsValid || !ProtocolMessageTypes.RetriableRequests.Contains(logicalRequest.MessageType))
        {
            throw new ArgumentException("Only valid retriable protocol requests can be chunked.", nameof(logicalRequest));
        }

        byte[] logicalPayload = Encoding.UTF8.GetBytes(logicalRequest.Payload.GetRawText());
        try
        {
            if (logicalPayload.Length is 0 or > MaximumLogicalPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(logicalRequest), "The logical payload exceeds the chunk protocol limit.");
            }

            string encodedPayload = Convert.ToBase64String(logicalPayload);
            string checksum = Adler32(encodedPayload);
            string transferId = CreateTransferId(logicalRequest);
            int totalChunks = checked((encodedPayload.Length + MaximumChunkDataCharacters - 1) / MaximumChunkDataCharacters);
            if (totalChunks > MaximumChunkCount)
            {
                throw new ArgumentOutOfRangeException(nameof(logicalRequest), "The logical payload would exceed the chunk-count limit.");
            }

            List<ProtocolEnvelope> chunks = new(totalChunks);
            for (int sequence = 0; sequence < totalChunks; sequence++)
            {
                int offset = sequence * MaximumChunkDataCharacters;
                int length = Math.Min(MaximumChunkDataCharacters, encodedPayload.Length - offset);
                string data = encodedPayload.Substring(offset, length);
                chunks.Add(CreateChunkEnvelope(logicalRequest, transferId, sequence, totalChunks, logicalPayload.Length, checksum, data));
            }

            return chunks;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(logicalPayload);
        }
    }

    public static string Adler32(string value)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (char character in value)
        {
            if (character > 0x7f)
            {
                throw new ArgumentException("Chunk checksums operate on ASCII transfer data only.", nameof(value));
            }

            a = (a + character) % modulus;
            b = (b + a) % modulus;
        }

        return ((b << 16) | a).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ProtocolEnvelope CreateChunkEnvelope(
        ProtocolEnvelope logicalRequest,
        string transferId,
        int sequence,
        int totalChunks,
        int logicalBytes,
        string checksum,
        string data)
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                transfer_id = transferId,
                logical_message_type = logicalRequest.MessageType,
                logical_message_id = logicalRequest.MessageId,
                logical_correlation_id = logicalRequest.CorrelationId,
                logical_idempotency_key = logicalRequest.IdempotencyKey,
                sequence,
                total_chunks = totalChunks,
                logical_bytes = logicalBytes,
                encoding = EncodingName,
                checksum,
                data,
            }));
        return new ProtocolEnvelope
        {
            ProtocolVersion = logicalRequest.ProtocolVersion,
            MessageType = ProtocolMessageTypes.Chunk,
            RunId = logicalRequest.RunId,
            MessageId = $"chunk-{transferId}-{sequence:D2}",
            CorrelationId = logicalRequest.CorrelationId,
            IdempotencyKey = logicalRequest.IdempotencyKey,
            Payload = document.RootElement.Clone(),
        };
    }

    private static string CreateTransferId(ProtocolEnvelope envelope)
    {
        string source = envelope.MessageId + "-" + envelope.CorrelationId;
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source));
        try
        {
            return "x" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed record AdminPortChunkReassemblyResult(
    bool Completed,
    ProtocolEnvelope? LogicalEnvelope,
    string? ErrorCode,
    string UserMessage);

public sealed record AdminPortChunkExpiry(string TransferId, string CorrelationId);

/// <summary>
/// Bounded, correlation-preserving chunk reassembly for GameScript messages
/// received by the .NET bridge. The same metadata is interpreted by ArenaGS;
/// no partially assembled data is dispatched as a command.
/// </summary>
public sealed class AdminPortChunkReassembler
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, PendingTransfer> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _expiredTransfers = new(StringComparer.Ordinal);

    public AdminPortChunkReassembler(TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        if (timeout is { TotalSeconds: <= 0 or > 60 })
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AdminPortChunkReassemblyResult Accept(ProtocolEnvelope chunk)
    {
        _ = PurgeExpiredTransfers();
        ProtocolValidationResult envelopeValidation = ProtocolEnvelopeValidator.Validate(chunk);
        if (!envelopeValidation.IsValid)
        {
            return Failure(envelopeValidation.ErrorCode ?? ArenaErrorCodes.ProtocolChunkInvalid, envelopeValidation.UserMessage);
        }

        if (!string.Equals(chunk.MessageType, ProtocolMessageTypes.Chunk, StringComparison.Ordinal))
        {
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "The received message is not a chunk envelope.");
        }

        if (!TryParseChunk(chunk, out ParsedChunk? parsed, out string? error) || parsed is null)
        {
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, error ?? "The chunk envelope is invalid.");
        }

        if (_expiredTransfers.ContainsKey(parsed.TransferId))
        {
            return Failure(ArenaErrorCodes.ProtocolChunkTimeout, "The chunk transfer already exceeded its bounded reassembly timeout.");
        }

        if (!_transfers.TryGetValue(parsed.TransferId, out PendingTransfer? transfer))
        {
            if (_transfers.Count >= 8)
            {
                return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "Too many incomplete protocol transfers are active.");
            }

            transfer = new PendingTransfer(parsed, _timeProvider.GetUtcNow());
            _transfers.Add(parsed.TransferId, transfer);
        }
        else if (!transfer.Matches(parsed))
        {
            _transfers.Remove(parsed.TransferId);
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "Chunk transfer metadata changed before reassembly completed.");
        }

        if (!transfer.Parts.TryAdd(parsed.Sequence, parsed.Data) &&
            !string.Equals(transfer.Parts[parsed.Sequence], parsed.Data, StringComparison.Ordinal))
        {
            _transfers.Remove(parsed.TransferId);
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "A duplicate chunk sequence carried different data.");
        }

        if (transfer.Parts.Count != transfer.TotalChunks)
        {
            return new AdminPortChunkReassemblyResult(false, null, null, "Chunk accepted; awaiting remaining parts.");
        }

        _transfers.Remove(parsed.TransferId);
        StringBuilder encoded = new();
        for (int sequence = 0; sequence < transfer.TotalChunks; sequence++)
        {
            if (!transfer.Parts.TryGetValue(sequence, out string? part))
            {
                return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "A chunk transfer completed with a missing sequence.");
            }

            encoded.Append(part);
        }

        string encodedPayload = encoded.ToString();
        if (!string.Equals(AdminPortChunking.Adler32(encodedPayload), transfer.Checksum, StringComparison.Ordinal))
        {
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "The completed protocol transfer did not match its checksum.");
        }

        try
        {
            byte[] payloadBytes = Convert.FromBase64String(encodedPayload);
            try
            {
                if (payloadBytes.Length != transfer.LogicalBytes)
                {
                    return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "The completed protocol transfer did not match its declared size.");
                }

                using JsonDocument payloadDocument = JsonDocument.Parse(payloadBytes);
                if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "The reassembled protocol payload must be an object.");
                }

                ProtocolEnvelope logical = new()
                {
                    ProtocolVersion = ContractVersions.ProtocolV1,
                    MessageType = transfer.LogicalMessageType,
                    RunId = transfer.RunId,
                    MessageId = transfer.LogicalMessageId,
                    CorrelationId = transfer.LogicalCorrelationId,
                    IdempotencyKey = transfer.LogicalIdempotencyKey,
                    Payload = payloadDocument.RootElement.Clone(),
                };
                ProtocolValidationResult validation = ProtocolEnvelopeValidator.Validate(logical);
                return validation.IsValid
                    ? new AdminPortChunkReassemblyResult(true, logical, null, "Chunk transfer reassembled.")
                    : Failure(validation.ErrorCode ?? ArenaErrorCodes.ProtocolChunkInvalid, validation.UserMessage);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(payloadBytes);
            }
        }
        catch (FormatException)
        {
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "The completed protocol transfer is not valid base64 data.");
        }
        catch (JsonException)
        {
            return Failure(ArenaErrorCodes.ProtocolChunkInvalid, "The completed protocol transfer is not valid JSON.");
        }
    }

    public IReadOnlyList<AdminPortChunkExpiry> PurgeExpiredTransfers()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<AdminPortChunkExpiry> expired = [];
        foreach ((string transferId, PendingTransfer transfer) in _transfers)
        {
            if (now - transfer.CreatedUtc > _timeout)
            {
                expired.Add(new AdminPortChunkExpiry(transferId, transfer.LogicalCorrelationId));
            }
        }

        foreach (AdminPortChunkExpiry transfer in expired)
        {
            _transfers.Remove(transfer.TransferId);
            _expiredTransfers[transfer.TransferId] = now;
        }

        foreach (string transferId in _expiredTransfers
                     .Where(entry => now - entry.Value > _timeout)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _expiredTransfers.Remove(transferId);
        }

        while (_expiredTransfers.Count > 8)
        {
            string oldest = _expiredTransfers.OrderBy(entry => entry.Value).First().Key;
            _expiredTransfers.Remove(oldest);
        }

        return expired;
    }

    public IReadOnlyList<string> PurgeExpired() =>
        PurgeExpiredTransfers().Select(transfer => transfer.TransferId).ToArray();

    private static AdminPortChunkReassemblyResult Failure(string code, string message) =>
        new(false, null, code, message);

    private static bool TryParseChunk(ProtocolEnvelope envelope, out ParsedChunk? parsed, out string? error)
    {
        parsed = null;
        error = null;
        JsonElement payload = envelope.Payload;
        string[] required =
        [
            "transfer_id",
            "logical_message_type",
            "logical_message_id",
            "logical_correlation_id",
            "logical_idempotency_key",
            "sequence",
            "total_chunks",
            "logical_bytes",
            "encoding",
            "checksum",
            "data",
        ];
        if (payload.ValueKind != JsonValueKind.Object ||
            required.Any(name => !payload.TryGetProperty(name, out _)) ||
            payload.EnumerateObject().Any(property => !required.Contains(property.Name, StringComparer.Ordinal)))
        {
            error = "The chunk payload is missing a required field.";
            return false;
        }

        if (!TryGetIdentifier(payload, "transfer_id", out string? transferId) ||
            !TryGetIdentifier(payload, "logical_message_id", out string? logicalMessageId) ||
            !TryGetIdentifier(payload, "logical_correlation_id", out string? logicalCorrelationId) ||
            !TryGetIdentifier(payload, "logical_idempotency_key", out string? logicalIdempotencyKey) ||
            !payload.TryGetProperty("logical_message_type", out JsonElement messageType) ||
            messageType.ValueKind != JsonValueKind.String ||
            !ProtocolMessageTypes.All.Contains(messageType.GetString()!) ||
            string.Equals(messageType.GetString(), ProtocolMessageTypes.Chunk, StringComparison.Ordinal) ||
            !payload.TryGetProperty("sequence", out JsonElement sequence) || !sequence.TryGetInt32(out int sequenceValue) ||
            !payload.TryGetProperty("total_chunks", out JsonElement total) || !total.TryGetInt32(out int totalValue) ||
            !payload.TryGetProperty("logical_bytes", out JsonElement logicalBytes) || !logicalBytes.TryGetInt32(out int logicalByteValue) ||
            !payload.TryGetProperty("encoding", out JsonElement encoding) || encoding.ValueKind != JsonValueKind.String || !string.Equals(encoding.GetString(), AdminPortChunking.EncodingName, StringComparison.Ordinal) ||
            !payload.TryGetProperty("checksum", out JsonElement checksum) || checksum.ValueKind != JsonValueKind.String || !IsChecksum(checksum.GetString()) ||
            !payload.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.String || data.GetString() is not { } dataText ||
            dataText.Length is 0 or > AdminPortChunking.MaximumChunkDataCharacters || !IsBase64Data(dataText))
        {
            error = "The chunk payload contains an invalid field.";
            return false;
        }

        if (totalValue is <= 0 or > AdminPortChunking.MaximumChunkCount ||
            sequenceValue < 0 || sequenceValue >= totalValue ||
            logicalByteValue is <= 0 or > AdminPortChunking.MaximumLogicalPayloadBytes ||
            !string.Equals(envelope.CorrelationId, logicalCorrelationId, StringComparison.Ordinal) ||
            !string.Equals(envelope.IdempotencyKey, logicalIdempotencyKey, StringComparison.Ordinal))
        {
            error = "The chunk payload exceeds a bounded transfer limit.";
            return false;
        }

        parsed = new ParsedChunk(
            transferId!,
            envelope.RunId,
            messageType.GetString()!,
            logicalMessageId!,
            logicalCorrelationId!,
            logicalIdempotencyKey!,
            sequenceValue,
            totalValue,
            logicalByteValue,
            checksum.GetString()!,
            dataText);
        return true;
    }

    private static bool TryGetIdentifier(JsonElement payload, string name, out string? value)
    {
        value = null;
        return payload.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String &&
            ProtocolEnvelopeValidator.IsIdentifier(value = property.GetString());
    }

    private static bool IsChecksum(string? value) =>
        value is { Length: 8 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsBase64Data(string value)
    {
        int firstPadding = value.IndexOf('=');
        if (firstPadding == 0 ||
            (firstPadding >= 0 && (firstPadding < value.Length - 2 ||
            value.Skip(firstPadding).Any(character => character != '=')))
        )
        {
            return false;
        }

        return value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=');
    }

    private sealed class PendingTransfer
    {
        public PendingTransfer(ParsedChunk first, DateTimeOffset createdUtc)
        {
            TransferId = first.TransferId;
            RunId = first.RunId;
            LogicalMessageType = first.LogicalMessageType;
            LogicalMessageId = first.LogicalMessageId;
            LogicalCorrelationId = first.LogicalCorrelationId;
            LogicalIdempotencyKey = first.LogicalIdempotencyKey;
            TotalChunks = first.TotalChunks;
            LogicalBytes = first.LogicalBytes;
            Checksum = first.Checksum;
            CreatedUtc = createdUtc;
        }

        public string TransferId { get; }

        public string RunId { get; }

        public string LogicalMessageType { get; }

        public string LogicalMessageId { get; }

        public string LogicalCorrelationId { get; }

        public string LogicalIdempotencyKey { get; }

        public int TotalChunks { get; }

        public int LogicalBytes { get; }

        public string Checksum { get; }

        public DateTimeOffset CreatedUtc { get; }

        public Dictionary<int, string> Parts { get; } = [];

        public bool Matches(ParsedChunk other) =>
            string.Equals(RunId, other.RunId, StringComparison.Ordinal) &&
            string.Equals(LogicalMessageType, other.LogicalMessageType, StringComparison.Ordinal) &&
            string.Equals(LogicalMessageId, other.LogicalMessageId, StringComparison.Ordinal) &&
            string.Equals(LogicalCorrelationId, other.LogicalCorrelationId, StringComparison.Ordinal) &&
            string.Equals(LogicalIdempotencyKey, other.LogicalIdempotencyKey, StringComparison.Ordinal) &&
            TotalChunks == other.TotalChunks &&
            LogicalBytes == other.LogicalBytes &&
            string.Equals(Checksum, other.Checksum, StringComparison.Ordinal);
    }

    private sealed record ParsedChunk(
        string TransferId,
        string RunId,
        string LogicalMessageType,
        string LogicalMessageId,
        string LogicalCorrelationId,
        string LogicalIdempotencyKey,
        int Sequence,
        int TotalChunks,
        int LogicalBytes,
        string Checksum,
        string Data);
}
