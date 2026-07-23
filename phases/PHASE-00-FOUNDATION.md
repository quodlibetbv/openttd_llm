# Phase 00 — Foundation

## Objective

Create the agreed engineering baseline before implementing gameplay. This phase converts the product concept into explicit boundaries, versioned contracts, repository structure, and automated quality gates.

## Goals

- Approve the product architecture and security model.
- Establish repository structure and component ownership.
- Define the first protocol, observation, action, goal, score, and manifest versions.
- Create deterministic test fixtures and a replay-first development strategy.
- Make architecture changes deliberate through ADRs.

## Deliverables

1. Repository solution containing placeholder projects for CLI, orchestrator, protocol, contracts, providers, scoring, camera, OBS, storage, and overlay.
2. OpenTTD GameScript and ModelProxyAI package skeletons with valid metadata.
3. Initial JSON Schemas:
   - `protocol-envelope.v1.json`
   - `observation.v1.json`
   - `model-decision.v1.json`
   - `action-request.v1.json`
   - `action-result.v1.json`
   - `goal.v1.json`
   - `score.v1.json`
   - `run-manifest.v1.json`
4. ADRs for:
   - Native GameScript executor instead of screen automation.
   - .NET orchestrator and TypeScript overlay.
   - AdminPort transport.
   - Simulation pause during provider calls.
   - Public summaries rather than hidden reasoning.
   - Immutable published scenarios.
5. Central error-code catalog and logging-field conventions.
6. Initial CI workflow for build, unit tests, schema validation, formatting, and secret scanning.
7. Replay-provider interface and sanitized fixture format.

## Required decisions

- Supported Windows versions.
- Minimum OpenTTD and OBS compatibility policy.
- Semantic versioning policy for application and contracts.
- Whether provider adapters run in-process initially or behind a future worker boundary.
- Run directory retention and cleanup policy.
- Benchmark publication and scenario immutability policy.

## Acceptance criteria

- A new developer can clone the repository and build placeholder projects.
- Every schema has at least one valid and two invalid test examples.
- The manifest schema can represent versions and hashes of every benchmark-defining input.
- Architecture tests prevent provider projects from referencing OpenTTD execution internals.
- CI rejects committed secret patterns and malformed schemas.
- `PRODUCT-SPEC.md`, `SETUP.md`, and `AGENTS.md` are present and internally consistent.

## Out of scope

- Launching OpenTTD.
- Calling a real provider.
- Recording video.
- Implementing pathfinding or scoring formulas.

## Exit condition

Phase 00 is complete when the architecture review is accepted, the repository builds, contract tests pass, and all Phase 01 work can proceed without unresolved technology-selection questions.
