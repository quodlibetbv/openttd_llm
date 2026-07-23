# ADR 0006: Immutable published scenarios and explicit run retention

- Status: Accepted
- Date: 2026-07-23

## Context

Benchmark comparisons are credible only when scenario semantics and evidence remain stable. Unbounded retention also risks filling the production host, while automatic deletion can destroy audit evidence.

## Decision

Published scenarios are immutable. Any semantic change to starting save, content, tools, observations, prompt, retry policy, score, or end condition creates a new scenario version and hash. The run manifest records hashes of every benchmark-defining input.

Retention is policy-driven: published or explicitly pinned runs are never deleted automatically; verified unpublished runs default to 30 days; failed or cancelled runs default to 14 days; disposable temporary files may be removed only after finalization has verified required artifacts. Cleanup is an explicit command, records what it removed, and never follows paths outside the run root.

## Consequences

- Phase 01 owns configurable cleanup implementation and path containment.
- Phase 07 owns scenario publication checks and run verifier behavior.
- Published leaderboards must identify scenario and contract hashes.

## Migration impact

Changing retention defaults is operational policy, but changing published scenario semantics requires a new version and preserves the prior results and artifacts.
