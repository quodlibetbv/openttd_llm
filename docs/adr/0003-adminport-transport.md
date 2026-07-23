# ADR 0003: Authenticated AdminPort is the game-control transport

- Status: Accepted
- Date: 2026-07-23

## Context

The orchestrator and authoritative GameScript require a local, typed, observable transport. The transport must prevent unknown clients, version confusion, oversized messages, and duplicate execution.

## Decision

Use OpenTTD AdminPort with its supported authentication mechanism, bound to loopback by default. Every message uses a versioned envelope containing protocol version, message type, run ID, message ID, and correlation ID. Retriable commands also use idempotency keys. Large logical payloads are chunked, checksummed, bounded, and reassembled with timeouts.

## Consequences

- Phase 03 owns transport implementation and cross-language contract tests.
- AdminPort credentials are separate from provider and OBS credentials.
- A protocol incompatibility blocks a run before gameplay begins.

## Migration impact

Replacing AdminPort requires a new protocol major, compatibility tests, and an explicit migration path for saved active runs.
