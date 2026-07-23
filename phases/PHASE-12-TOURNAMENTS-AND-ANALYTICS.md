# Phase 12 — Tournaments and Analytics

## Objective

Run large multi-model benchmark sets unattended and produce statistically meaningful leaderboards, reports, and reproducible evidence bundles.

## Goals

- Schedule contestants and repeated runs under identical conditions.
- Isolate individual failures and resume interrupted tournaments.
- Aggregate game score, reliability, latency, and cost without conflating them.
- Generate human- and machine-readable reports suitable for publication.

## Deliverables

1. Tournament definition schema listing scenario, contestants, model parameters, run count, order randomization, and concurrency.
2. Persistent tournament scheduler with resumable queue.
3. Host resource manager for ports, run directories, OBS exclusivity, and provider rate limits.
4. SQLite result store and export to JSON/CSV/Markdown.
5. Leaderboard calculations with median, mean, range, standard deviation, success rate, bankruptcy rate, invalid-decision rate, latency, tokens, and estimated cost.
6. Pairwise and confidence-interval reporting where sample size permits.
7. Artifact browser linking each leaderboard row to run evidence.
8. Replay-based audit command.
9. YouTube metadata generator using run and tournament summaries.

## Fair scheduling rules

- Randomize or rotate contestant order to reduce temporal provider bias.
- Respect provider-specific rate limits without changing in-game budgets.
- Record provider model identifiers exactly as returned or configured.
- Do not mix scenario versions in one leaderboard.
- Separate failed infrastructure runs from valid model failures.
- Preserve raw individual scores; never publish only an aggregate.

## Acceptance criteria

- A tournament with at least three contestants and five runs each completes unattended.
- Terminating the orchestrator mid-tournament allows restart without duplicating completed runs.
- One failed run does not terminate unrelated contestants.
- Aggregates can be regenerated from immutable run artifacts.
- Leaderboard filters prevent mixing incompatible scenario, tool, prompt, or score versions.
- Reports clearly separate game performance, reliability, speed, and cost.
- All SQLite queries used for large result sets have appropriate indexes and measured query plans.

## Required SQLite indexes

The implementation should include equivalent indexes for its final schema, such as:

```sql
CREATE INDEX IF NOT EXISTS ix_runs_tournament_contestant_status
ON runs(tournament_id, contestant_id, status);

CREATE INDEX IF NOT EXISTS ix_runs_scenario_model_completed
ON runs(scenario_hash, provider, model, completed_utc);

CREATE INDEX IF NOT EXISTS ix_scores_run_metric
ON score_components(run_id, metric_key);

CREATE INDEX IF NOT EXISTS ix_provider_usage_run_timestamp
ON provider_usage(run_id, recorded_utc);
```

## Exit condition

Phase 12 is complete when a multi-model comparison can run, resume, verify, aggregate, and publish without manual spreadsheet work or selective result handling.
