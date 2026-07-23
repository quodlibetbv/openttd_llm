# Replay Fixture Format

Replay fixtures make automated tests and demonstrations deterministic without a paid provider call. The format is defined by `tests/fixtures/providers/replay-decision.v1.json`:

- `fixture_version`: fixture format version.
- `provider` and `model`: fixture identity only.
- `steps`: ordered deterministic responses.
- `expected_observation_sha256`: the exact normalized observation expected at that step.
- `decision`: a common `model-decision.v1` payload.
- `usage`: synthetic latency/token metrics retained separately from score.

Fixtures must be sanitized authored test data. They never contain real provider headers, credentials, raw transcripts, hidden reasoning, customer/game data, or paid-provider output.
