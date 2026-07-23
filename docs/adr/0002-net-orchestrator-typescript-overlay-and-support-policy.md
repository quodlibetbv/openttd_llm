# ADR 0002: .NET orchestrator, TypeScript overlay, and initial support policy

- Status: Accepted
- Date: 2026-07-23

## Context

The platform needs a Windows-native process supervisor and credential boundary, an OBS Browser Source overlay, and a clear baseline before Phase 01 setup work begins.

## Decision

- Production orchestration and shared contracts target .NET 8.
- The overlay is a TypeScript web project and remains a projection of authoritative run state.
- The Phase 00 supported host is 64-bit Windows 11. Windows 10 is not a supported production host until a later ADR and CI matrix prove it.
- The initial compatibility floor is OpenTTD 14.0 and OBS Studio 28.0 with WebSocket 5.x. The Phase 01 doctor will verify the installed versions and later release ADRs will publish a tested matrix.
- Application releases use Semantic Versioning. Versioned contracts retain their meaning within a major version; incompatible changes create a new schema/contract major and migration path.
- Provider adapters run in-process initially behind `IModelProvider`. They may not leak provider SDK types into shared contracts. A future worker boundary requires an ADR and a compatibility-preserving transport contract.

## Consequences

- All production C# projects enable nullable references, deterministic builds, and warnings as errors.
- The overlay must escape provider-produced text and cannot become a scoring or control authority.
- Setup documentation must describe Windows 11 as the supported baseline rather than imply unsupported versions are certified.

## Migration impact

Moving to a supported Windows 10, Linux, a new OpenTTD/OBS baseline, or out-of-process providers requires a compatibility decision, CI coverage, and documented migration behavior.
