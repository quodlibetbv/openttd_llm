using System.Text.Json;
using System.Text.Json.Serialization;

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
