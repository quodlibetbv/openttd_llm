# Phase 13 — Hardening and Release

## Objective

Convert the complete feature set into a secure, recoverable, supportable Windows product that can produce public benchmarks and videos reliably.

## Goals

- Eliminate known single-run and tournament reliability weaknesses.
- Package installation, upgrades, configuration, and diagnostics.
- Complete security, performance, compatibility, and disaster-recovery testing.
- Publish operator, developer, provider, scenario, and troubleshooting documentation.

## Deliverables

1. Signed or verifiably packaged Windows release with CLI and required local services.
2. Versioned migration process for configuration, database, schemas, and run artifacts.
3. Crash recovery for orchestrator, overlay, spectator, OBS, and OpenTTD failure classes.
4. Tournament resume and quarantine workflow for corrupted runs.
5. Central secret redaction and security test suite.
6. Performance profiles for large maps, long games, large tournaments, and high-resolution recording.
7. Compatibility matrix for supported Windows, OpenTTD, OBS, and content versions.
8. Backup, retention, cleanup, and artifact archival commands.
9. Complete documentation set and sample scenarios.
10. Release smoke suite and production readiness checklist.
11. At least one full public benchmark package with videos, manifests, scores, and verification instructions.

## Reliability tests

- Kill and restart every managed process at controlled points.
- Disconnect AdminPort during idle and active commands.
- Interrupt provider calls and rate-limit responses.
- Exhaust disk space in a safe test environment.
- Corrupt a partial recording and confirm classified failure.
- Restart the host during a tournament and resume safely.
- Load old supported run artifacts and configuration versions.

## Security tests

- Credential redaction across logs, exceptions, manifests, and crash reports.
- Unauthorized AdminPort, overlay, and OBS connection attempts.
- Malformed and oversized model responses.
- Path traversal and unsafe filename attempts.
- Scenario schema abuse and resource-exhaustion inputs.
- Dependency and secret scanning in CI.

## Production acceptance criteria

- A clean supported Windows machine installs and passes `ttd-arena doctor` using published documentation.
- A road benchmark, rail benchmark, and metropolis benchmark each complete hands-off with recording enabled.
- A ten-contestant, ten-run tournament completes with automatic retries and failure isolation.
- Every successful run passes `ttd-arena verify-run`.
- Replay reproduces accepted actions without provider credentials.
- No credential appears in a complete security scan of generated artifacts.
- Recordings contain synchronized gameplay, readable sidebar decisions, cinematic focus, and final results.
- Published leaderboards identify all benchmark-defining versions and hashes.
- Upgrade and rollback procedures are tested.
- All critical and high-severity defects are closed; accepted lower-severity limitations are documented.

## Final definition of completion

The project is complete when a non-developer operator can select a scenario and model configuration, launch a tournament, leave the machine unattended, and later receive verified videos, savegames, logs, scores, leaderboards, and publication metadata for every completed run, with failures clearly classified and recoverable.

## Post-release backlog boundary

The following are optional future releases rather than blockers unless promoted by an ADR:

- Automated narration and post-production editing.
- Distributed execution across multiple hosts.
- Hosted benchmark service.
- Community scenario marketplace.
- Ships and aircraft tool packs.
- Linux production host support.
- Live-stream mode.
- Viewer voting or interactive goals.
