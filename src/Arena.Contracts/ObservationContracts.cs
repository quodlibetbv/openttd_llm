using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

public sealed record TileCoordinate
{
    [JsonPropertyName("x")]
    public required int X { get; init; }

    [JsonPropertyName("y")]
    public required int Y { get; init; }
}

public sealed record GameCompanyState
{
    [JsonPropertyName("company_id")]
    public required int CompanyId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("cash")]
    public required long Cash { get; init; }

    [JsonPropertyName("loan")]
    public required long Loan { get; init; }

    [JsonPropertyName("quarterly_income")]
    public required long QuarterlyIncome { get; init; }

    [JsonPropertyName("quarterly_expenses")]
    public required long QuarterlyExpenses { get; init; }

    [JsonPropertyName("quarterly_cargo_delivered")]
    public required long QuarterlyCargoDelivered { get; init; }

    [JsonPropertyName("company_value")]
    public required long CompanyValue { get; init; }

    [JsonPropertyName("performance_rating")]
    public required int PerformanceRating { get; init; }
}

public sealed record GameTownState
{
    [JsonPropertyName("town_id")]
    public required int TownId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("population")]
    public required int Population { get; init; }

    [JsonPropertyName("location")]
    public required TileCoordinate Location { get; init; }
}

public sealed record GameIndustryState
{
    [JsonPropertyName("industry_id")]
    public required int IndustryId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("location")]
    public required TileCoordinate Location { get; init; }
}

public sealed record GameStationState
{
    [JsonPropertyName("station_id")]
    public required int StationId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("location")]
    public required TileCoordinate Location { get; init; }

    [JsonPropertyName("vehicle_count")]
    public required int VehicleCount { get; init; }
}

public sealed record GameVehicleState
{
    [JsonPropertyName("vehicle_id")]
    public required int VehicleId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("vehicle_type")]
    public required string VehicleType { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("profit_last_year")]
    public required long ProfitLastYear { get; init; }

    [JsonPropertyName("location")]
    public required TileCoordinate Location { get; init; }
}

public sealed record GameRouteState
{
    [JsonPropertyName("route_id")]
    public required string RouteId { get; init; }

    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("source_station_id")]
    public required int SourceStationId { get; init; }

    [JsonPropertyName("destination_station_id")]
    public required int DestinationStationId { get; init; }

    [JsonPropertyName("cargo")]
    public required string Cargo { get; init; }

    [JsonPropertyName("vehicle_ids")]
    public required IReadOnlyList<int> VehicleIds { get; init; }

    [JsonPropertyName("operational")]
    public required bool Operational { get; init; }
}

public sealed record GameProjectState
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("spent")]
    public required long Spent { get; init; }

    [JsonPropertyName("maximum_budget")]
    public required long MaximumBudget { get; init; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }
}

public sealed record NormalizedGameEvent
{
    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("event_code")]
    public required string EventCode { get; init; }

    [JsonPropertyName("game_date")]
    public required string GameDate { get; init; }

    [JsonPropertyName("entity_ids")]
    public required IReadOnlyList<string> EntityIds { get; init; }

    [JsonPropertyName("public_summary")]
    public required string PublicSummary { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}

/// <summary>
/// The bounded authoritative state emitted by ArenaGS. It is intentionally
/// provider-neutral: the orchestrator enriches it with goal and budget context
/// before it becomes an observation sent to a model.
/// </summary>
public sealed record GameScriptSnapshot
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("game_date")]
    public required string GameDate { get; init; }

    [JsonPropertyName("paused")]
    public required bool Paused { get; init; }

    [JsonPropertyName("game_tick")]
    public required long GameTick { get; init; }

    [JsonPropertyName("company")]
    public required GameCompanyState Company { get; init; }

    [JsonPropertyName("towns")]
    public required IReadOnlyList<GameTownState> Towns { get; init; }

    [JsonPropertyName("industries")]
    public required IReadOnlyList<GameIndustryState> Industries { get; init; }

    [JsonPropertyName("stations")]
    public required IReadOnlyList<GameStationState> Stations { get; init; }

    [JsonPropertyName("vehicles")]
    public required IReadOnlyList<GameVehicleState> Vehicles { get; init; }

    [JsonPropertyName("routes")]
    public required IReadOnlyList<GameRouteState> Routes { get; init; }

    [JsonPropertyName("projects")]
    public required IReadOnlyList<GameProjectState> Projects { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<NormalizedGameEvent> Events { get; init; }
}

public sealed record ObservationRunContext
{
    [JsonPropertyName("scenario_id")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("scenario_version")]
    public required string ScenarioVersion { get; init; }

    [JsonPropertyName("benchmark_company_id")]
    public required int BenchmarkCompanyId { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }
}

public sealed record ObservationGoalContext
{
    [JsonPropertyName("goal_id")]
    public required string GoalId { get; init; }

    [JsonPropertyName("goal_version")]
    public required string GoalVersion { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("allowed_tools")]
    public required IReadOnlyList<string> AllowedTools { get; init; }

    [JsonPropertyName("ranking_rule")]
    public required string RankingRule { get; init; }
}

public sealed record ObservationGameClock
{
    [JsonPropertyName("game_date")]
    public required string GameDate { get; init; }

    [JsonPropertyName("game_tick")]
    public required long GameTick { get; init; }

    [JsonPropertyName("paused")]
    public required bool Paused { get; init; }
}

public sealed record ObservationCompanySummary
{
    [JsonPropertyName("company_id")]
    public required int CompanyId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("vehicle_count")]
    public required int VehicleCount { get; init; }

    [JsonPropertyName("station_count")]
    public required int StationCount { get; init; }

    [JsonPropertyName("route_count")]
    public required int RouteCount { get; init; }
}

