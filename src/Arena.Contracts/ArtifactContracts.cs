using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

public sealed record RecordedObservation
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("observation_sha256")]
    public required string ObservationSha256 { get; init; }

    [JsonPropertyName("replay_observation_sha256")]
    public required string ReplayObservationSha256 { get; init; }

    [JsonPropertyName("observation")]
    public required ObservationSnapshot Observation { get; init; }
}

public sealed record RecordedGameEvent
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("event")]
    public required NormalizedGameEvent Event { get; init; }
}

public sealed record ProviderUsageRecord
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("input_tokens")]
    public required long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public required long OutputTokens { get; init; }

    [JsonPropertyName("latency_ms")]
    public required long LatencyMilliseconds { get; init; }

    [JsonPropertyName("provider_request_id")]
    public string? ProviderRequestId { get; init; }

    [JsonPropertyName("estimated_cost")]
    public decimal? EstimatedCost { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }
}

public sealed record RecordedDecision
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("observation_sha256")]
    public required string ObservationSha256 { get; init; }

    [JsonPropertyName("prompt_template_version")]
    public required string PromptTemplateVersion { get; init; }

    [JsonPropertyName("prompt_template_sha256")]
    public required string PromptTemplateSha256 { get; init; }

    [JsonPropertyName("decision")]
    public required ModelDecision Decision { get; init; }
}

public sealed record RecordedAction
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("request")]
    public required ActionRequest Request { get; init; }

    [JsonPropertyName("result")]
    public required ActionResult Result { get; init; }
}
