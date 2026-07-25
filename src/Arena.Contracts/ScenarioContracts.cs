using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// Immutable, versioned benchmark semantics. A scenario is deliberately data,
/// not a provider-specific prompt: the same instance drives observations,
/// action authorization, scoring, manifests, and replay.
/// </summary>
public sealed record BenchmarkScenario
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("scenario_id")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("world")]
    public required ScenarioWorld World { get; init; }

    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("allowed_tools")]
    public required IReadOnlyList<string> AllowedTools { get; init; }

    [JsonPropertyName("constraints")]
    public required ScenarioConstraints Constraints { get; init; }

    [JsonPropertyName("model_budget")]
    public required ScenarioModelBudget ModelBudget { get; init; }

    [JsonPropertyName("objectives")]
    public required IReadOnlyList<ScenarioObjective> Objectives { get; init; }

    [JsonPropertyName("penalties")]
    public required IReadOnlyList<ScenarioPenalty> Penalties { get; init; }

    [JsonPropertyName("end_condition")]
    public required ScenarioEndCondition EndCondition { get; init; }

    [JsonPropertyName("scoring")]
    public required ScenarioScoringDefinition Scoring { get; init; }

    [JsonPropertyName("observation")]
    public required ScenarioObservationPolicy Observation { get; init; }

    [JsonPropertyName("camera_relevance_hints")]
    public required IReadOnlyList<string> CameraRelevanceHints { get; init; }

    [JsonPropertyName("replay_tolerances")]
    public required ReplayMetricTolerances ReplayTolerances { get; init; }
}

public sealed record ScenarioWorld
{
    [JsonPropertyName("starting_save_id")]
    public required string StartingSaveId { get; init; }

    [JsonPropertyName("content_manifest_id")]
    public required string ContentManifestId { get; init; }

    [JsonPropertyName("game_settings_id")]
    public required string GameSettingsId { get; init; }

    [JsonPropertyName("start_date")]
    public required string StartDate { get; init; }
}

public sealed record ScenarioConstraints
{
    [JsonPropertyName("minimum_cash_reserve")]
    public required long MinimumCashReserve { get; init; }

    [JsonPropertyName("per_project_budget")]
    public required long PerProjectBudget { get; init; }

    [JsonPropertyName("maximum_active_projects")]
    public required int MaximumActiveProjects { get; init; }

    [JsonPropertyName("allowed_modes")]
    public required IReadOnlyList<string> AllowedModes { get; init; }

    [JsonPropertyName("allowed_cargo")]
    public required IReadOnlyList<string> AllowedCargo { get; init; }
}

public sealed record ScenarioModelBudget
{
    [JsonPropertyName("maximum_calls")]
    public required int MaximumCalls { get; init; }

    [JsonPropertyName("maximum_output_tokens")]
    public required int MaximumOutputTokens { get; init; }

    [JsonPropertyName("maximum_retries")]
    public required int MaximumRetries { get; init; }
}

public sealed record ScenarioObjective
{
    [JsonPropertyName("objective_id")]
    public required string ObjectiveId { get; init; }

    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("minimum")]
    public required long Minimum { get; init; }
}

public sealed record ScenarioPenalty
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("points")]
    public required decimal Points { get; init; }
}

public sealed record ScenarioEndCondition
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed record ScenarioScoringDefinition
{
    [JsonPropertyName("score_schema_version")]
    public required string ScoreSchemaVersion { get; init; }

    [JsonPropertyName("formula_id")]
    public required string FormulaId { get; init; }

    [JsonPropertyName("components")]
    public required IReadOnlyList<ScenarioScoreComponentDefinition> Components { get; init; }
}

public sealed record ScenarioScoreComponentDefinition
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("metric")]
    public required string Metric { get; init; }

    [JsonPropertyName("units")]
    public required string Units { get; init; }

    [JsonPropertyName("baseline")]
    public required decimal Baseline { get; init; }

    [JsonPropertyName("cap")]
    public required decimal Cap { get; init; }

    [JsonPropertyName("weight")]
    public required decimal Weight { get; init; }

    [JsonPropertyName("missing_data_behavior")]
    public required string MissingDataBehavior { get; init; }

    [JsonPropertyName("penalty_interaction")]
    public required string PenaltyInteraction { get; init; }
}

