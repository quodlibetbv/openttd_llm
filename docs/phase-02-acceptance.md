# Phase 02 Acceptance Evidence

Phase 02 implements the reproducible-game-run objective in [`phases/PHASE-02-REPRODUCIBLE-GAME-RUN.md`](../phases/PHASE-02-REPRODUCIBLE-GAME-RUN.md). It is a provider-free lifecycle proof, not a gameplay benchmark: no model, AdminPort gameplay command, score, OBS recording, or public overlay is involved.

| Requirement | Evidence in the repository | Verification |
|---|---|---|
| Unique contained run directory | [`RunDirectoryAllocator`](../src/Arena.Storage/RunDirectoryAllocator.cs) reserves a timestamped cryptographic run ID through a staging directory; [`RunPathPolicy`](../src/Arena.Storage/RunPathPolicy.cs) rejects escapes and reparse points. | `RunDirectoryAllocatorTests` and `RunPathPolicyTests` |
| Persisted lifecycle | [`RunLifecycleJournal`](../src/Arena.Storage/RunLifecycleJournal.cs) appends durable NDJSON transitions and can classify an interrupted non-terminal run. | `RunLifecycleJournalTests` |
| Immutable templates and isolated profiles | [`RuntimeLayoutBuilder`](../src/Arena.Storage/RuntimeLayoutBuilder.cs) creates server/spectator templates; [`Phase02RunPreparation`](../src/Arena.Orchestrator/Phase02RunPreparation.cs) copies them below each run. | `RuntimeLayoutBuilderTests`, smoke artifacts |
| Explicit readiness and bounded start | [`Phase02RunService`](../src/Arena.Orchestrator/Phase02RunService.cs) checks the loopback game port and ArenaGS/ModelProxyAI markers with bounded timeouts. | `Phase02RunServiceTests`, live smoke |
| Supervised server and spectators | [`Phase02ProcessControl`](../src/Arena.Orchestrator/Phase02ProcessControl.cs) owns graceful shutdown and force-termination fallback. Spectators receive stable `Arena - Wide`, `Arena - Medium`, and `Arena - Close` titles. | success, crash, timeout, and title-failure tests; live smoke |
| Checkpoint/final-save preservation | The service copies a cached fixed start into `input/`, writes `checkpoints/checkpoint-0001.sav`, and finalizes `final-save.sav` when possible. | normal, crash, and cancellation tests; live smoke |
| Separate, safe artifacts | Server and every spectator receive separate stdout/stderr logs. OpenTTD-generated `secrets.cfg` is deleted before hashing/indexing artifacts. | normal smoke test checks both absence and artifact index |
| Distinct abnormal exits | Startup timeout, GameScript readiness failure, server exit, spectator exit, cancellation, and finalization failure have stable exit reasons/error codes. | `Phase02RunServiceTests` |

## Windows smoke procedure

Run this on the supported Windows host after bootstrap has copied the isolated OpenTTD runtime. A live provider, provider credential, OBS, and an OBS WebSocket connection are not required for this command. `doctor` may still report OBS-specific blocks while Phase 02 smoke work remains valid; the smoke command only requires the isolated OpenTTD runtime and an available loopback game port.

```powershell
pwsh ./scripts/smoke.ps1 -DurationSeconds 10
```

The equivalent direct CLI command is:

```powershell
pwsh ./scripts/ttd-arena.ps1 smoke --duration-seconds 10
```

Expected terminal result:

```text
Phase 02 smoke completed (completed).
```

During the run, visually confirm three OpenTTD spectator windows briefly appear with these stable titles (each title also includes the generated run ID):

- `Arena - Wide`
- `Arena - Medium`
- `Arena - Close`

The dedicated server intentionally has no gameplay window. Phase 02 does not switch OBS scenes or produce a recording, so no OBS visual result is expected yet. Once the command exits, the spectator windows should be gone.

Inspect the most recent evidence without exposing credentials:

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

Get-Content (Join-Path $run.FullName 'lifecycle.ndjson')
$result = Get-Content (Join-Path $run.FullName 'run-result.json') -Raw | ConvertFrom-Json
$result.final_state
$result.exit_reason
Get-ChildItem $run.FullName -Recurse -Filter '*.sav' | Select-Object FullName, Length
Get-ChildItem $run.FullName -Recurse -Filter 'secrets.cfg'
Get-Process openttd -ErrorAction SilentlyContinue
Get-NetTCPConnection -State Listen -LocalPort 3979 -ErrorAction SilentlyContinue
```

The first two result values must be `completed`. The save listing must include the copied starting save, checkpoint, and final save. The final three commands should produce no output: no generated secret file remains in the run, no OpenTTD process remains, and port 3979 is no longer listening.

For cancellation behavior, start a longer smoke and press `Ctrl+C` in the same console:

```powershell
pwsh ./scripts/smoke.ps1 -DurationSeconds 120
```

The command should exit with code `130`, a `cancelled` lifecycle/result state, component logs, and the latest valid checkpoint. Do not terminate OpenTTD from Task Manager for this test; that is a different server-crash path.

## Repetition check

Run five smoke lifecycles sequentially, not concurrently, before calling a host Phase 02-ready:

```powershell
1..5 | ForEach-Object {
    pwsh ./scripts/smoke.ps1 -DurationSeconds 0
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke run $_ failed with exit code $LASTEXITCODE."
    }
}

Get-Process openttd -ErrorAction SilentlyContinue
Get-NetTCPConnection -State Listen -LocalPort 3979 -ErrorAction SilentlyContinue
```

All five runs must complete and the two final checks must be empty. Retain the generated run directories for diagnosis; they contain only redacted lifecycle/result data, save artifacts, and component logs.
