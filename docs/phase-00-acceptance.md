# Phase 00 Acceptance Evidence

This document maps the Foundation acceptance criteria to repository evidence. It records the scope of this phase; it does not claim later gameplay, setup, provider, or recording work is complete.

| Acceptance criterion | Evidence |
|---|---|
| A developer can build placeholder projects | `OpenTTD.ModelArena.sln`, ten .NET projects, the TypeScript overlay scaffold, and the root quality commands. |
| Every schema has valid and invalid examples | Eight schemas under `schemas/`, each with one valid and at least two invalid sibling fixtures; `scripts/validate-schemas.mjs` enforces this. |
| Manifest represents all benchmark-defining hashes and versions | `schemas/manifests/run-manifest.v1.json` and `Arena.Contracts/RunManifestContracts.cs`. |
| Providers cannot reference OpenTTD execution internals | `scripts/check-architecture.mjs` and `tests/architecture-boundaries.test.mjs`. |
| CI checks build, tests, schemas, formatting, and secrets | `.github/workflows/quality.yml`, `scripts/test-all.ps1`, root `npm run verify`. |
| Architecture decisions are deliberate | Six accepted ADRs in `docs/adr/`. |
| Replay testing does not need a paid provider | `IModelProvider`, `ReplayModelProvider`, sanitized fixture guidance, and C#/Node tests. |

Phase 00 closed after the accepted architecture review and Windows quality-gate path were approved. Its evidence remains historical; Phase 01 implementation and verification are documented separately in [phase-01-acceptance.md](phase-01-acceptance.md).
