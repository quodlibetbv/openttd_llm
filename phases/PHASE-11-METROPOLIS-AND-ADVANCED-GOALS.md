# Phase 11 — Metropolis and Advanced Goals

## Objective

Support long-horizon goals that optimize town growth, connectivity, service quality, and map-wide development rather than only company profit.

## Goals

- Define measurable town-growth and connectivity objectives.
- Give models strategic tools for service expansion and infrastructure planning.
- Prevent destructive or meaningless growth exploits.
- Maintain solvency and operational quality over long game horizons.

## New capabilities

- Town-growth observations and trends.
- Coverage and accessibility metrics.
- Multi-modal route planning where allowed.
- Network-wide service-frequency and capacity management.
- Infrastructure plans spanning multiple decisions.
- Goal-aware opportunity ranking without embedding optimal answers.
- Optional approved town-growth interventions supported by the OpenTTD scripting API and scenario policy.

## Candidate tools

```text
inspect_town_growth
plan_regional_network
connect_towns
increase_service_frequency
relieve_congestion
upgrade_hub
fund_town_growth
rebalance_network
retire_unproductive_assets
```

Each tool must define exact allowed effects. No tool may provide unbounded money, directly set population, or bypass normal game costs unless the scenario explicitly defines a separate non-competitive creative mode.

## Metropolis scenario metrics

- Total population growth from baseline.
- Percentage of towns with qualifying passenger and mail service.
- Percentage of population connected through the transport network.
- Service frequency and station rating.
- Passenger and mail delivered.
- Network reach and hub connectivity.
- Congestion and vehicle-loss penalties.
- Company solvency and infrastructure efficiency.

## Acceptance criteria

- The metropolis scenario has a fixed starting save, end year, tool set, model budget, score formula, and published hash.
- A replay baseline completes the scenario and produces non-zero growth and connectivity.
- Growth scoring distinguishes population increase caused during the run from starting population.
- Service-quality metrics cannot be inflated by inactive stations or unusable routes.
- Long-running projects survive save/load and host restart.
- The camera and overlay can explain multi-step regional plans.
- At least one live provider completes the scenario without manual intervention.

## Out of scope

- Scenario-specific hidden scripts that make one provider’s strategy succeed.
- Direct city editing or sandbox population modification in competitive benchmarks.
- Unbounded context windows containing the entire historical game log.

## Exit condition

Phase 11 is complete when the platform supports a credible “turn the map into one metropolis” benchmark with transparent metrics, long-horizon planning, and publishable unattended recordings.
