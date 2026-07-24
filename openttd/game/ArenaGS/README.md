# ArenaGS Protocol Package

`ArenaGS` is the authoritative game-side boundary. It retains the Phase 02 readiness marker and persisted load state, then implements the Phase 03 AdminPort dispatcher.

The dispatcher accepts only the versioned closed envelope defined in `schemas/protocol/`. It binds a bridge session to the first `hello` run ID, validates identifiers and bounded values, records a persisted bounded idempotency ledger, returns correlated errors for stale runs, and supports bounded Base64 chunk transfers with Adler-32 integrity checks and expiry.

Phase 03 supports handshake, heartbeat, pause, resume, snapshot, finalize, and typed deferred action/camera/checkpoint results. It deliberately does not implement rich observations, model access, route construction, scoring, recording, or camera behavior. `ModelProxyAI` remains the only company owner and stays inert.
