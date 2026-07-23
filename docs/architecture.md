# Phase 01 Architecture Boundary

```text
model provider / replay fixture
            |
      IModelProvider
            |
Arena.Orchestrator -- Arena.AdminProtocol -- ArenaGS (authoritative)
      |                     |                   |
 Arena.Storage         AdminPort            Game state/actions
 Arena.Scoring
 Arena.Camera -- Arena.Obs -- TypeScript overlay
```

The model-facing boundary ends at `IModelProvider`: it returns a validated common decision and has no GameScript, AdminPort, OBS, filesystem, shell, or operating-system access. `ArenaGS` is the only future executor of company actions. Scoring consumes authoritative metrics only; camera and overlay consume events but cannot change game state or score.

Phase 01 adds setup ownership without moving those boundaries: `Arena.Cli` composes the local configuration loader, Credential Manager adapter, runtime builder, and doctor probes. All generated OpenTTD and OBS template state stays beneath `.runtime`; local configuration holds credential references only. The doctor may authenticate to the local OBS WebSocket to inspect readiness, but it does not mutate OBS or launch OpenTTD.
