using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;

namespace OpenTtd.ModelArena.Contracts;

public sealed record ProtocolEnvelope
{
    [JsonPropertyName("protocol_version")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("message_type")]
    public required string MessageType { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// The closed, versioned set of messages permitted across the Arena AdminPort
/// boundary. Keeping this list in contracts prevents a provider or caller from
/// inventing a transport command outside the GameScript allowlist.
/// </summary>
public static class ProtocolMessageTypes
{
    public const string Hello = "hello";
    public const string Capabilities = "capabilities";
    public const string Heartbeat = "heartbeat";
    public const string PauseRequest = "pause_request";
    public const string PauseResult = "pause_result";
    public const string ResumeRequest = "resume_request";
    public const string ResumeResult = "resume_result";
    public const string SnapshotRequest = "snapshot_request";
    public const string SnapshotResult = "snapshot_result";
    public const string ActionRequest = "action_request";
    public const string ActionProgress = "action_progress";
    public const string ActionResult = "action_result";
    public const string CameraRequest = "camera_request";
    public const string CameraResult = "camera_result";
    public const string CheckpointRequest = "checkpoint_request";
    public const string CheckpointResult = "checkpoint_result";
    public const string FinalizeRequest = "finalize_request";
    public const string FinalizeResult = "finalize_result";
    public const string Error = "error";
    public const string Chunk = "chunk";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Hello,
        Capabilities,
        Heartbeat,
        PauseRequest,
        PauseResult,
        ResumeRequest,
        ResumeResult,
        SnapshotRequest,
        SnapshotResult,
        ActionRequest,
        ActionProgress,
        ActionResult,
        CameraRequest,
        CameraResult,
        CheckpointRequest,
        CheckpointResult,
        FinalizeRequest,
        FinalizeResult,
        Error,
        Chunk,
    };

    public static IReadOnlySet<string> RetriableRequests { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Hello,
        Heartbeat,
        PauseRequest,
        ResumeRequest,
        SnapshotRequest,
        ActionRequest,
        CameraRequest,
        CheckpointRequest,
        FinalizeRequest,
        Chunk,
    };
}

public sealed record ProtocolValidationResult(
    bool IsValid,
    string? ErrorCode,
    string UserMessage)
{
    public static ProtocolValidationResult Valid { get; } = new(true, null, "Protocol envelope is valid.");
}

/// <summary>
/// Applies the same closed-envelope safety boundary used by the C# bridge and
/// represented by the Squirrel dispatcher fixtures. Payload semantics remain
/// message-specific, but every message is bounded before it crosses either
/// process boundary.
/// </summary>
public static partial class ProtocolEnvelopeValidator
{
    /// <summary>The maximum JSON envelope that can cross one AdminPort packet.</summary>
    public const int MaximumWireEnvelopeBytes = 8 * 1024;

    /// <summary>The maximum logical payload after bounded chunk reassembly.</summary>
    public const int MaximumLogicalPayloadBytes = 12 * 1024;
    public const int MaximumPayloadProperties = 64;
    public const int MaximumPayloadDepth = 16;
    public const int MaximumPayloadStringLength = 12 * 1024;
    public const int MaximumPayloadArrayLength = 128;

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "protocol_version",
        "message_type",
        "run_id",
        "message_id",
        "correlation_id",
        "idempotency_key",
        "payload",
    };

    public static ProtocolValidationResult Validate(ProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!string.Equals(envelope.ProtocolVersion, ContractVersions.ProtocolV1, StringComparison.Ordinal))
        {
            return Invalid(ArenaErrorCodes.ProtocolVersionMismatch, "The protocol version is not supported by this Arena bridge.");
        }

        if (!ProtocolMessageTypes.All.Contains(envelope.MessageType))
        {
            return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol message type is not allowlisted.");
        }

