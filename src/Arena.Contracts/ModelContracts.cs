using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

public sealed record ModelRequest
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("observation_hash")]
    public required string ObservationHash { get; init; }

    /// <summary>
    /// A stable replay fixture fingerprint. It is derived from the same public
    /// observation after replacing the per-run identifier and volatile game
    /// clock fields with fixed values, so checked-in replay fixtures do not
    /// depend on generated run names or bounded startup timing. The exact
    /// observation hash remains <see cref="ObservationHash"/>.
    /// </summary>
    [JsonPropertyName("replay_observation_hash")]
    public string? ReplayObservationHash { get; init; }

    [JsonPropertyName("observation")]
    public required JsonElement Observation { get; init; }

    [JsonPropertyName("available_tools")]
    public required IReadOnlyList<string> AvailableTools { get; init; }

    [JsonPropertyName("remaining_model_calls")]
    public required int RemainingModelCalls { get; init; }

    [JsonPropertyName("remaining_output_tokens")]
    public required int RemainingOutputTokens { get; init; }

    [JsonPropertyName("maximum_actions")]
    public int MaximumActions { get; init; } = 8;

    [JsonPropertyName("prompt_template_version")]
    public string PromptTemplateVersion { get; init; } = "1.0";

    [JsonPropertyName("prompt_template_sha256")]
    public string? PromptTemplateSha256 { get; init; }

    [JsonPropertyName("schema_correction_attempt")]
    public int SchemaCorrectionAttempt { get; init; }
}

public sealed record ModelDecision
{
    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("public_summary")]
    public required string PublicSummary { get; init; }

    [JsonPropertyName("observations")]
    public required IReadOnlyList<string> Observations { get; init; }

    [JsonPropertyName("actions")]
    public required IReadOnlyList<ModelAction> Actions { get; init; }

    [JsonPropertyName("next_review_game_days")]
    public required int NextReviewGameDays { get; init; }
}

public sealed record ModelAction
{
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("arguments")]
    public required JsonElement Arguments { get; init; }
}
