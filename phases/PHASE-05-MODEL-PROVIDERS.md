# Phase 05 — Model Providers

## Objective

Implement a provider-neutral decision loop with strict structured output, beginning with replay and DeepSeek adapters.

## Goals

- Keep benchmark semantics identical across providers.
- Resolve secrets safely and record usage without exposing credentials.
- Validate provider output before any game action.
- Make paid provider calls unnecessary for automated tests.

## Deliverables

1. `IModelProvider` contract and provider registry.
2. Replay provider driven by recorded decisions.
3. DeepSeek adapter using the common request and decision contracts.
4. Prompt-template versioning and hashing.
5. JSON Schema validation with one scenario-configurable corrective retry.
6. Timeout, cancellation, retry, rate-limit, and provider-error classification.
7. Usage records for latency, input/output tokens, estimated cost where configured, and provider request ID.
8. Sanitized provider-response contract fixtures.
9. CLI commands to list and test provider configurations.

## Decision-loop behavior

1. Pause the simulation.
2. Request and persist the authoritative observation.
3. Build the common model request.
4. Call the configured provider under its budget and timeout.
5. Parse and validate the response.
6. On eligible invalid output, send one concise schema-correction request.
7. Convert valid actions into authorization requests.
8. Persist the decision and usage metadata.
9. Resume only after action handling reaches a safe boundary.

## Public explanation rules

- Request a concise summary intended for publication.
- Cap summary and bullet lengths.
- Do not ask for hidden reasoning.
- Do not expose raw prompts or provider-private metadata in the overlay.
- Sanitize control characters and markup.

## Acceptance criteria

- Replay and DeepSeek produce the same `ModelDecision` type.
- Automated tests make no paid network calls.
- Invalid JSON, schema mismatch, timeout, authentication failure, rate limit, and cancellation are classified separately.
- Credentials are absent from logs, crash diagnostics, manifests, and process arguments.
- Simulation remains paused for the entire provider-call and validation interval.
- Provider-specific optional features do not change the common tool set.

## Out of scope

- Road construction tools.
- Final scoring.
- Video recording.

## Exit condition

Phase 05 is complete when a real DeepSeek decision and a replayed decision can be substituted without changing any downstream orchestrator, GameScript, scoring, overlay, or camera code.
