# OpenTTD Model Arena — Specification Set

OpenTTD Model Arena is a Windows-first, hands-off benchmark and video-production system in which an external language model operates an OpenTTD company through deterministic game tools. Each run starts from a fixed scenario, applies a declared goal and model budget, records the complete game, overlays human-readable model decisions, directs cinematic camera shots, and produces comparable scores and run artifacts.

## Current status

Phases 00–03 are complete. Alongside the idempotent Windows bootstrap, structured `doctor` command, and provider-free Phase 02 lifecycle smoke, the repository can run an authenticated, versioned AdminPort bridge smoke against a real isolated OpenTTD server. OpenTTD 15+ uses its native secure AdminPort login; a narrowly version-gated compatibility path supports OpenTTD 14.x. ArenaGS validates a closed envelope, run binding, idempotency, heartbeats, bounded chunking, and typed control results. Rich observations, provider calls, route construction, recording, and scoring remain later phases.

## Document map

| Document | Purpose |
|---|---|
| [PRODUCT-SPEC.md](PRODUCT-SPEC.md) | Product scope, architecture, interfaces, benchmark rules, and definition of completion. |
| [SETUP.md](SETUP.md) | Windows development and runtime setup. |
| [AGENTS.md](AGENTS.md) | Repository instructions for Codex and other coding agents. |
| [GOALS-INDEX.md](GOALS-INDEX.md) | Phase dependency map, milestone summary, and release gates. |
| [docs/architecture.md](docs/architecture.md) | Phase 03 process, protocol, artifact, and authority boundaries. |
| [docs/phase-02-acceptance.md](docs/phase-02-acceptance.md) | Phase 02 requirement-to-evidence map and Windows verification procedure. |
| [docs/phase-03-acceptance.md](docs/phase-03-acceptance.md) | Phase 03 requirement-to-evidence map, migration, and Windows bridge verification. |
| [docs/adr/](docs/adr/README.md) | Accepted architecture and compatibility decisions. |
| [phases/PHASE-00-FOUNDATION.md](phases/PHASE-00-FOUNDATION.md) | Product decisions, repository skeleton, and engineering baseline. |
| [phases/PHASE-01-SETUP-AND-DOCTOR.md](phases/PHASE-01-SETUP-AND-DOCTOR.md) | Repeatable Windows setup and environment diagnostics. |
| [phases/PHASE-02-REPRODUCIBLE-GAME-RUN.md](phases/PHASE-02-REPRODUCIBLE-GAME-RUN.md) | Automated OpenTTD process lifecycle and isolated runs. |
| [phases/PHASE-03-ADMINPORT-BRIDGE.md](phases/PHASE-03-ADMINPORT-BRIDGE.md) | Secure AdminPort/GameScript protocol and message transport. |
| [phases/PHASE-04-OBSERVATION-MODEL.md](phases/PHASE-04-OBSERVATION-MODEL.md) | Stable game-state observations and event model. |
| [phases/PHASE-05-MODEL-PROVIDERS.md](phases/PHASE-05-MODEL-PROVIDERS.md) | Provider-neutral model loop and DeepSeek adapter. |
| [phases/PHASE-06-ROAD-EXECUTOR-MVP.md](phases/PHASE-06-ROAD-EXECUTOR-MVP.md) | Deterministic road-vehicle construction and management tools. |
| [phases/PHASE-07-GOALS-SCORING-REPRODUCIBILITY.md](phases/PHASE-07-GOALS-SCORING-REPRODUCIBILITY.md) | Goal schema, scoring, manifests, and fair comparisons. |
| [phases/PHASE-08-RECORDING-AND-SIDEBAR.md](phases/PHASE-08-RECORDING-AND-SIDEBAR.md) | OBS recording and human-readable decision overlay. |
| [phases/PHASE-09-CINEMATIC-CAMERA.md](phases/PHASE-09-CINEMATIC-CAMERA.md) | Event-driven camera direction and shot selection. |
| [phases/PHASE-10-RAIL-EXECUTOR.md](phases/PHASE-10-RAIL-EXECUTOR.md) | Reliable train networks, signaling, stations, and upgrades. |
| [phases/PHASE-11-METROPOLIS-AND-ADVANCED-GOALS.md](phases/PHASE-11-METROPOLIS-AND-ADVANCED-GOALS.md) | Town-growth tools and complex long-horizon scenarios. |
| [phases/PHASE-12-TOURNAMENTS-AND-ANALYTICS.md](phases/PHASE-12-TOURNAMENTS-AND-ANALYTICS.md) | Batch benchmarking, leaderboards, statistics, and reports. |
| [phases/PHASE-13-HARDENING-AND-RELEASE.md](phases/PHASE-13-HARDENING-AND-RELEASE.md) | Reliability, security, packaging, documentation, and release completion. |

## Target command-line experience

```powershell
ttd-arena doctor

ttd-arena smoke --duration-seconds 10

ttd-arena run `
  --scenario scenarios/road-profit-v1.yaml `
  --provider deepseek `
  --model <model-id> `
  --key-ref credman:OpenTTDModelArena/DeepSeek `
  --runs 5 `
  --record

ttd-arena tournament `
  --scenario scenarios/road-profit-v1.yaml `
  --contestants tournaments/providers.yaml `
  --runs-per-contestant 10
```

## Release definition

The product is complete when a clean Windows machine can install the prerequisites, pass `ttd-arena doctor`, execute a full unattended tournament, recover from individual run failures, and produce synchronized videos, decision logs, final savegames, immutable manifests, and statistically comparable scores without manual interaction.

## Phase 03 quality gate

On a machine with the .NET 8 SDK and Node.js 20 or later:

```powershell
npm ci
npm run verify
dotnet restore OpenTTD.ModelArena.sln
dotnet build OpenTTD.ModelArena.sln -c Debug --no-restore
dotnet test OpenTTD.ModelArena.sln -c Debug --no-build
npm ci --prefix src/Arena.Overlay
npm test --prefix src/Arena.Overlay
npm run build --prefix src/Arena.Overlay
```

`pwsh ./scripts/test-all.ps1` runs the same source-quality gate on the supported Windows host. See [SETUP.md](SETUP.md) for bootstrap, Credential Manager, OBS/doctor setup, the live provider-free lifecycle smoke, and authenticated Phase 03 bridge verification. The source-quality gate does not itself launch OpenTTD.
