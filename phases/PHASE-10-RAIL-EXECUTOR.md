# Phase 10 — Rail Executor

## Objective

Add reliable high-level train-network tools while preserving the same provider-neutral decision model and benchmark-integrity rules.

## Goals

- Build operational point-to-point and multi-station rail services.
- Handle track geometry, signals, depots, station lengths, train composition, and orders deterministically.
- Upgrade and expand existing rail networks without uncontrolled disruption.
- Expose strategic choices to models without requiring tile-level plans.

## New or extended tools

```text
build_rail_route
build_rail_corridor
expand_station
add_passing_loop
upgrade_rail_line
add_train
replace_trains
resolve_rail_congestion
inspect_rail_network
```

## Executor responsibilities

- Select suitable station footprints and orientations.
- Estimate complete project cost before construction.
- Find routes with bounded terraforming, bridge, and tunnel policies.
- Choose rail type, locomotive, wagons, train length, and capacity.
- Build safe signaling patterns from certified templates.
- Validate depot access and order feasibility.
- Verify first complete trip and cargo loading/unloading.
- Detect congestion, deadlocks, stranded trains, and incompatible upgrades.

## Safety and recovery

- Never create knowingly unsafe signal layouts.
- Pause or isolate affected services during disruptive upgrades.
- Use staged construction and rollback boundaries.
- Retain sufficient cash reserve according to scenario constraints.
- Emit explicit failure causes for path, footprint, signal, vehicle, and order problems.

## Acceptance criteria

- Replay scenarios build a working passenger line and a freight line.
- A train completes a verified source-to-destination trip after construction.
- Save/load during staged rail construction resumes safely.
- Duplicate requests do not duplicate tracks, stations, or trains.
- Certified signaling templates pass automated topology checks.
- Congestion detection identifies a deliberately overloaded test corridor and an allowed tool can improve throughput.
- A train-only profit goal can run to completion without manual intervention.

## Out of scope

- Arbitrary model-authored track tile sequences.
- Full support for every NewGRF rail type.
- Ships and aircraft unless added by later scope decision.

## Exit condition

Phase 10 is complete when models can plan train strategies at the route and corridor level while deterministic code produces safe, functional rail networks.
