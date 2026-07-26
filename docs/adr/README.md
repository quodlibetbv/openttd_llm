# Architecture Decision Records

Accepted ADRs are the highest-precedence implementation authority after the repository instructions. Each ADR records the decision, consequences, and migration impact. A new ADR is required before changing a benchmark-defining boundary.

| ADR | Status | Decision |
|---|---|---|
| [0001](0001-native-gamescript-executor.md) | Accepted | Native GameScript is the sole gameplay executor. |
| [0002](0002-net-orchestrator-typescript-overlay-and-support-policy.md) | Accepted | .NET 8 orchestration, TypeScript overlay, initial host compatibility, versioning, and provider boundary. |
| [0003](0003-adminport-transport.md) | Accepted | Authenticated AdminPort is the game-control transport. |
| [0004](0004-pause-simulation-for-provider-calls.md) | Accepted | Simulation pauses across provider calls and corrective retries. |
| [0005](0005-public-summaries-not-hidden-reasoning.md) | Accepted | Only concise publishable summaries are requested or persisted. |
| [0006](0006-immutable-published-scenarios-and-retention.md) | Accepted | Published scenarios are immutable and run retention is explicit. |
| [0007](0007-immutable-benchmark-inputs-and-pure-scoring.md) | Accepted | Comparable runs snapshot immutable inputs and use pure score/replay boundaries. |
