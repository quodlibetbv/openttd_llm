# ADR 0001: Native GameScript executor instead of screen automation

- Status: Accepted
- Date: 2026-07-23

## Context

Screen-coordinate automation is fragile, cannot prove what changed in game state, and would give a model an unsafe, untyped control surface. Benchmark outcomes must remain explainable and replayable from game-side evidence.

## Decision

Models select only allowlisted, typed high-level tools. `ArenaGS` is authoritative for observations and action execution, while `ModelProxyAI` remains inert except for its explicitly versioned ownership and heartbeat responsibilities. The orchestrator sends validated commands through the protocol boundary; it does not translate model intent into clicks, keystrokes, or screen coordinates.

## Consequences

- Every action must have a versioned request/result contract, stable error code, idempotency behavior, and GameScript-side constraint checks.
- Camera and overlay systems may observe events but cannot alter company state.
- Phase 00 establishes inert package entry points; execution begins only after the AdminPort and action contracts are implemented.

## Migration impact

Any prototype using desktop automation must be retired rather than adapted into a competitive benchmark. New game-side capabilities require a GameScript and contract version decision.
