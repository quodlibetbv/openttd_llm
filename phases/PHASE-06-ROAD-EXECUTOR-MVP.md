# Phase 06 — Road Executor MVP

## Objective

Enable a model to create and operate road-vehicle routes through reliable high-level tools implemented by deterministic GameScript logic.

## Goals

- Build routes without screen automation or tile-by-tile model instructions.
- Verify that each project is operational before reporting success.
- Recover cleanly from infeasible placement, pathfinding, budget, and vehicle-selection failures.
- Provide enough tools to play the first road-profit benchmark.

## Initial tool set

```text
inspect_company
list_opportunities
inspect_town
inspect_industry
build_transport_route
expand_route
reduce_route
replace_vehicles
repay_loan
take_loan
wait
```

## `build_transport_route` responsibilities

- Validate source, destination, cargo, mode, and budget.
- Select compatible station locations.
- Find a road path using bounded incremental search.
- Build road segments, bridges, tunnels, depots, and stations as permitted.
- Select compatible road vehicles.
- Purchase the requested initial fleet.
- Configure and verify orders.
- Start vehicles and confirm route traversal.
- Emit progress, cost, created-entity, camera, and final result events.

## Project state machine

```text
Proposed → Validating → Surveying → BuildingInfrastructure
→ BuyingVehicles → ConfiguringOrders → Verifying
→ Completed | Recovering → Failed
```

Each project has an ID and persists enough state to survive save/load.

## Failure behavior

- Never report success merely because construction commands returned success individually.
- Stop before exceeding the declared maximum budget.
- Classify failures such as station placement, path not found, insufficient funds, unsuitable vehicle, order validation, and verification timeout.
- Remove or reuse partial assets only according to a documented recovery policy.
- Do not create repeated duplicate stations or depots after a retried command.

## Acceptance criteria

- The replay provider can build a working passenger bus route from a fixed smoke save.
- The DeepSeek provider can select and execute at least one profitable road opportunity using the same tools.
- Repeating an action with the same idempotency key does not duplicate infrastructure or vehicles.
- Save/load during each project stage resumes or fails safely.
- Budget enforcement is exact and tested around boundary conditions.
- A completed route has valid stations, depot access, vehicles, orders, and demonstrated movement.
- Twenty smoke runs complete without manual repair or leaked projects.

## Out of scope

- Railways.
- Cinematic camera switching.
- Complex town-growth interventions.

## Exit condition

Phase 06 is complete when the system can finish a short road-only game autonomously and every action outcome is explainable from structured results and logs.
