# Phase 04–06 Verification

This guide verifies the implemented observation, provider, and road-executor boundaries on the supported Windows host. It does not claim a scored benchmark, recording, overlay, or camera workflow; those are later phases.

## Acceptance status

Phases 04–06 are complete. The supported Windows verification evidence includes provider-independent observation replay, deterministic replay-road construction and recovery checks, save/load checks across every project stage, and an opt-in DeepSeek V4-Flash road smoke. The live smoke produced a schema-valid public decision, one typed `build_transport_route` action, and an `ARENA-ROUTE-OPERATING` event. Its decision, action, provider-usage, observation, and event artifacts stay in the ignored per-run directory; they are not committed.

## Preconditions

Bootstrap the repository-local OpenTTD runtime and pass the relevant setup checks first:

```powershell
pwsh ./scripts/bootstrap.ps1 -OpenTtdSource "C:\Program Files\OpenTTD"
pwsh ./scripts/ttd-arena.ps1 doctor --verbose
pwsh ./scripts/bridge-smoke.ps1
```

The bridge and Phase 04–06 OpenTTD checks need the dedicated AdminPort credential. OBS failures do not affect the provider-free road commands, but must be fixed before a future recording phase.

## Provider-free evidence

Run each command from the repository root. Every command allocates a fresh `.runtime/runs/bridge-<run-id>` directory and leaves the immutable source runtime untouched.

| Command | What it proves | Key evidence |
|---|---|---|
| `pwsh ./scripts/ttd-arena.ps1 observation-smoke` | ArenaGS emits an authoritative snapshot and the orchestrator writes a bounded canonical observation. | `observations.ndjson`, `game-events.ndjson`, `bridge-result.json` |
| `pwsh ./scripts/ttd-arena.ps1 observations replay <run-directory>` | Recorded snapshot/delta hashes replay to the reported human-readable state without OpenTTD. | CLI replay output and `observations.ndjson` |
| `pwsh ./scripts/ttd-arena.ps1 road-smoke` | A replayed `ModelDecision` builds and verifies a passenger road route. | `decisions.ndjson`, `actions.ndjson`, project events, `bridge-result.json` |
| `pwsh ./scripts/ttd-arena.ps1 fleet-smoke` | A completed route accepts a fleet expansion exactly once and remains operational. | fleet action/result records and `fleet-idempotency` check |
| `pwsh ./scripts/ttd-arena.ps1 road-budget-smoke` | A deliberately impossible project budget fails before construction and recovers without created route assets. | budget failure event and `budget-recovery` check |

For the project-persistence requirement, exercise every declared project state:

```powershell
$stages = 'proposed', 'validating', 'surveying', 'building_infrastructure',
    'buying_vehicles', 'configuring_orders', 'verifying'

foreach ($stage in $stages) {
    pwsh ./scripts/ttd-arena.ps1 road-save-load-smoke --stage $stage
    if ($LASTEXITCODE -ne 0) {
        throw "Save/load smoke failed at stage: $stage"
    }
}
```

Each successful save/load result contains the stage name plus `checkpoint-saved`, `checkpoint-restored`, `route-operational`, and `finalize` checks. The checkpoint path is supervisor-only; no model decision has console, save-file, or AdminPort authority.

Run the Phase 06 repeatability gate as twenty isolated replay route runs:

```powershell
pwsh ./scripts/road-soak.ps1
```

The script stops at the first failed run and preserves its run directory for diagnosis. `-Count`, `-Config`, and timeout parameters are available for focused local investigation; the default count is the Phase 06 acceptance count of twenty.

## Native bridge and tunnel links

The road executor persists every surveyed path edge as a typed native link. It
uses ordinary road edges first; when ordinary progress toward the target is
blocked, it makes a bounded, deterministic set of native bridge/tunnel
test-mode probes (maximum 24-tile span and 16 special-link probes per search
node). Each special link must have a straight ordinary-road approach and exit;
the search rejects turns across bridge or tunnel ramps because those native
commands can replace perpendicular road halves. A legacy road-only saved project is migrated only when every stored edge
is adjacent; otherwise it safely enters recovery rather than guessing topology.

The stock replay smoke route does not deliberately require an obstacle link, so
the absence of these events from an ordinary `road-smoke` run is expected. On a
controlled map where a valid route must cross water or a hill, inspect the
finished run for the corresponding public event:

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

$events = Get-Content (Join-Path $run.FullName 'game-events.ndjson') |
    ForEach-Object { $_ | ConvertFrom-Json }

$events | Where-Object EventCode -in 'ARENA-BRIDGE-CREATED', 'ARENA-TUNNEL-CREATED'
```

The route must still reach `ARENA-ROUTE-OPERATING`; the created-link event alone
is not a successful-route result. A dedicated fixed obstacle-save smoke is not
part of the published Phase 06 replay input, so retain any controlled-map run
directory as additional Windows evidence rather than comparing it with the
immutable road-profit benchmark.

## Inspect a finished run

`bridge-result.json` must report `succeeded: true` and have no error code. Inspect the latest run without exposing a credential:

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

Get-Content (Join-Path $run.FullName 'bridge-result.json') -Raw | ConvertFrom-Json
Get-Content (Join-Path $run.FullName 'observations.ndjson')
Get-Content (Join-Path $run.FullName 'game-events.ndjson')
Get-Content (Join-Path $run.FullName 'actions.ndjson')
pwsh ./scripts/ttd-arena.ps1 observations replay $run.FullName
```

Provider-free replay entries have simulated usage. A live provider run additionally records redacted `decisions.ndjson` and `provider-usage.ndjson`; neither file contains an API key, raw prompt, or hidden reasoning.

## Opt-in live DeepSeek proof

Do not paste a DeepSeek API key into chat, PowerShell history, a file, or `providers.local.yaml`. Save it through the CLI's hidden prompt:

```powershell
pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/DeepSeek
```

Add only the following metadata to the ignored `.config/providers.local.yaml` (or uncomment the equivalent example). The value after `credential_ref` is a reference, not a secret:

```yaml
config_version: 1
providers:
  deepseek:
    type: deepseek
    base_url: https://api.deepseek.com/
    model: deepseek-v4-flash
    credential_ref: credman:OpenTTDModelArena/DeepSeek
    timeout_seconds: 45
    maximum_transient_retries: 1
```

Validate the local configuration and Credential Manager reference without a remote request:

```powershell
pwsh ./scripts/ttd-arena.ps1 providers test deepseek
```

Then explicitly run the live proof:

```powershell
pwsh ./scripts/ttd-arena.ps1 provider-road-smoke deepseek
```

This final command makes one or more billable DeepSeek requests within the configured retry policy. Its public request supplies the trusted `decision_id`, a one-action limit, and the versioned `build_transport_route` argument contract; it does not supply host, filesystem, console, AdminPort, credential access, or an instruction to return hidden reasoning. For DeepSeek V4, the adapter explicitly selects non-thinking mode so the bounded output budget is reserved for the public decision JSON. The command pauses the simulation across the provider call and validation interval, requires the provider to choose the typed route action, and verifies the resulting route with the same downstream GameScript path used by replay. A successful result is the Phase 05/06 live-provider acceptance artifact.

## Visual boundary

The Phase 04–06 bridge commands intentionally run a dedicated OpenTTD server without a spectator window, OBS scene switch, or recording. Their primary evidence is the structured run artifact chain. To visually verify the isolated lifecycle now, run `pwsh ./scripts/smoke.ps1 -DurationSeconds 30` and observe the Wide, Medium, and Close spectator windows. Automatic recording, overlays, and camera framing are intentionally deferred to later phases.
