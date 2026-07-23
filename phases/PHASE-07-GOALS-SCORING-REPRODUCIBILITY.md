# Phase 07 — Goals, Scoring, and Reproducibility

## Objective

Turn autonomous gameplay into a fair benchmark with immutable scenario definitions, deterministic scoring, comparable budgets, and verifiable run manifests.

## Goals

- Define goals as versioned data rather than prompt prose alone.
- Enforce constraints before and during action execution.
- Calculate scores deterministically from authoritative metrics.
- Reproduce accepted action sequences without calling a provider.

## Deliverables

1. Goal/scenario schema with world, constraints, model budget, objectives, penalties, end conditions, and camera relevance hints.
2. `road-profit-v1.yaml` and a short smoke variant.
3. Pure scoring engine with a detailed score breakdown.
4. Periodic metric snapshots and final authoritative metrics.
5. Immutable run manifest with hashes of all benchmark-defining inputs.
6. Run verifier and score recalculation command.
7. Accepted-action replay mode.
8. Scenario publication/versioning rules.
9. Statistical guidance for repeated nondeterministic provider runs.

## Fairness rules

- Freeze starting save, content manifest, game settings, observation schema, tool schema, prompt template, retry policy, and end condition.
- Pause simulation during provider calls.
- Apply identical model-call and output budgets.
- Record provider latency and cost separately.
- Do not let recording, camera, or overlay failure change score.
- Treat invalid decisions and constraint violations using declared penalties.

## Initial road-profit score

The first production formula should include normalized components for:

- Operating profit.
- Company value.
- Cargo or passengers delivered.
- Profit per active vehicle.
- Return on infrastructure investment.
- Solvency and completion.

Every component must define units, baseline, cap, normalization, missing-data behavior, and penalty interaction.

## Acceptance criteria

- Recalculating a score from persisted metrics produces exactly the stored result.
- Editing a published scenario changes its hash and is rejected unless the version changes.
- Replay of accepted actions against the same starting state produces equivalent normalized final metrics within documented engine tolerances.
- Constraint violations are blocked game-side and recorded orchestrator-side.
- Two providers receive byte-equivalent common requests for the same normalized observation and scenario.
- The run verifier detects altered logs, score files, savegames, and manifests.

## Out of scope

- OBS recording.
- Rail scoring.
- Tournament leaderboards.

## Exit condition

Phase 07 is complete when road-profit runs can be publicly compared using immutable evidence and independently recalculated scores.
