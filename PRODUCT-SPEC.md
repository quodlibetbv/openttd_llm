# Product Specification

## 1. Product statement

OpenTTD Model Arena is a reproducible benchmarking and media-production platform for evaluating language models as strategic OpenTTD operators. A user selects a goal, provider, model, credential reference, benchmark map, and number of runs. The platform launches the game, gives the model structured observations and a constrained tool set, executes valid actions through native OpenTTD scripting, records the complete run, presents public model decisions in a sidebar, directs the camera toward important events, calculates a score, and emits all evidence needed to reproduce the result.

The system is not a pixel-clicking bot. Language models make high-level decisions; deterministic game-side code performs pathfinding, construction, orders, verification, and recovery.

## 2. Primary use cases

1. Publish videos comparing models on the same OpenTTD objective.
2. Run repeatable benchmarks across providers, model versions, prompts, and budgets.
3. Create goal-driven challenge videos such as:
   - Road vehicles only; maximize operating profit.
   - Trains only; maximize delivered cargo.
   - Turn the map into a connected metropolis.
   - Serve every town while remaining solvent.
   - Build the highest-throughput freight network.
4. Inspect exactly what the model observed, decided, attempted, and achieved.
5. Re-run an experiment from a saved manifest and starting savegame.

## 3. Non-goals

- Giving models unrestricted mouse, keyboard, filesystem, shell, or network access.
- Treating provider latency as additional in-game thinking time.
- Displaying hidden chain-of-thought or provider-private reasoning.
- Allowing each provider a different tool set or observation schema in the same benchmark.
- Guaranteeing bit-for-bit identical model output across remote provider calls.
- Supporting arbitrary NewGRF combinations before compatibility is explicitly certified.

## 4. System components

### 4.1 Arena Orchestrator

A .NET 8 Windows application and CLI responsible for:

- Loading and validating scenario definitions.
- Resolving provider credentials from Windows Credential Manager.
- Creating isolated run directories.
- Launching and supervising OpenTTD server and spectator clients.
- Connecting to AdminPort.
- Pausing the simulation during provider calls.
- Validating model decisions against JSON Schema.
- Sending authorized actions to the GameScript.
- Controlling OBS and the overlay.
- Calculating final scores and generating manifests.
- Recovering from failures and continuing batch runs.

### 4.2 Arena GameScript

A Squirrel GameScript package responsible for:

- Receiving versioned commands through AdminPort.
- Querying game state through native APIs.
- Executing actions in the benchmark company context.
- Enforcing game-side constraints.
- Publishing normalized events and execution results.
- Driving client viewports to selected map positions.
- Saving checkpoints and final state on request.

### 4.3 ModelProxyAI

A minimal Squirrel AI package that creates or owns the benchmark company and otherwise remains inactive. The GameScript performs company actions through an explicit company context. The proxy must never independently build, buy, sell, borrow, or alter orders.

### 4.4 Provider adapters

Each adapter converts the common `ModelRequest` into a provider-specific API call and returns the common `ModelDecision`. Providers are plugins behind the same interface. Initial support is DeepSeek; subsequent adapters may include OpenAI, Anthropic, Google, local OpenAI-compatible endpoints, and recorded/replay providers.

### 4.5 Decision overlay

A local web application rendered as an OBS Browser Source. It shows only publishable information:

- Goal and constraints.
- Model/provider identity.
- Current game date.
- Current plan.
- Concise public reasoning.
- Execution state and results.
- Cash, profit, score, and selected scenario metrics.
- Provider latency and optional estimated cost.

### 4.6 Camera director

An event-driven service that selects locations and shot types. It controls multiple spectator clients with fixed zoom levels, then switches OBS sources rather than relying on fragile zoom automation.

### 4.7 Scoring engine

A pure, deterministic module that consumes the scenario definition, final snapshot, periodic metrics, and event log. It must never call a model. Given the same inputs, it must produce the same score and breakdown.

## 5. Core run flow

1. Validate scenario and provider configuration.
2. Create an immutable run manifest draft and isolated runtime directory.
3. Copy the starting savegame and certified content manifest.
4. Launch the dedicated server, GameScript, proxy AI, and spectator clients.
5. Establish secure local control channels and verify protocol versions.
6. Launch or connect to OBS and load the Arena scene collection.
7. Start recording after all components report ready.
8. Request an initial normalized observation.
9. Pause simulation before every model call.
10. Ask the model for a bounded, schema-valid public decision.
11. Validate goal constraints and action arguments outside the model.
12. Execute accepted actions through the GameScript.
13. Resume simulation for the requested review interval.
14. Stream events to the overlay, camera director, logs, and scoring metrics.
15. Repeat until the end condition, bankruptcy, fatal error, or budget exhaustion.
16. Save final game state, display a result scene, stop recording, finalize hashes and scores.

