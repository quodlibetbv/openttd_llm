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

    [JsonPropertyName("value")]
    public required decimal Value { get; init; }

    [JsonPropertyName("weight")]
    public required decimal Weight { get; init; }

    [JsonPropertyName("contribution")]
    public required decimal Contribution { get; init; }
}

public sealed record ScoreResult
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("total_score")]
    public required decimal TotalScore { get; init; }

    [JsonPropertyName("components")]
    public required IReadOnlyList<ScoreComponent> Components { get; init; }
}
