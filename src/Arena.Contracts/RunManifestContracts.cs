using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

public sealed record BenchmarkInputHashes
{
    [JsonPropertyName("starting_save_sha256")]
    public required string StartingSaveSha256 { get; init; }

    [JsonPropertyName("content_manifest_sha256")]
    public required string ContentManifestSha256 { get; init; }

    [JsonPropertyName("scenario_sha256")]
    public required string ScenarioSha256 { get; init; }

    [JsonPropertyName("game_settings_sha256")]
    public required string GameSettingsSha256 { get; init; }

    [JsonPropertyName("prompt_template_sha256")]
    public required string PromptTemplateSha256 { get; init; }

    [JsonPropertyName("tool_contract_sha256")]
    public required string ToolContractSha256 { get; init; }

    [JsonPropertyName("observation_schema_sha256")]
    public required string ObservationSchemaSha256 { get; init; }

    [JsonPropertyName("action_schema_sha256")]
    public required string ActionSchemaSha256 { get; init; }

    [JsonPropertyName("score_schema_sha256")]
    public required string ScoreSchemaSha256 { get; init; }

    [JsonPropertyName("protocol_schema_sha256")]
    public required string ProtocolSchemaSha256 { get; init; }

    [JsonPropertyName("retry_policy_sha256")]
    public required string RetryPolicySha256 { get; init; }

    [JsonPropertyName("end_condition_sha256")]
    public required string EndConditionSha256 { get; init; }
}

public sealed record ContractVersionsUsed
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; init; }

    [JsonPropertyName("observation")]
    public required string Observation { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    [JsonPropertyName("score")]
    public required string Score { get; init; }

    [JsonPropertyName("manifest")]
    public required string Manifest { get; init; }
}

public sealed record RunManifest
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    [JsonPropertyName("application_version")]
    public required string ApplicationVersion { get; init; }

    [JsonPropertyName("git_commit")]
    public required string GitCommit { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("contract_versions")]
    public required ContractVersionsUsed ContractVersions { get; init; }

    [JsonPropertyName("benchmark_input_hashes")]
    public required BenchmarkInputHashes BenchmarkInputHashes { get; init; }
}
