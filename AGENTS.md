# AGENTS.md — Codex Repository Instructions

## Mission

Build OpenTTD Model Arena as a reproducible, secure, hands-off benchmark and video-production platform. Models make high-level strategic decisions. Native OpenTTD scripting performs deterministic game actions. Every score and video must be backed by complete, verifiable run artifacts.

These instructions apply to Codex and all automated coding agents operating in this repository.

## Source of truth

Read these documents before changing code:

1. `PRODUCT-SPEC.md`
2. `GOALS-INDEX.md`
3. The active phase file under `phases/`
4. Relevant JSON Schemas and ADRs
5. Existing tests for the component being changed

When documents conflict, use this precedence:

```text
Accepted ADR > versioned schema > PRODUCT-SPEC.md > active phase document > implementation notes
```

Do not silently resolve conflicts. Add or update an ADR and identify the migration impact.

## Non-negotiable architecture rules

1. Do not implement gameplay as screen-coordinate mouse automation.
2. The model may select only allowlisted, typed tools.
3. The model never receives filesystem, shell, AdminPort, OBS, or operating-system access.
4. The GameScript is authoritative for game-state observations and action execution.
5. The scoring engine is deterministic and never calls a model.
6. Simulation is paused during provider calls and retries.
7. Provider latency and token cost are recorded separately from gameplay score unless a scenario explicitly says otherwise.
8. Public decision text must be concise and publishable. Never request, store, or display hidden chain-of-thought.
9. Scenario, observation, action, scoring, and protocol contracts are versioned.
10. Secrets must not be present in source, test fixtures, command-line arguments, logs, manifests, recordings, or snapshots.
11. A benchmark scenario is immutable after publication. Create a new version instead of editing published semantics.
12. A run is not successful unless required artifacts are finalized and verified.

## Intended repository layout

```text
/src
  /Arena.Cli
  /Arena.Orchestrator
  /Arena.AdminProtocol
  /Arena.Contracts
  /Arena.Providers
  /Arena.Scoring
  /Arena.Camera
  /Arena.Obs
  /Arena.Storage
  /Arena.Overlay

/openttd
  /game/ArenaGS
  /ai/ModelProxyAI

/schemas
  /protocol
  /observations
  /actions
  /goals
  /scores
  /manifests

/scenarios
/benchmarks
/tests
/scripts
/docs/adr
/phases
```

Respect component boundaries. Shared contracts belong in `Arena.Contracts` or `schemas`; provider-specific code belongs only in its provider adapter; OpenTTD-specific execution belongs in the GameScript and protocol boundary.

## Standard workflow

For every task:

1. Identify the active phase and its exit criteria.
2. Inspect existing code, tests, schemas, and ADRs before editing.
3. State the smallest coherent implementation slice.
4. Add or update tests first when practical.
5. Implement without unrelated refactoring.
6. Run the narrowest relevant tests, then the repository quality gate.
7. Update schemas, examples, and documentation in the same change.
8. Verify generated files contain no secrets or machine-local paths.
9. Summarize behavior changes, tests run, and remaining limitations.

Do not leave the repository in a state where contracts and implementations disagree.

## Commands

Use repository scripts when available because they encode the supported workflow.

```powershell
pwsh ./scripts/bootstrap.ps1
pwsh ./scripts/format.ps1
pwsh ./scripts/test-all.ps1
pwsh ./scripts/smoke.ps1

dotnet build -c Debug
dotnet test -c Debug

npm ci --prefix src/Arena.Overlay
npm test --prefix src/Arena.Overlay
npm run build --prefix src/Arena.Overlay
```

Do not claim a change is tested unless the command actually completed successfully. If a required tool is unavailable, record exactly which checks could not run.

## C# rules

- Target .NET 8 until an ADR changes the baseline.
- Enable nullable reference types and treat warnings as errors in production projects.
- Prefer immutable records for versioned contracts.
- Pass `CancellationToken` through all asynchronous boundaries.
- Use dependency injection at process boundaries; avoid service-locator patterns.
- Use structured logging with `run_id`, `decision_id`, `action_id`, and `game_date` scopes.
- Use explicit result types for expected failures; reserve exceptions for exceptional conditions.
- Never log request headers, credentials, or unredacted provider bodies.
- Keep provider HTTP code behind `IModelProvider`.
- Keep scoring functions pure and independently testable.
- Use deterministic clocks and random sources in tests.
- Avoid static mutable state.

## Squirrel/OpenTTD rules

- Keep protocol parsing separate from gameplay execution.
- Validate command version, run ID, correlation ID, and argument types before acting.
- Execute company actions only inside the intended company context.
- Check available funds and scenario constraints before starting a project.
- Return structured, machine-readable failure codes.
- Never continue a partially failed multi-step project without an explicit recovery path.
- Publish progress and camera events for long operations.
- Keep expensive searches incremental to avoid script timeouts.
- Preserve save/load compatibility for active protocol versions.
- ModelProxyAI must remain inert except for company ownership and heartbeat behavior explicitly defined by its contract.