## 6. Model contract

A provider receives a normalized request containing:

- Goal summary and explicit constraints.
- Current and recent company metrics.
- Relevant towns, industries, stations, vehicles, routes, and opportunities.
- Active projects and prior action outcomes.
- Remaining model-call and token budget.
- Available tool schemas.
- A requirement to produce a concise public explanation.

A provider returns:

```json
{
  "decision_id": "d-000184",
  "public_summary": "Connect two underserved towns with four buses while preserving a cash reserve.",
  "observations": [
    "Both towns have growing populations",
    "Estimated route cost is below the project budget"
  ],
  "actions": [
    {
      "tool": "build_transport_route",
      "arguments": {
        "mode": "road",
        "source_id": 14,
        "destination_id": 27,
        "cargo": "passengers",
        "initial_vehicle_count": 4,
        "maximum_budget": 125000
      }
    }
  ],
  "next_review_game_days": 30
}
```

The arena validates syntax, schema, authorization, constraints, budgets, and entity references. Invalid output receives at most the scenario-defined retry count. Repeated invalid output becomes a no-op decision and a scored reliability event.

## 7. Benchmark fairness rules

- Every contestant receives the same starting savegame and scenario hash.
- Every contestant receives the same observation fields and tool schemas.
- Simulation is paused during provider calls and retries.
- Model-call, output-token, retry, and elapsed-run budgets are declared in the scenario.
- Provider latency and cost are measured separately from game score unless explicitly included by the goal.
- Camera and recording behavior cannot affect simulation state.
- Scoring is calculated from authoritative game data, not pixels or OCR.
- A score change requires a scoring-schema version change.
- A tool behavior change requires a tool-contract version change.
- A scenario is immutable once published; edits create a new scenario version and hash.

## 8. Run artifacts

Every run directory must contain:

```text
recording.mp4
final-save.sav
score.json
run-manifest.json
decisions.ndjson
game-events.ndjson
observations.ndjson
actions.ndjson
provider-usage.ndjson
camera-events.ndjson
component-logs/
checkpoints/
youtube-metadata.json
```

The manifest records application versions, OpenTTD version, content hashes, scenario and prompt hashes, provider/model identity, tool and observation schema versions, timing, exit reason, and every output hash. Secrets are never serialized.

## 9. Security model

- Provider secrets remain in Windows Credential Manager and are materialized only inside the provider process.
- The model never receives AdminPort, OBS, filesystem, shell, or operating-system credentials.
- AdminPort and overlay connections bind to loopback by default.
- All model-selected actions pass through an allowlist and typed validator.
- Paths are constrained to the active run directory.
- Logs apply centralized secret redaction before writing.
- OBS control uses a dedicated local credential.
- The GameScript rejects unknown protocol versions, commands, tools, and fields.

## 10. Quality attributes

### Reliability

A failed provider request, client crash, overlay crash, or recording fault must produce a classified result and preserve diagnostic artifacts. Batch execution continues unless the benchmark host is unsafe or corrupted.

### Reproducibility

The platform must regenerate the same game-side result when replaying a recorded sequence of accepted actions against the same starting state and compatible OpenTTD build.

### Observability

Every state transition has a correlation ID. Logs from all components include run ID, decision ID, action ID, game date, and wall-clock timestamp where applicable.

### Extensibility

Providers, goals, scoring functions, observations, and tools use versioned contracts. New functionality must not require changes to existing providers unless the common contract changes.

### Video quality

The published recording must remain understandable without reading logs. The sidebar must explain the model’s current intent, while the camera shows the relevant location and the final scene communicates the result.

## 11. Definition of completion

The first production release is complete when all conditions below are met:

- A clean supported Windows machine can follow `SETUP.md` without undocumented steps.
- `ttd-arena doctor` validates every dependency and reports actionable failures.
- At least three providers can run through the same adapter contract.
- Road and rail objectives complete without manual interaction.
- The metropolis scenario can run from start to configured end date.
- A ten-contestant, ten-run tournament can complete unattended.
- Individual run failures do not terminate the tournament.
- Recordings contain synchronized gameplay, overlay, and camera direction.
- Every published score can be traced to immutable manifests and logs.
- Replay mode can reproduce accepted game actions without provider access.
- Security, reliability, performance, and release acceptance tests pass.
- Installation, operation, scenario authoring, provider integration, and troubleshooting are documented.
