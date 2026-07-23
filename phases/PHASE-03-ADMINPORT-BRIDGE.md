# Phase 03 — AdminPort Bridge

## Objective

Implement a secure, versioned, bidirectional protocol between the .NET orchestrator and ArenaGS over OpenTTD AdminPort.

## Goals

- Authenticate and connect locally without exposing a privileged unauthenticated port.
- Exchange typed messages with correlation and idempotency.
- Support payload chunking, checksums, timeouts, and retries.
- Detect protocol incompatibility before gameplay begins.

## Deliverables

1. AdminPort client with connection lifecycle, authentication, keepalive, reconnect policy, and bounded queues.
2. ArenaGS protocol dispatcher.
3. Versioned envelope and message schemas.
4. Chunking protocol for payloads above the safe AdminPort message size.
5. Heartbeat, capabilities handshake, and readiness messages.
6. Idempotent command ledger in the GameScript for retried requests.
7. Sanitized protocol capture fixtures and cross-language contract tests.
8. Error mapping from OpenTTD/AdminPort failures to Arena error codes.

## Required messages

```text
hello
capabilities
heartbeat
pause_request / pause_result
resume_request / resume_result
snapshot_request / snapshot_result
action_request / action_progress / action_result
camera_request / camera_result
checkpoint_request / checkpoint_result
finalize_request / finalize_result
error
```

## Security requirements

- Bind to loopback by default.
- Use the supported secure authentication mechanism.
- Keep AdminPort credentials separate from model and OBS credentials.
- Reject unknown clients, versions, message types, oversized fields, and stale run IDs.
- Apply per-message and per-reassembled-payload size limits.

## Acceptance criteria

- C# and Squirrel implementations pass the same valid and invalid protocol fixtures.
- A 10 KB logical payload can be chunked, transferred, verified, and reassembled.
- Duplicate idempotent commands return the original result without executing twice.
- Lost chunk, checksum mismatch, stale correlation, and timeout cases are deterministic and tested.
- Reconnect restores heartbeat and allows safe continuation or produces a classified terminal failure.
- An incompatible protocol version prevents the run from starting.

## Out of scope

- Rich game observations.
- Model providers.
- Route construction.

## Exit condition

Phase 03 is complete when a real OpenTTD instance can exchange authenticated, versioned commands and results under normal, duplicate, timeout, and reconnect conditions without double execution.