## TypeScript and overlay rules

- The overlay is a projection of run state, not a source of truth.
- It must tolerate reconnects and replay the latest snapshot.
- Escape all provider-generated text before rendering.
- Enforce maximum lengths for public summaries and observation bullets.
- Do not display raw prompts, API payloads, credentials, or hidden reasoning.
- Keep the overlay legible at the production canvas size.
- Provide deterministic demo data for screenshot and visual tests.
- Avoid animation that obscures active gameplay or makes recordings difficult to read.

## Protocol and schema rules

- Every message has `protocol_version`, `message_type`, `run_id`, `message_id`, and `correlation_id` where applicable.
- Schemas reject unknown fields by default unless forward compatibility explicitly requires otherwise.
- Large AdminPort payloads use the defined chunking envelope with checksums and reassembly timeouts.
- Retries must be idempotent. Commands that may be retried require an idempotency key.
- A protocol change requires compatibility tests and a versioning decision.
- Never reuse a field with new semantics.
- Include representative valid and invalid examples beside each schema.

## Provider adapter rules

Every provider adapter implements the same behavior:

- Resolve its credential by reference at request time.
- Apply configured timeout, retry, and rate-limit policies.
- Translate the common request without changing benchmark semantics.
- Request strict structured output when supported.
- Parse into the common decision contract.
- Report token usage, latency, provider request ID, and classified errors.
- Redact secrets before diagnostics.
- Never execute tools directly.
- Never extend the tool set for only one provider in a shared benchmark.

A replay provider must exist for deterministic tests and demonstrations without remote API calls.

## Testing requirements

### Unit tests

Cover validators, schema migrations, scoring functions, provider parsing, redaction, retry classification, shot selection, and manifest hashing.

### Contract tests

Validate each provider adapter against recorded sanitized responses. Validate C# and Squirrel protocol implementations against the same fixtures.

### Integration tests

Exercise AdminPort connection, chunking, heartbeat, command execution, save/load, and process supervision against a real OpenTTD test instance.

### Replay tests

Given the same starting save and accepted-action log, replay must produce the same normalized final metrics within explicitly documented engine tolerances.

### End-to-end tests

A smoke scenario must launch all required components, execute at least one route, produce a result, and finalize artifacts without manual interaction.

## Security requirements

- Use Windows Credential Manager or an approved secret provider.
- Bind internal services to loopback by default.
- Validate every path before file access and keep writes under the run root.
- Redact bearer tokens, API keys, passwords, cookies, and configured secret patterns.
- Treat provider responses and scenario files as untrusted input.
- Enforce JSON size, string length, array length, recursion, and timeout limits.
- Do not reduce authentication, validation, or sandboxing to make a test pass.
- Add a regression test for every security defect.

## Benchmark integrity requirements

Changes to any item below require explicit review and usually a version bump:

- Observation fields or ranking.
- Tool availability or behavior.
- Decision interval semantics.
- Retry policy.
- Game pause behavior.
- Starting save or content manifest.
- Score formula or normalization.
- End condition.
- Provider prompt template.

Store hashes of these inputs in the run manifest. Never retroactively change a published leaderboard without preserving the original results.

## Error handling

Use stable error codes such as:

```text
ARENA-PROVIDER-TIMEOUT
ARENA-PROVIDER-INVALID-OUTPUT
ARENA-PROTOCOL-VERSION-MISMATCH
ARENA-ACTION-CONSTRAINT-VIOLATION
ARENA-ACTION-PATH-NOT-FOUND
ARENA-OPENTTD-PROCESS-EXITED
ARENA-OBS-RECORDING-FAILED
ARENA-ARTIFACT-VERIFICATION-FAILED
```

User-facing messages explain remediation. Logs include technical context but remain redacted.

## Documentation requirements

Update documentation in the same change when behavior, configuration, contracts, commands, or acceptance criteria change. Examples must be executable or clearly marked as illustrative. Do not document planned behavior as already implemented.

## Pull request completion checklist

A change is ready only when all applicable statements are true:

- The active phase goal is referenced.
- Scope is limited and architecture boundaries are preserved.
- Tests cover success, expected failure, and idempotent retry behavior.
- Formatting and static analysis pass.
- Schemas and examples are synchronized.
- Logs and fixtures contain no secrets.
- Windows paths and process behavior are tested or explicitly isolated.
- The smoke scenario still completes.
- Documentation reflects actual implementation.
- Known limitations are recorded.
- No acceptance criterion is claimed without evidence.

## Prohibited shortcuts

Do not:

- Add arbitrary `Task.Delay` calls to hide race conditions.
- Catch and ignore exceptions.
- Parse provider output with ad hoc string slicing when a schema parser is available.
- Base scores on overlay or video data.
- Store secrets in `.env` as the production credential design.
- Make tests depend on paid provider calls.
- Use mutable global singleton state for active runs.
- Couple camera behavior to scoring.
- Alter game settings between contestants.
- Commit generated recordings, savegames, credentials, local runtime binaries, or private benchmark data.
