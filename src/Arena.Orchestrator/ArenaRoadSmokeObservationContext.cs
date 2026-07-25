using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Provider-neutral one-decision context for the real Phase 06 road proof.
/// Replay and live providers receive the same goal, bounded observation, and
/// common tool allowlist; only their adapter differs.
/// </summary>
public static class ArenaRoadSmokeObservationContext
{
    public static ObservationBuildContext Create(string runId) => new(
        RunId: runId,
        ScenarioId: "phase-06-road-smoke",
        ScenarioVersion: "1.0.0",
        GoalId: "open-profitable-passenger-route",
        GoalVersion: "1.0.0",
        GoalTitle: "Open one profitable passenger road route",
        GoalObjective: "In this one available decision, select a nearby authoritative passenger opportunity and issue exactly one build_transport_route action. Use the bounded maximum_budget from the observation; do not issue inspection or wait actions.",
        AllowedTools: [RoadToolCatalog.BuildTransportRoute],
        MinimumCashReserve: 10_000,
        PerProjectBudget: 100_000,
        RemainingModelCalls: 1,
        RemainingOutputTokens: 1_000,
        RemainingRetries: 1,
        PriorDecisionResults: [],
        ReductionPolicy: ObservationReductionPolicy.Default);
}
