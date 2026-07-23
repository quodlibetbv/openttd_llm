# Phase 04 — Observation Model

## Objective

Expose stable, authoritative, provider-neutral game observations and normalized events suitable for strategic model decisions, scoring, overlays, and camera direction.

## Goals

- Represent the game using entity IDs and bounded structured summaries.
- Separate full snapshots from high-frequency events and deltas.
- Rank opportunities without embedding goal-specific hidden advantages.
- Keep observations within declared size and token budgets.

## Deliverables

1. `observation.v1` schema and generated C# contract types.
2. ArenaGS queries for company, economy, towns, industries, stations, vehicles, routes, active projects, and constraints.
3. Normalized event catalog with stable event codes.
4. Snapshot builder with configurable limits and deterministic ordering.
5. Observation reducer that selects relevant entities using scenario-declared ranking rules.
6. NDJSON observation and event writers.
7. Snapshot/delta consistency tests across save and load.
8. Synthetic fixtures for provider and overlay development.

## Observation sections

```text
run_context
goal_context
game_clock
company_summary
financial_summary
network_summary
active_projects
constraints_and_budgets
candidate_opportunities
recent_events
prior_decision_results
remaining_model_budget
```

## Design rules

- Use OpenTTD entity IDs, never screen coordinates.
- Include units and currency semantics explicitly.
- Limit arrays and strings through scenario or platform configuration.
- Order equivalent entities deterministically.
- Do not expose executor-internal search state as a provider advantage.
- Record the exact observation sent to each provider.
- Allow the overlay and camera to consume richer event streams than the provider receives.

## Acceptance criteria

- The same game state produces byte-stable canonical JSON after normalization.
- Snapshot size remains within configured limits on the largest certified benchmark map.
- Entity references in actions can be validated against the latest snapshot.
- Save/load does not change stable IDs or produce duplicate synthetic events.
- Observation generation completes within the GameScript execution budget.
- Fixtures cover early game, profitable company, debt stress, congestion, and bankruptcy risk.

## Out of scope

- Provider prompts.
- Action execution.
- Scoring formulas.

## Exit condition

Phase 04 is complete when a provider-independent replay tool can read recorded observations and a human can understand the company’s state without inspecting the OpenTTD screen.