        if (!IsIdentifier(envelope.RunId) ||
            !IsIdentifier(envelope.MessageId) ||
            !IsIdentifier(envelope.CorrelationId) ||
            (envelope.IdempotencyKey is not null && !IsIdentifier(envelope.IdempotencyKey)))
        {
            return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol envelope contains an invalid identifier.");
        }

        if (ProtocolMessageTypes.RetriableRequests.Contains(envelope.MessageType) &&
            !IsIdentifier(envelope.IdempotencyKey))
        {
            return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "Retriable protocol requests require an idempotency key.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol payload must be an object.");
        }

        if (!IsPayloadWithinLimits(envelope.Payload, 0) ||
            Encoding.UTF8.GetByteCount(envelope.Payload.GetRawText()) > MaximumLogicalPayloadBytes)
        {
            return Invalid(ArenaErrorCodes.ProtocolMessageTooLarge, "The protocol payload exceeds the supported safety limits.");
        }

        return ProtocolValidationResult.Valid;
    }

    public static ProtocolValidationResult TryParse(
        ReadOnlySpan<byte> utf8Json,
        out ProtocolEnvelope? envelope)
    {
        envelope = null;
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumWireEnvelopeBytes)
        {
            return Invalid(ArenaErrorCodes.ProtocolMessageTooLarge, "The protocol envelope exceeds the supported size limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol envelope must be a JSON object.");
            }

            Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!KnownFields.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value))
                {
                    return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol envelope contains an unknown or duplicate field.");
                }
            }

            string[] required = ["protocol_version", "message_type", "run_id", "message_id", "correlation_id", "payload"];
            if (required.Any(field => !fields.ContainsKey(field)) ||
                required.Take(5).Any(field => fields[field].ValueKind != JsonValueKind.String) ||
                fields["payload"].ValueKind != JsonValueKind.Object ||
                (fields.TryGetValue("idempotency_key", out JsonElement idempotency) && idempotency.ValueKind != JsonValueKind.String))
            {
                return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol envelope has missing or incorrectly typed required fields.");
            }

            envelope = new ProtocolEnvelope
            {
                ProtocolVersion = fields["protocol_version"].GetString()!,
                MessageType = fields["message_type"].GetString()!,
                RunId = fields["run_id"].GetString()!,
                MessageId = fields["message_id"].GetString()!,
                CorrelationId = fields["correlation_id"].GetString()!,
                IdempotencyKey = fields.TryGetValue("idempotency_key", out JsonElement key) ? key.GetString() : null,
                Payload = fields["payload"].Clone(),
            };
            ProtocolValidationResult result = Validate(envelope);
            if (!result.IsValid)
            {
                envelope = null;
            }

            return result;
        }
        catch (JsonException)
        {
            return Invalid(ArenaErrorCodes.ProtocolInvalidMessage, "The protocol envelope is not valid JSON.");
        }
    }

    public static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } && IdentifierPattern().IsMatch(value);

    private static ProtocolValidationResult Invalid(string code, string message) => new(false, code, message);

    private static bool IsPayloadWithinLimits(JsonElement value, int depth)
    {
        if (depth > MaximumPayloadDepth)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Count() <= MaximumPayloadProperties &&
                value.EnumerateObject().All(property =>
                    property.Name.Length <= 128 &&
                    IsPayloadWithinLimits(property.Value, depth + 1)),
            JsonValueKind.Array => value.GetArrayLength() <= MaximumPayloadArrayLength &&
                value.EnumerateArray().All(item => IsPayloadWithinLimits(item, depth + 1)),
            JsonValueKind.String => value.GetString() is { Length: <= MaximumPayloadStringLength },
            JsonValueKind.Number => value.TryGetInt64(out _),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

public sealed record ActionRequest
{
    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("idempotency_key")]
    public required string IdempotencyKey { get; init; }

    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("arguments")]
    public required JsonElement Arguments { get; init; }

    [JsonPropertyName("constraint_context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioActionConstraintContext? ConstraintContext { get; init; }
}

public sealed record ActionResult
{
    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("correlation_id")]
    public required string CorrelationId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}
