# Phase 03 Architecture Boundary

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

`ArenaGS` is the authoritative protocol dispatcher. It accepts a closed v1 envelope, binds the first `hello` to a run ID, stores a bounded persisted idempotency ledger, and returns typed results for the Phase 03 control surface. Action, camera, and checkpoint messages are deliberately typed deferred results rather than gameplay execution. `ModelProxyAI` remains inert. The model/provider boundary therefore remains unchanged: providers have no filesystem, shell, operating-system, OBS, dedicated-console, or AdminPort access. Rich observations, route execution, scoring, recording, overlay projection, and camera direction remain later boundaries.

All mutable OpenTTD profile state is confined to the run directory. The supervisor removes both generated OpenTTD secrets and the dedicated AdminPort `secrets.cfg` before it writes bridge evidence; source runtime templates and the cached starting save are never run as writable inputs. Every lifecycle transition is append-only and fsync'd so an interrupted host can classify an incomplete run without treating it as a completed benchmark.