public sealed record ObservationFinancialSummary
{
    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("cash")]
    public required long Cash { get; init; }

    [JsonPropertyName("loan")]
    public required long Loan { get; init; }

    [JsonPropertyName("quarterly_income")]
    public required long QuarterlyIncome { get; init; }

    [JsonPropertyName("quarterly_expenses")]
    public required long QuarterlyExpenses { get; init; }

    [JsonPropertyName("quarterly_profit")]
    public required long QuarterlyProfit { get; init; }

    [JsonPropertyName("quarterly_cargo_delivered")]
    public required long QuarterlyCargoDelivered { get; init; }

    [JsonPropertyName("company_value")]
    public required long CompanyValue { get; init; }

    [JsonPropertyName("performance_rating")]
    public required int PerformanceRating { get; init; }
}

public sealed record ObservationRoute
{
    [JsonPropertyName("route_id")]
    public required string RouteId { get; init; }

    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("source_station_id")]
    public required int SourceStationId { get; init; }

    [JsonPropertyName("destination_station_id")]
    public required int DestinationStationId { get; init; }

    [JsonPropertyName("cargo")]
    public required string Cargo { get; init; }

    [JsonPropertyName("vehicle_ids")]
    public required IReadOnlyList<int> VehicleIds { get; init; }

    [JsonPropertyName("operational")]
    public required bool Operational { get; init; }
}

public sealed record ObservationNetworkSummary
{
    [JsonPropertyName("stations")]
    public required IReadOnlyList<GameStationState> Stations { get; init; }

    [JsonPropertyName("vehicles")]
    public required IReadOnlyList<GameVehicleState> Vehicles { get; init; }

    [JsonPropertyName("routes")]
    public required IReadOnlyList<ObservationRoute> Routes { get; init; }
}

public sealed record ObservationProject
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("spent")]
    public required long Spent { get; init; }

    [JsonPropertyName("maximum_budget")]
    public required long MaximumBudget { get; init; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }
}

public sealed record ObservationConstraintsAndBudgets
{
    [JsonPropertyName("minimum_cash_reserve")]
    public required long MinimumCashReserve { get; init; }

    [JsonPropertyName("per_project_budget")]
    public required long PerProjectBudget { get; init; }

    [JsonPropertyName("available_project_budget")]
    public required long AvailableProjectBudget { get; init; }
}

public sealed record ObservationOpportunity
{
    [JsonPropertyName("opportunity_id")]
    public required string OpportunityId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("source_town_id")]
    public required int SourceTownId { get; init; }

    [JsonPropertyName("source_town_name")]
    public required string SourceTownName { get; init; }

    [JsonPropertyName("destination_town_id")]
    public required int DestinationTownId { get; init; }

    [JsonPropertyName("destination_town_name")]
    public required string DestinationTownName { get; init; }

    [JsonPropertyName("cargo")]
    public required string Cargo { get; init; }

    [JsonPropertyName("distance_tiles")]
    public required int DistanceTiles { get; init; }

    [JsonPropertyName("ranking_score")]
    public required int RankingScore { get; init; }
}

public sealed record ObservationCandidateOpportunities
{
    [JsonPropertyName("towns")]
    public required IReadOnlyList<GameTownState> Towns { get; init; }

    [JsonPropertyName("industries")]
    public required IReadOnlyList<GameIndustryState> Industries { get; init; }

    [JsonPropertyName("opportunities")]
    public required IReadOnlyList<ObservationOpportunity> Opportunities { get; init; }
}

public sealed record ObservationDecisionResult
{
    [JsonPropertyName("decision_id")]
    public required string DecisionId { get; init; }

    [JsonPropertyName("action_id")]
    public required string ActionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record ObservationModelBudget
{
    [JsonPropertyName("remaining_calls")]
    public required int RemainingCalls { get; init; }

    [JsonPropertyName("remaining_output_tokens")]
    public required int RemainingOutputTokens { get; init; }

    [JsonPropertyName("remaining_retries")]
    public required int RemainingRetries { get; init; }
}

public sealed record ObservationSections
{
    [JsonPropertyName("run_context")]
    public required ObservationRunContext RunContext { get; init; }

    [JsonPropertyName("goal_context")]
    public required ObservationGoalContext GoalContext { get; init; }

    [JsonPropertyName("game_clock")]
    public required ObservationGameClock GameClock { get; init; }

    [JsonPropertyName("company_summary")]
    public required ObservationCompanySummary CompanySummary { get; init; }

    [JsonPropertyName("financial_summary")]
    public required ObservationFinancialSummary FinancialSummary { get; init; }

    [JsonPropertyName("network_summary")]
    public required ObservationNetworkSummary NetworkSummary { get; init; }

    [JsonPropertyName("active_projects")]
    public required IReadOnlyList<ObservationProject> ActiveProjects { get; init; }

    [JsonPropertyName("constraints_and_budgets")]
    public required ObservationConstraintsAndBudgets ConstraintsAndBudgets { get; init; }

    [JsonPropertyName("candidate_opportunities")]
    public required ObservationCandidateOpportunities CandidateOpportunities { get; init; }

    [JsonPropertyName("recent_events")]
    public required IReadOnlyList<NormalizedGameEvent> RecentEvents { get; init; }

    [JsonPropertyName("prior_decision_results")]
    public required IReadOnlyList<ObservationDecisionResult> PriorDecisionResults { get; init; }

    [JsonPropertyName("remaining_model_budget")]
    public required ObservationModelBudget RemainingModelBudget { get; init; }
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
    public required ObservationSections Sections { get; init; }
}
