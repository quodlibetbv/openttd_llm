using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Provider-neutral context for the bounded fleet-expansion proof after a
/// route is already operational. It intentionally exposes only the one
/// action that the proof is authorised to perform.
/// </summary>
public static class ArenaFleetSmokeObservationContext
{
    public static ObservationBuildContext Create(string runId) => new(
        RunId: runId,
        ScenarioId: "phase-06-road-smoke",
        ScenarioVersion: "1.0.0",
        GoalId: "expand-operational-passenger-route",
        GoalVersion: "1.0.0",
        GoalTitle: "Expand one operational passenger road route",
        GoalObjective: "Select the existing authoritative road route and issue exactly one expand_route action with a bounded target fleet and maximum_budget.",
        AllowedTools: [RoadToolCatalog.ExpandRoute],
        MinimumCashReserve: 10_000,
        PerProjectBudget: 100_000,
        RemainingModelCalls: 1,
        RemainingOutputTokens: 1_000,
        RemainingRetries: 1,
        PriorDecisionResults: [],
        ReductionPolicy: ObservationReductionPolicy.Default);
}
