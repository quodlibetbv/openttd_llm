# Phase 02 — Reproducible Game Run

## Objective

Automate the complete OpenTTD process lifecycle in an isolated run directory without model participation.

## Goals

- Start a dedicated server from a fixed savegame or scenario.
- Load ArenaGS and ModelProxyAI automatically.
- Launch spectator clients with stable window identities.
- Pause, resume, save, and terminate the game through controlled mechanisms.
- Preserve evidence after normal completion and process failure.

## Deliverables

1. Run-directory allocator with unique run IDs and path containment.
2. Process supervisor for server and spectator clients.
3. Generated server and spectator configurations derived from immutable templates.
4. Readiness detection based on explicit signals and bounded timeouts.
5. Graceful shutdown and forced-termination fallback.
6. Checkpoint and final-save management.
7. Replay smoke scenario requiring no remote provider.
8. Process logs separated by component.

## Lifecycle states

```text
Created → Preparing → StartingServer → WaitingForGameScript
→ StartingClients → Ready → Running → Finalizing
→ Completed | Failed | Cancelled
```

Every state transition must be persisted so an interrupted host can classify incomplete runs.

## Acceptance criteria

- A smoke command starts OpenTTD, creates the benchmark company, advances the game, saves, and exits with no manual input.
- Five sequential smoke runs do not leak processes or ports.
- Server crash, client crash, startup timeout, and cancellation produce distinct exit reasons.
- The starting save is copied, never modified in place.
- Run paths cannot escape the configured run root.
- Finalization preserves logs and the latest valid checkpoint even after abnormal termination.
- Process titles or identifiers are stable enough for later OBS capture configuration.

## Out of scope

- Sending gameplay commands over AdminPort.
- Querying detailed observations.
- Recording video.

## Exit condition

Phase 02 is complete when the orchestrator can execute repeated, isolated, unattended game lifecycles and reliably clean up or diagnose every process it launches.
