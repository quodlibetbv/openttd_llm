# Phase 07 Verification

This guide verifies the implemented road-profit benchmark surface on the supported Windows host. It covers immutable scenarios, replay-provider scoring, verifier/recalculation, accepted-action replay, and the independent scenario-constraint proof. It does not claim automatic recording, an overlay, or cinematic camera behavior; those remain Phase 08 and 09 work.

## Before running a benchmark

Run these from the repository root after the normal local runtime setup. The OpenTTD benchmark commands need the dedicated AdminPort credential and a passing bridge setup; they do not need OBS or a provider credential when using replay.

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/ttd-arena.ps1 doctor --verbose
pwsh ./scripts/bridge-smoke.ps1
pwsh ./scripts/test-all.ps1
pwsh ./scripts/ttd-arena.ps1 scenarios validate scenarios/road-profit-smoke-v1.yaml
pwsh ./scripts/ttd-arena.ps1 scenarios validate scenarios/road-profit-v1.yaml
```

`scenarios validate` reports the scenario ID, semantic version, SHA-256 fingerprint, and whether it is present in the publication catalog. A comparable benchmark run requires `published: yes`; validation of an unpublished draft is allowed only so it can be reviewed before `scenarios publish` updates the catalog.

## Provider-free benchmark evidence

Run the short immutable replay benchmark:

```powershell
pwsh ./scripts/ttd-arena.ps1 benchmark-smoke
```

The command creates a fresh `bridge-<run-id>` directory under `.runtime/runs`. It pauses simulation around the provider-neutral replay decision, accepts exactly one constrained route action through ArenaGS, waits for an operational route, captures periodic/final metrics, saves the final game, and seals the run.

Find and inspect the run:

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'run-manifest.json') } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

Get-Content (Join-Path $run.FullName 'bridge-result.json') -Raw | ConvertFrom-Json
Get-Content (Join-Path $run.FullName 'run-manifest.json') -Raw | ConvertFrom-Json
Get-Content (Join-Path $run.FullName 'final-metrics.json') -Raw | ConvertFrom-Json
Get-Content (Join-Path $run.FullName 'score.json') -Raw | ConvertFrom-Json
Get-Content (Join-Path $run.FullName 'actions.ndjson')
Get-Content (Join-Path $run.FullName 'metrics.ndjson')
```

The successful bridge result contains `benchmark-inputs`, `benchmark-request`, `benchmark-action-accepted`, `benchmark-objective`, `benchmark-score`, and `benchmark-manifest` checks. The manifest must list hashes for the starting save, content, settings, scenario, schemas, prompt, tool contract, retry policy, end condition, public streams, final save, final metrics, and score.

Verify the sealed evidence without starting OpenTTD or contacting a provider:

```powershell
pwsh ./scripts/ttd-arena.ps1 verify-run $run.FullName
pwsh ./scripts/ttd-arena.ps1 score recalculate $run.FullName
```

Both commands must succeed. `score recalculate` reports identical stored and regenerated score hashes; it is deliberately independent from the stored total alone.

## Accepted-action replay and constraint proof

Replay only the accepted source actions against a fresh matching fixed starting save:

```powershell
pwsh ./scripts/ttd-arena.ps1 actions replay $run.FullName
```

This is provider-free. It rejects a source whose seal, action stream, scenario constraint context, or starting-save fingerprint does not match. The replay run’s `bridge-result.json` contains `replay-baseline`, `replay-actions`, and `replay-metrics` checks; `metrics.ndjson` contains the fresh authoritative final vector compared within the scenario’s documented tolerances.

Exercise the two independent scenario-constraint layers:

```powershell
pwsh ./scripts/ttd-arena.ps1 road-constraint-smoke
```

The command first creates one valid route project while paused, then attempts a second route. The orchestrator records its pre-dispatch rejection in `actions.ndjson`; a separately dispatched trusted test action carries the same constraint context, and ArenaGS must reject it with `ARENA-ACTION-CONSTRAINT-VIOLATION` without creating a second project. The result includes `constraint-orchestrator` and `constraint-gamescript` checks.

## Opt-in live provider benchmark

Do not put a provider key in source, YAML, PowerShell history, or chat. Store it through the hidden credential prompt and configure only its reference in the ignored local provider configuration:

```powershell
pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/DeepSeek
pwsh ./scripts/ttd-arena.ps1 providers test deepseek
pwsh ./scripts/ttd-arena.ps1 benchmark run scenarios/road-profit-v1.yaml deepseek
```

The last command is billable. It uses the exact same normalized common request, typed tools, paused simulation boundary, scenario budget, constraint context, final metrics, scorer, and manifest workflow as replay. Its `provider-usage.ndjson` records bounded public usage metadata separately from score; it never contains the API key, raw provider body, raw prompt, or hidden reasoning.

If only a provider-free check is wanted, use:

```powershell
pwsh ./scripts/ttd-arena.ps1 benchmark run scenarios/road-profit-v1.yaml replay
```

## What can be seen visually now

Phase 07 benchmark evidence is intentionally artifact-first: `benchmark-smoke` runs the authoritative dedicated server without an OBS scene, overlay, spectator presentation, or recording. The visual route/camera proof is not part of the score and is deferred to later phases.

You can still visually confirm the isolated OpenTTD/spectator lifecycle with the existing Phase 02 smoke:

```powershell
pwsh ./scripts/smoke.ps1 -DurationSeconds 30
```

That opens the previously configured spectator windows and proves the host can launch and shut down the isolated runtime. It is not a substitute for the Phase 07 manifest, score, or action-replay evidence above.

## Statistical comparison guidance

Do not publish a winner from one nondeterministic provider run. For each provider/model combination, hold every published scenario and input hash constant, retain every sealed run directory, and record failures alongside completed scores. A practical minimum is 20 independent completed attempts per provider/model after a provider-specific smoke succeeds.

For each compatible group, report the count attempted, count completed, failure rate, median score, interquartile range, and a bootstrap confidence interval for the median. Do not aggregate runs whose scenario ID/version/SHA, starting-save hash, content/settings hash, observation/tool/prompt/retry/end-condition hash, or score formula differs. Provider latency and estimated cost can be reported beside outcomes, but they are not gameplay score components unless a future published scenario explicitly changes that rule.
