# ADR 0007: Immutable benchmark inputs and pure scoring

- Status: Accepted
- Date: 2026-07-25

## Context

Phase 07 turns a working road executor into a comparable benchmark. A score is not credible if its scenario, starting state, prompt, retry policy, tool surface, or final evidence can change after a provider has acted. Replaying a provider response also risks accidentally treating latency, recording state, or new provider output as gameplay.

## Decision

- A benchmark scenario is a closed, versioned YAML contract. Its published `scenario_id` and semantic version map to an immutable SHA-256 fingerprint in `scenarios/published-scenarios.v1.json`.
- A comparable run snapshots the starting save, content manifest, generated settings, scenario and schema, observation/action/score/protocol schemas, prompt template, typed tool contract, retry policy, and end condition before the provider call. The final manifest hashes every captured input and evidence artifact.
- `RoadProfitScoreCalculator` is pure. It accepts only the sealed scenario plus periodic and final authoritative GameScript metric snapshots. Provider latency, token cost, OBS, overlay, and camera state are excluded from the score formula.
- The orchestrator validates scenario constraints before dispatch and attaches the same trusted constraint context to the typed action request. ArenaGS independently enforces it during native action acceptance and execution.
- Accepted-action replay starts from a matching starting-save fingerprint, replays only source actions whose recorded results were accepted, never creates a provider, and compares the declared final metric vector against scenario-owned tolerances.

## Consequences

- `benchmark-smoke`, `benchmark run`, `verify-run`, `score recalculate`, `actions replay`, and `road-constraint-smoke` are explicit evidence commands rather than informal operator procedures.
- Changing any published scenario byte, score semantics, input fingerprint, observation/tool contract, prompt, retry policy, or end condition requires a new scenario version and new published hash.
- Recordings and visual presentation may fail independently in future phases, but cannot affect a Phase 07 score.
- Archived sealed runs remain independently verifiable without OpenTTD or a provider credential. Accepted-action replay still needs a prepared local OpenTTD runtime because it executes the authoritative GameScript path anew.

## Migration impact

Existing Phase 00–06 bridge runs are not retroactively benchmarked: they do not contain a Phase 07 manifest, metric stream, immutable scenario snapshot, or score artifact. They remain valid evidence for their own phase contracts. New comparable results must use a published Phase 07 scenario and retain the sealed run directory unchanged.
