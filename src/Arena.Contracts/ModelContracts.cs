using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

public sealed record ModelRequest
{
    public required string RunId { get; init; }

    public required string DecisionId { get; init; }

    public required string ObservationHash { get; init; }

    public required JsonElement Observation { get; init; }

    public required IReadOnlyList<string> AvailableTools { get; init; }

    public required int RemainingModelCalls { get; init; }

    public required int RemainingOutputTokens { get; init; }
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

public sealed record ObservationSnapshot
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("game_date")]
    public required string GameDate { get; init; }

    [JsonPropertyName("sections")]
    public required JsonElement Sections { get; init; }
}
