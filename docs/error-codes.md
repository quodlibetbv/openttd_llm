# Arena Error Codes

Error codes are stable, machine-readable identifiers. User-facing messages state the remediation; logs may add redacted technical context. Never parse human text to classify an error.

| Code | Meaning | Owner |
|---|---|---|
| `ARENA-PROVIDER-TIMEOUT` | The configured provider call did not finish within its timeout. | Providers |
| `ARENA-PROVIDER-INVALID-OUTPUT` | A provider response could not satisfy the common decision contract. | Providers |
| `ARENA-PROVIDER-INVALID-JSON` | A provider response was not syntactically valid JSON. | Providers |
| `ARENA-PROVIDER-SCHEMA-MISMATCH` | Valid JSON did not satisfy the closed common decision schema or safe bounds. | Providers |
| `ARENA-PROVIDER-REPLAY-EXHAUSTED` | A replay run requested more decisions than its fixture provides. | Providers |
| `ARENA-PROVIDER-REPLAY-OBSERVATION-MISMATCH` | A replay fixture does not match the normalized observation hash. | Providers |
| `ARENA-PROTOCOL-VERSION-MISMATCH` | A component cannot safely use the negotiated protocol version. | Admin protocol |
| `ARENA-PROTOCOL-INVALID-MESSAGE` | A protocol message failed validation or limits. | Admin protocol |
| `ARENA-PROTOCOL-MESSAGE-TOO-LARGE` | A direct or logical protocol payload exceeded its bounded size. | Admin protocol |
| `ARENA-PROTOCOL-CHUNK-INVALID` | A chunked protocol transfer had invalid metadata, ordering, or integrity data. | Admin protocol |
| `ARENA-PROTOCOL-CHUNK-TIMEOUT` | A chunked protocol transfer did not complete within its bounded reassembly window. | Admin protocol |
| `ARENA-PROTOCOL-STALE-CORRELATION` | A message belonged to another run, request, or idempotency key. | Admin protocol |
| `ARENA-ADMINPORT-UNAVAILABLE` | The authenticated loopback AdminPort could not be reached or rejected a packet. | Admin protocol |
| `ARENA-ADMINPORT-AUTHENTICATION-FAILED` | OpenTTD rejected the dedicated AdminPort credential. | Admin protocol |
| `ARENA-ADMINPORT-PROTOCOL-INCOMPATIBLE` | OpenTTD did not expose the required Admin protocol or GameScript update capability. | Admin protocol |
| `ARENA-ADMINPORT-QUEUE-FULL` | The bounded outbound AdminPort queue cannot accept another request. | Admin protocol |
| `ARENA-ADMINPORT-REQUEST-TIMED-OUT` | ArenaGS did not produce a correlated result before the request deadline. | Admin protocol |
| `ARENA-ADMINPORT-RECONNECT-FAILED` | A dropped AdminPort connection could not be restored within the configured retry limit. | Admin protocol |
| `ARENA-ADMINPORT-SECRET-INVALID` | The dedicated AdminPort credential cannot safely be written to OpenTTD `secrets.cfg`. | Admin protocol |
| `ARENA-ACTION-CONSTRAINT-VIOLATION` | A requested action violates scenario or game-side constraints. | GameScript |
| `ARENA-ACTION-PATH-NOT-FOUND` | Deterministic path construction found no allowed path. | GameScript |
| `ARENA-ACTION-STATION-PLACEMENT-FAILED` | A bounded station or depot placement search could not produce a buildable native placement. | GameScript |
| `ARENA-ACTION-BUDGET-EXCEEDED` | The next deterministic project command would exceed its declared budget or available funds. | GameScript |
| `ARENA-ACTION-INSUFFICIENT-FUNDS` | A route request declared more budget than the benchmark company currently has. | GameScript |
| `ARENA-ACTION-VEHICLE-UNSUITABLE` | No compatible passenger road vehicle could be selected or purchased. | GameScript |
| `ARENA-ACTION-ORDER-INVALID` | A required route order, station, or vehicle state could not be validated. | GameScript |
| `ARENA-ACTION-VERIFICATION-TIMED-OUT` | A route did not demonstrate movement before the bounded operational-verification deadline. | GameScript |
| `ARENA-OPENTTD-PROCESS-EXITED` | A supervised OpenTTD process exited unexpectedly. | Orchestrator |
| `ARENA-RUN-ALLOCATION-FAILED` | The orchestrator could not reserve a unique contained run directory. | Storage |
| `ARENA-RUN-PREPARATION-FAILED` | Isolated run templates, paths, or fixed-save preparation could not be completed safely. | Orchestrator |
| `ARENA-RUN-STARTUP-TIMED-OUT` | A server, readiness signal, or required spectator window did not become ready before its bounded timeout. | Orchestrator |
| `ARENA-RUN-GAMESCRIPT-NOT-READY` | ArenaGS or ModelProxyAI did not publish the expected explicit readiness signal. | Orchestrator |
| `ARENA-RUN-SERVER-EXITED` | The supervised dedicated OpenTTD server exited unexpectedly. | Orchestrator |
| `ARENA-RUN-SPECTATOR-EXITED` | A supervised spectator OpenTTD client exited unexpectedly. | Orchestrator |
| `ARENA-RUN-CANCELLED` | The caller cancelled the provider-free lifecycle and the supervisor finalized available artifacts. | Orchestrator |
| `ARENA-RUN-FINALIZATION-FAILED` | Shutdown or required final artifact preservation could not complete safely. | Orchestrator |
| `ARENA-RUN-CONSOLE-CONTROL-FAILED` | The controlled dedicated-server console could not perform an allowed lifecycle operation. | Orchestrator |
| `ARENA-RUN-ARTIFACT-MISSING` | A required checkpoint, final save, or other run artifact was not produced. | Orchestrator |
| `ARENA-OBS-RECORDING-FAILED` | OBS could not safely produce the expected recording artifact. | OBS |
| `ARENA-ARTIFACT-VERIFICATION-FAILED` | Required artifacts, hashes, or metadata could not be verified. | Storage |
| `ARENA-STORAGE-PATH-OUTSIDE-RUN-ROOT` | A file operation attempted to escape the active run root. | Storage |
| `ARENA-CONFIG-INVALID` | Local setup configuration is missing, malformed, unknown-field, or path-policy-invalid. | Setup |
| `ARENA-CONFIG-SECRET-DETECTED` | A local configuration attempted to contain a raw secret-shaped field. | Setup |
| `ARENA-CREDENTIAL-REFERENCE-INVALID` | A configuration value is not a valid Credential Manager reference or a CLI target is out of scope. | Storage |
| `ARENA-CREDENTIAL-MISSING` | A referenced Credential Manager entry is absent or empty. | Storage |
| `ARENA-CREDENTIAL-STORE-UNAVAILABLE` | Windows Credential Manager could not safely complete the requested operation. | Storage |
| `ARENA-RUNTIME-LAYOUT-INVALID` | The repository-contained runtime is missing, unsafe, or could not be generated. | Setup |
| `ARENA-DOCTOR-PREREQUISITE-FAILED` | A required host dependency or executable is absent, unreadable, or below the supported version. | Doctor |
| `ARENA-DOCTOR-CHECK-PASSED` | A structured doctor check passed. | Doctor |
| `ARENA-DOCTOR-DEFERRED` | A check is intentionally deferred to a later phase. | Doctor |
| `ARENA-DOCTOR-PATH-NOT-WRITABLE` | A repository-local runtime, run, or recording directory cannot be written safely. | Doctor |
| `ARENA-DOCTOR-PORT-UNAVAILABLE` | The configured loopback OpenTTD control port is unavailable. | Doctor |
| `ARENA-DOCTOR-DISK-SPACE-LOW` | The recording drive is below the configured free-space threshold. | Doctor |
| `ARENA-OBS-TEMPLATE-INVALID` | The generated OBS scene checklist/template is missing required content or cannot be written. | OBS |
| `ARENA-OBS-WEBSOCKET-UNAVAILABLE` | OBS WebSocket could not be reached or did not provide a supported response. | OBS |
| `ARENA-OBS-AUTHENTICATION-FAILED` | OBS WebSocket authentication is disabled, incomplete, or failed. | OBS |
| `ARENA-OBS-SCENE-REQUIREMENTS-MISSING` | OBS authenticated but required Arena scenes or sources are absent. | OBS |

New codes use `ARENA-<AREA>-<CONDITION>` and are added here with a regression test whenever they close a defect.
