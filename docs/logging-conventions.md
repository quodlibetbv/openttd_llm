# Logging Conventions

Logs use structured fields. Every run-scoped event includes `run_id`, `correlation_id`, `component`, and `event_name`; include `decision_id`, `action_id`, and `game_date` whenever they exist. Errors also include `error_code`.

| Field | Purpose |
|---|---|
| `run_id` | Isolated execution and artifact scope. |
| `decision_id` | Model decision that caused the event, when applicable. |
| `action_id` | Deterministic GameScript action, when applicable. |
| `game_date` | Authoritative in-game date, when applicable. |
| `correlation_id` | Cross-component causality for one operation. |
| `component` | Emitting subsystem. |
| `event_name` | Stable event classification. |
| `error_code` | Stable failure classification, when applicable. |

Never log request headers, credential references with resolved values, API keys, cookies, raw provider request/response bodies, hidden reasoning, or machine-local paths outside an approved redacted diagnostic field.
