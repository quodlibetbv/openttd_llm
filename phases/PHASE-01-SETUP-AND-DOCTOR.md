# Phase 01 — Setup and Doctor

## Objective

Make development and production prerequisites repeatable on Windows and provide a diagnostic command that identifies configuration problems before a run starts.

## Goals

- Bootstrap the repository from a clean Windows account.
- Maintain an isolated OpenTTD runtime instead of modifying the user profile.
- Store secrets in Windows Credential Manager.
- Validate OBS, OpenTTD, ports, paths, and build dependencies with actionable diagnostics.

## Deliverables

1. `scripts/bootstrap.ps1` with idempotent restore, build, directory creation, and script-package installation.
2. `scripts/install-prerequisites.ps1` or documented manual fallback.
3. Machine-local configuration schema and example file.
4. Credential commands: set, test, list metadata, and remove.
5. `ttd-arena doctor` with structured check results and non-zero exit code on blocking failures.
6. OBS scene-collection template or generator containing required scenes and sources.
7. Isolated `.runtime/openttd` layout and generated configuration.
8. `scripts/test-all.ps1` and `scripts/clean.ps1`.

## Doctor checks

- Operating system, PowerShell, .NET, Node, OpenTTD, and OBS versions.
- Executable discovery and file permissions.
- Writable run and recording directories.
- Available ports and loopback binding.
- Installed GameScript and AI package metadata.
- Credential references without exposing values.
- OBS WebSocket authentication and required scene/source names.
- Disk-space threshold.
- Configuration and scenario schema validation.

## Acceptance criteria

- A clean Windows test machine can complete setup using only `SETUP.md` and repository scripts.
- Running bootstrap twice produces the same effective state and does not overwrite local credentials or configuration.
- Doctor distinguishes warnings from blocking failures.
- Every blocking failure includes a specific remediation.
- Logs from setup and doctor pass automated secret-redaction tests.
- The user’s normal OpenTTD profile remains unchanged.

## Out of scope

- Starting a complete benchmark run.
- AdminPort messaging.
- Provider calls.
- Gameplay tools.

## Exit condition

Phase 01 is complete when setup and doctor pass on a clean supported Windows machine and fail predictably when OpenTTD, OBS, credentials, or required ports are deliberately misconfigured.