public sealed record ScenarioObservationPolicy
{
    [JsonPropertyName("ranking_rule")]
    public required string RankingRule { get; init; }

    [JsonPropertyName("maximum_canonical_bytes")]
    public required int MaximumCanonicalBytes { get; init; }

    [JsonPropertyName("maximum_estimated_tokens")]
    public required int MaximumEstimatedTokens { get; init; }
}

/// <summary>
/// Explicit engine tolerance limits used only by accepted-action replay
/// comparison. A zero means that metric must reproduce exactly.
/// </summary>
public sealed record ReplayMetricTolerances
{
    [JsonPropertyName("cash")]
    public required long Cash { get; init; }

    [JsonPropertyName("operating_profit")]
    public required long OperatingProfit { get; init; }

    [JsonPropertyName("company_value")]
    public required long CompanyValue { get; init; }

    [JsonPropertyName("cargo_delivered")]
    public required long CargoDelivered { get; init; }

    [JsonPropertyName("active_vehicle_count")]
    public required long ActiveVehicleCount { get; init; }

    [JsonPropertyName("operational_route_count")]
    public required long OperationalRouteCount { get; init; }

    [JsonPropertyName("infrastructure_investment")]
    public required long InfrastructureInvestment { get; init; }
}

/// <summary>
/// Trusted scenario metadata attached by the orchestrator to an action
/// envelope. It is never provider supplied and lets ArenaGS enforce the cash
/// reserve and project ceiling during native execution as well as before it.
/// </summary>
public sealed record ScenarioActionConstraintContext
{
    [JsonPropertyName("scenario_id")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("scenario_version")]
    public required string ScenarioVersion { get; init; }

    [JsonPropertyName("scenario_sha256")]
    public required string ScenarioSha256 { get; init; }

    [JsonPropertyName("minimum_cash_reserve")]
    public required long MinimumCashReserve { get; init; }

    [JsonPropertyName("per_project_budget")]
    public required long PerProjectBudget { get; init; }

    [JsonPropertyName("maximum_active_projects")]
    public required int MaximumActiveProjects { get; init; }

    [JsonPropertyName("allowed_modes")]
    public required IReadOnlyList<string> AllowedModes { get; init; }

    [JsonPropertyName("allowed_cargo")]
    public required IReadOnlyList<string> AllowedCargo { get; init; }

    [JsonPropertyName("allowed_tools")]
    public required IReadOnlyList<string> AllowedTools { get; init; }
}

/// <summary>
/// A periodically captured, authoritative game-side metric vector. All
/// values are normalized to stable units before scoring or replay comparison.
/// </summary>
public sealed record BenchmarkMetrics
{
    [JsonPropertyName("cash")]
    public required long Cash { get; init; }

    [JsonPropertyName("loan")]
    public required long Loan { get; init; }

    [JsonPropertyName("quarterly_income")]
    public required long QuarterlyIncome { get; init; }

    [JsonPropertyName("quarterly_expenses")]
    public required long QuarterlyExpenses { get; init; }

    [JsonPropertyName("operating_profit")]
    public required long OperatingProfit { get; init; }

    [JsonPropertyName("company_value")]
    public required long CompanyValue { get; init; }

    [JsonPropertyName("quarterly_cargo_delivered")]
    public required long QuarterlyCargoDelivered { get; init; }

    [JsonPropertyName("active_vehicle_count")]
    public required int ActiveVehicleCount { get; init; }

    [JsonPropertyName("operational_route_count")]
    public required int OperationalRouteCount { get; init; }

    [JsonPropertyName("completed_project_count")]
    public required int CompletedProjectCount { get; init; }

    [JsonPropertyName("infrastructure_investment")]
    public required long InfrastructureInvestment { get; init; }

    [JsonPropertyName("invalid_decision_count")]
    public required int InvalidDecisionCount { get; init; }

    [JsonPropertyName("constraint_violation_count")]
    public required int ConstraintViolationCount { get; init; }
}

public sealed record BenchmarkMetricSnapshot
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("sample_id")]
    public required string SampleId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("game_date")]
    public required string GameDate { get; init; }

    [JsonPropertyName("game_tick")]
    public required long GameTick { get; init; }

    [JsonPropertyName("metrics")]
    public required BenchmarkMetrics Metrics { get; init; }
}
