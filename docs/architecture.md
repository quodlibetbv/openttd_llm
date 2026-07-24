# Phase 02 Architecture Boundary

```text
                            .runtime/runs/<run_id>
                                      |
  Arena.Cli -> Arena.Orchestrator -> Arena.Storage
                   |          |              |
                   |          |        lifecycle.ndjson, saves,
                   |          |        component logs, result hashes
                   |          |
       controlled dedicated-console bridge
          (pause, unpause, save, quit only)
                   |
       OpenTTD dedicated server -- loopback game port -- three spectators
                   |                                      |
        ArenaGS readiness marker                 stable capture titles
        ModelProxyAI readiness marker          Wide / Medium / Close
```

The Phase 02 supervisor owns only a reproducible process lifecycle. It generates run-local copies from immutable OpenTTD templates, starts a dedicated server from a cached fixed starting save, waits for explicit ArenaGS and ModelProxyAI readiness markers, starts three spectator clients, and finalizes checkpoint, save, logs, lifecycle journal, and result records.

The dedicated-console bridge is deliberately narrow: it accepts only fixed pause, unpause, save, and quit operations from the orchestrator. It is not exposed to a provider, a model, a spectator, or a future gameplay tool. It exists because OpenTTD's dedicated server console is the supported Phase 02 control surface; Phase 03 owns authenticated AdminPort messaging and Phase 06 owns deterministic gameplay actions.

`ArenaGS` and `ModelProxyAI` publish fixed lifecycle readiness markers only. Neither package executes a company action in this phase. The model/provider boundary therefore remains unchanged: providers have no filesystem, shell, operating-system, OBS, dedicated-console, or AdminPort access. Scoring, recording, overlay projection, and camera direction remain later boundaries.

All mutable OpenTTD profile state is confined to the run directory. The supervisor removes any OpenTTD-generated `secrets.cfg` before it writes the artifact index; source runtime templates and the cached starting save are never run as writable inputs. Every lifecycle transition is append-only and fsync'd so an interrupted host can classify an incomplete run without treating it as a completed benchmark.
