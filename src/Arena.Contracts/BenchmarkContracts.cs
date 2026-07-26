using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

public sealed record BenchmarkGoal
{
    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("allowed_tools")]
    public required IReadOnlyList<string> AllowedTools { get; init; }
}

public sealed record ScoreComponent
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("units")]
    public required string Units { get; init; }

    [JsonPropertyName("value")]
    public required decimal Value { get; init; }

    [JsonPropertyName("baseline")]
    public required decimal Baseline { get; init; }

    [JsonPropertyName("cap")]
    public required decimal Cap { get; init; }

    [JsonPropertyName("normalization")]
    public required string Normalization { get; init; }

    [JsonPropertyName("normalized_value")]
    public required decimal NormalizedValue { get; init; }

    [JsonPropertyName("weight")]
    public required decimal Weight { get; init; }

    [JsonPropertyName("contribution")]
    public required decimal Contribution { get; init; }

    [JsonPropertyName("missing_data_behavior")]
    public required string MissingDataBehavior { get; init; }

    [JsonPropertyName("penalty_interaction")]
    public required string PenaltyInteraction { get; init; }
}

public sealed record ScoreResult
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("scenario_id")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("scenario_version")]
    public required string ScenarioVersion { get; init; }

    [JsonPropertyName("formula_id")]
    public required string FormulaId { get; init; }

    [JsonPropertyName("final_metrics_sha256")]
    public required string FinalMetricsSha256 { get; init; }

    [JsonPropertyName("total_score")]
    public required decimal TotalScore { get; init; }

    [JsonPropertyName("total_penalty")]
    public required decimal TotalPenalty { get; init; }

    [JsonPropertyName("components")]
    public required IReadOnlyList<ScoreComponent> Components { get; init; }
}
