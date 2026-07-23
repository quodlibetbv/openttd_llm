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

The remaining Phase 00 exit-condition evidence is an accepted architecture review and successful execution of these gates on the supported Windows host. The initial CI workflow supplies the Windows verification path; Phase 01 may not begin until that review accepts the decisions in `docs/adr/`.
