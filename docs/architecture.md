# Phase 03–06 Architecture Boundary

```text
                                  .runtime/runs/<run_id>
                                             |
    Arena.Cli -> Arena.Orchestrator -> Arena.Storage
                         |                    |
                         |              lifecycle.ndjson,
                         |              bridge-result.json,
                         |              component logs
                         |
              authenticated loopback AdminPort client
              bounded queue, ping, retry, chunk reassembly
                         |
              OpenTTD dedicated server / ArenaGS
                         |
             versioned, closed GameScript envelope
          hello, heartbeat, control results, errors, chunks
                         |
              loopback game port -- Phase 02 spectators
                                      Wide / Medium / Close
```

The Phase 02 supervisor still owns the reproducible process lifecycle: it generates run-local copies from immutable OpenTTD templates, starts a dedicated server from a cached fixed starting save, waits for explicit ArenaGS and ModelProxyAI readiness markers, starts spectators for the lifecycle smoke, and finalizes saves, logs, lifecycle journals, and result records.

Phase 03 adds a separate authenticated AdminPort path from the orchestrator to ArenaGS. Its password is resolved from a dedicated Credential Manager reference only while a run-local `secrets.cfg` is needed; it is deleted before bridge artifacts are reported. Both AdminPort and the game server bind to loopback. OpenTTD 15+ uses its native X25519/PAKE login and encrypted records; the older password-only flow is permitted only for a detected 14.x executable, never as a generic downgrade. The client accepts only the OpenTTD Admin protocol version and GameScript capability it needs, then enforces bounded queues, keepalives, reconnect limits, correlation, and chunk integrity.

`ArenaGS` is the authoritative protocol dispatcher. It accepts a closed v1 envelope, binds the first `hello` to a run ID, stores a bounded persisted idempotency ledger, and returns typed results. `ModelProxyAI` remains inert. The model/provider boundary therefore remains unchanged: providers have no filesystem, shell, operating-system, OBS, dedicated-console, or AdminPort access.

Phase 04 adds authoritative GameScript snapshots and normalized events for the company, economy, network, active projects, and bounded opportunities. The orchestrator normalizes them into canonical provider observations, records snapshots/deltas/events as NDJSON below the run root, and can replay the record without opening OpenTTD. Providers receive the bounded public observation only; richer executor state remains inside ArenaGS.

Phase 05 adds the provider-neutral `IModelProvider` boundary. Replay and DeepSeek adapters return the same validated `ModelDecision` contract. The common request carries a trusted decision correlation, bounded public observation, action limit, and versioned public tool-argument metadata; it never carries an executor capability. A provider call is made only while the simulation is paused; the orchestrator records redacted decision/action/usage artifacts and resumes at a safe boundary. A Credential Manager reference is resolved at request time and is never passed through configuration values, process arguments, artifacts, or provider-visible tools.

Phase 06 turns the typed road actions into deterministic GameScript projects. ArenaGS validates the action against the latest snapshot, creates a persisted project state machine, builds and verifies the route in the intended company context, and reports structured progress or recovery errors. A supervisor-only checkpoint request can save/reload a project at each declared state; this console capability is never included in a model request. Scoring, recording, overlay projection, and camera direction remain later boundaries.

All mutable OpenTTD profile state is confined to the run directory. The supervisor removes both generated OpenTTD secrets and the dedicated AdminPort `secrets.cfg` before it writes bridge evidence; source runtime templates and the cached starting save are never run as writable inputs. Every lifecycle transition is append-only and fsync'd so an interrupted host can classify an incomplete run without treating it as a completed benchmark.
