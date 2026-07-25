using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Versioned, provider-neutral context used by the fixed local smoke map. It
/// intentionally contains no host paths, credential references, or executor
/// internals, so the same public observation can drive replay and live adapters.
/// </summary>
public static class ArenaSmokeObservationContext
{
    public static ObservationBuildContext Create(string runId) => new(
        RunId: runId,
        ScenarioId: "phase-04-observation-smoke",
        ScenarioVersion: "1.0.0",
        GoalId: "understand-company-state",
        GoalVersion: "1.0.0",
        GoalTitle: "Understand the current company state",
        GoalObjective: "Read the bounded public company, network, opportunity, and budget summaries without changing the game.",
        AllowedTools: RoadToolCatalog.All,
        MinimumCashReserve: 10_000,
        PerProjectBudget: 100_000,
        RemainingModelCalls: 1,
        RemainingOutputTokens: 1_000,
        RemainingRetries: 1,
        PriorDecisionResults: [],
        ReductionPolicy: ObservationReductionPolicy.Default);
}
