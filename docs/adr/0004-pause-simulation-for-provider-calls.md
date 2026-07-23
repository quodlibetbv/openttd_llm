# ADR 0004: Pause simulation for provider calls and corrective retries

- Status: Accepted
- Date: 2026-07-23

## Context

Remote providers have unequal latency. Letting game time advance while a provider responds would make network conditions part of gameplay performance and undermine fair comparisons.

## Decision

The orchestrator pauses simulation before requesting an authoritative observation for a model call. Simulation remains paused through provider request, timeout/retry handling, output validation, authorization, and transition to a safe action-execution boundary. Provider latency and estimated cost are recorded as separate metrics unless a future scenario explicitly includes them.

## Consequences

- Provider and replay adapters must support cancellation and bounded calls.
- Observation, action, overlay, and event records must preserve the pause/correlation context.
- A stalled provider becomes a classified reliability event, never extra in-game thinking time.

## Migration impact

Changing pause semantics changes benchmark behavior and requires a new scenario/tool/protocol compatibility decision before publication.
