# Arena Error Codes

Error codes are stable, machine-readable identifiers. User-facing messages state the remediation; logs may add redacted technical context. Never parse human text to classify an error.

| Code | Meaning | Owner |
|---|---|---|
| `ARENA-PROVIDER-TIMEOUT` | The configured provider call did not finish within its timeout. | Providers |
| `ARENA-PROVIDER-INVALID-OUTPUT` | A provider response could not satisfy the common decision contract. | Providers |
| `ARENA-PROVIDER-REPLAY-EXHAUSTED` | A replay run requested more decisions than its fixture provides. | Providers |
| `ARENA-PROVIDER-REPLAY-OBSERVATION-MISMATCH` | A replay fixture does not match the normalized observation hash. | Providers |
| `ARENA-PROTOCOL-VERSION-MISMATCH` | A component cannot safely use the negotiated protocol version. | Admin protocol |
| `ARENA-PROTOCOL-INVALID-MESSAGE` | A protocol message failed validation or limits. | Admin protocol |
| `ARENA-ACTION-CONSTRAINT-VIOLATION` | A requested action violates scenario or game-side constraints. | GameScript |
| `ARENA-ACTION-PATH-NOT-FOUND` | Deterministic path construction found no allowed path. | GameScript |
| `ARENA-OPENTTD-PROCESS-EXITED` | A supervised OpenTTD process exited unexpectedly. | Orchestrator |
| `ARENA-OBS-RECORDING-FAILED` | OBS could not safely produce the expected recording artifact. | OBS |
| `ARENA-ARTIFACT-VERIFICATION-FAILED` | Required artifacts, hashes, or metadata could not be verified. | Storage |
| `ARENA-STORAGE-PATH-OUTSIDE-RUN-ROOT` | A file operation attempted to escape the active run root. | Storage |

New codes use `ARENA-<AREA>-<CONDITION>` and are added here with a regression test whenever they close a defect.
