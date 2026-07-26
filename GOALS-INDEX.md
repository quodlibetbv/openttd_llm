# Phase Goals and Milestone Index

## Delivery model

Each phase produces a demonstrable increment with explicit exit criteria. A phase is complete only when its acceptance tests pass and required documentation is updated. Later phases may begin in parallel only where dependencies are satisfied and benchmark contracts are not still unstable.

## Current status

Phases 00–06 are complete. Phase 04 provides canonical, replayable authoritative observations; Phase 05 provides interchangeable replay and DeepSeek decision adapters; and Phase 06 provides deterministic road-route execution, recovery, and operation verification. Phase 07 implementation provides immutable road-profit scenario, scoring, manifest, verification, and action-replay commands; its status remains in progress until the documented Windows benchmark evidence is accepted. Phase 08-and-later documents define planned deliverables and must not be read as implemented behavior.

## Milestone summary

| Phase | Milestone | Primary outcome | Depends on |
|---:|---|---|---|
| 00 | Foundation | Approved architecture, contracts, repository, quality gates | — |
| 01 | Setup and Doctor | Clean Windows setup is repeatable and diagnosable | 00 |
| 02 | Reproducible Game Run | OpenTTD starts, runs, saves, and exits unattended | 01 |
| 03 | AdminPort Bridge | Versioned, secure, reliable orchestrator/GameScript messaging | 02 |
| 04 | Observation Model | Stable authoritative state snapshots and events | 03 |
| 05 | Model Providers | Provider-neutral decision loop with DeepSeek and replay | 04 |
| 06 | Road Executor MVP | A model can build and operate profitable road routes | 05 |
| 07 | Goals and Scoring | Fair, reproducible benchmark scenarios and scores | 06 |
| 08 | Recording and Sidebar | Complete videos with synchronized public decisions | 07 |
| 09 | Cinematic Camera | Event-driven, hands-off, understandable camera work | 08 |
| 10 | Rail Executor | Models can build and maintain reliable train networks | 07, 09 |
| 11 | Metropolis Goals | Long-horizon town-growth and advanced planning scenarios | 10 |
| 12 | Tournaments and Analytics | Multi-model batch runs, leaderboards, reports, and replay | 11 |
| 13 | Hardening and Release | Secure, recoverable, packaged production release | 12 |

## Major release gates

### Gate A — Technical proof of control

Completed after Phase 03.

Evidence:

- OpenTTD runs unattended in an isolated runtime.
- The orchestrator and GameScript exchange authenticated, versioned messages.
- A command can be executed exactly once and its result correlated.
- A final save and structured logs are preserved after termination.

### Gate B — Playable benchmark MVP

Completed after Phase 07.

Evidence:

- DeepSeek and replay providers use the same contract.
- A model can inspect opportunities, build road routes, buy vehicles, set orders, and review performance.
- A road-profit scenario produces deterministic score calculations.
- Runs are comparable from identical starting state and declared budgets.

### Gate C — Publishable video MVP

Completed after Phase 09.

Evidence:

- OBS records the full run automatically.
- The sidebar shows synchronized, human-readable decisions and results.
- The camera follows relevant construction, route openings, and milestones.
- A result scene summarizes score and benchmark metadata.

### Gate D — Complete gameplay benchmark

Completed after Phase 11.

Evidence:

- Road and rail goals complete without manual intervention.
- The system supports long-horizon metropolis objectives.
- Executors recover from common construction and operational failures.
- Tools remain provider-neutral and goal constraints are enforced.

### Gate E — Production release

Completed after Phase 13.

Evidence:

- Tournaments complete unattended across multiple providers.
- Run failures are isolated and resumable.
- Every score is traceable and verifiable.
- Installation, security, packaging, diagnostics, and operational documentation are complete.

## Global definition of done

Every phase must satisfy these shared conditions:

- Required tests pass on the supported Windows environment.
- New external behavior has a versioned contract or configuration schema.
- Failures are classified and observable.
- No secret or machine-local path is committed.
- Documentation describes actual behavior.
- The smoke run remains functional.
- Run artifacts remain backward-readable or have an explicit migration path.
