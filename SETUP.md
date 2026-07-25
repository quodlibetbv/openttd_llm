# Windows Setup Guide

> Status: Phases 01–06 provide setup, isolated lifecycle, authenticated bridge, observation/replay, provider, and road-executor verification commands. A real DeepSeek call is opt-in and requires a locally configured Credential Manager reference. Recording, scoring, and benchmark scenarios remain later phases.

## Supported host

The supported production baseline is 64-bit Windows 11 (build 22000 or later), PowerShell 7, .NET 8 SDK, Node.js 20 or later, OpenTTD 14 or later, and OBS Studio 28 or later with WebSocket 5.x. Windows 10 and non-Windows hosts are not production-supported until an ADR changes the policy.

Use a dedicated Windows account for unattended work. The Arena never edits that account's normal OpenTTD or OBS profile.

## Install prerequisites

Review the manual installation links without changing the machine:

```powershell
pwsh ./scripts/install-prerequisites.ps1
```

To install the reviewed package list through `winget`, opt in explicitly:

```powershell
pwsh ./scripts/install-prerequisites.ps1 -Install
```

If `winget` is unavailable, install these from their official sources, start a new PowerShell 7 session, and confirm that `dotnet`, `node`, and `npm` are on `PATH`:

1. Git for Windows: <https://git-scm.com/download/win>
2. PowerShell 7: <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows>
3. .NET 8 SDK: <https://dotnet.microsoft.com/download/dotnet/8.0>
4. Node.js LTS: <https://nodejs.org/>
5. OpenTTD 14 or later: <https://www.openttd.org/downloads/openttd-releases/latest>
6. OBS Studio 28 or later: <https://obsproject.com/download>

## Bootstrap a checkout

Clone the repository, then give bootstrap the installed OpenTTD directory. The source directory is copied into the repository runtime; it is not used as a profile directory.

```powershell
git clone <repository-url> openttd-model-arena
cd openttd-model-arena
pwsh ./scripts/bootstrap.ps1 -OpenTtdSource "C:\Program Files\OpenTTD"
```

Bootstrap is safe to run again. It restores/builds the repository, builds the overlay, creates runtime directories, copies the Arena packages, writes generated runtime configuration, and creates local config only when it does not already exist. It preserves existing local settings; the only versioned migration is insertion of the non-secret `openttd.admin_credential_ref` when a Phase 02 configuration lacks it. It never creates, changes, or reads a credential value, and excludes OpenTTD profile configuration (including `private.cfg` and `secrets.cfg`) from the isolated copy.

The generated OpenTTD configuration fixes English language, 2560×1440 resolution, 100% GUI scale, a bounded 10-minute autosave policy, local server discovery, and loopback control binding. Scenario-specific simulation semantics and content policy remain later versioned concerns; bootstrap does not choose a benchmark map, goal, or score setting.

The generated repository-local state is intentionally ignored by Git:

```text
.config/
  arena.local.yaml
  providers.local.yaml
.runtime/
  openttd/
  obs/Arena-Scene-Collection.template.json
  runs/
  recordings/
  cache/
  temp/
artifacts/
logs/
```

If OpenTTD is installed later, rerun bootstrap with `-OpenTtdSource`; it updates only `.runtime/openttd` and the bundled GameScript/AI packages.

## Local configuration

Bootstrap copies these tracked examples if their local counterparts are absent:

- `.config/arena.example.yaml` → `.config/arena.local.yaml`
- `.config/providers.example.yaml` → `.config/providers.local.yaml`

`arena.local.yaml` is a closed, versioned configuration shape. Its repository-relative paths keep the runtime contained in this checkout:

```yaml
config_version: 1

runtime:
  root: .runtime
  runs: .runtime/runs
  recordings: .runtime/recordings

openttd:
  executable: .runtime/openttd/openttd.exe
  server_config: .runtime/openttd/server.cfg
  spectator_config: .runtime/openttd/spectator.cfg
  admin_port: 3977
  admin_credential_ref: credman:OpenTTDModelArena/AdminPort

obs:
  host: 127.0.0.1
  port: 4455
  credential_ref: credman:OpenTTDModelArena/OBS
  scene_collection: OpenTTD Model Arena
  executable: obs64

network:
  bind_address: 127.0.0.1

logging:
  level: Information
  redact_secrets: true

doctor:
  minimum_free_disk_gb: 20
```

Keep the runtime and OpenTTD paths below the repository root. The loader rejects unknown fields, non-loopback control addresses, path traversal, raw secret-shaped fields, and credential values that are not managed `credman:OpenTTDModelArena/<name>` references. The JSON schema equivalents and valid/invalid fixtures live under [`schemas/setup/`](schemas/setup/).

`providers.local.yaml` can remain empty for Phase 01:

```yaml
config_version: 1
providers: {}
```

The default `obs.executable: obs64` relies on OBS being on `PATH`. If the standard OBS installer did not add it, set the executable explicitly before running doctor:

```yaml
obs:
  executable: 'C:\Program Files\obs-studio\bin\64bit\obs64.exe'
```

A later live provider entry contains metadata plus `credential_ref`; it never contains an API key:

```yaml
providers:
  deepseek:
    type: deepseek
    base_url: https://api.deepseek.com/
    model: deepseek-chat
    credential_ref: credman:OpenTTDModelArena/DeepSeek
    timeout_seconds: 45
    maximum_transient_retries: 1
```

## Credentials

The CLI accepts a credential value only through an interactive hidden prompt. It never accepts a credential value through an argument, standard input, config file, environment file, log, or manifest. Managed targets are deliberately scoped to `OpenTTDModelArena/`.

```powershell
pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/OBS
pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/AdminPort
pwsh ./scripts/ttd-arena.ps1 credentials list
pwsh ./scripts/ttd-arena.ps1 credentials remove OpenTTDModelArena/OBS
```

To check that a configured provider reference exists without issuing a paid provider request:

```powershell
pwsh ./scripts/ttd-arena.ps1 credentials test deepseek
```

You can test a dedicated managed target directly, for example `credentials test OpenTTDModelArena/OBS`. `credentials test` verifies only Credential Manager metadata. `providers test deepseek` additionally validates the local adapter metadata and credential reference, but deliberately makes no remote provider request. The explicit Phase 05/06 provider road smoke is the only command in this guide that makes a DeepSeek request.

`OpenTTDModelArena/AdminPort` is a separate OpenTTD-only credential. Enter it through the same hidden prompt; do not reuse OBS or a provider password. OpenTTD's generated `secrets.cfg` policy requires 1–31 printable ASCII characters with no spaces, `=`, `;`, or `#`. Bootstrap migrates a pre-Phase-03 local configuration by inserting the reference only; it never writes or reads the credential value.

OpenTTD 15 and later use the native secure PAKE login automatically. The bridge permits the older password-only flow only for a detected OpenTTD 14.x executable; there is no configuration switch that can downgrade a 15+ server.

## OBS setup

Create a dedicated OBS scene collection and enable OBS WebSocket authentication on `127.0.0.1:4455`. Store its dedicated password in `OpenTTDModelArena/OBS`; do not reuse a provider credential.

Bootstrap writes a repository-owned scene checklist at:

```text
.runtime/obs/Arena-Scene-Collection.template.json
```

It defines the required scenes:

- `Arena - Starting`
- `Arena - Wide`
- `Arena - Medium`
- `Arena - Close`
- `Arena - Results`
- `Arena - Failure`

It also defines the required source names and intended kinds:

- `Arena-Wide`, `Arena-Medium`, `Arena-Close` — Window Capture
- `Arena-Sidebar` — Browser Source
- `Arena-Results` — Browser Source or Media Source
- `Arena-Audio` — optional Application Audio Capture

The JSON file is a generated Arena template/checklist, not a claim that it has altered your OBS profile. Recreate the named scenes and sources in the dedicated collection, then run doctor. Doctor uses the OBS WebSocket 5.x handshake, authenticates with the dedicated Credential Manager value, and inspects the active scene/source names without changing a scene, source, recording, or profile.

## Run doctor

Use the wrapper from the repository root:

```powershell
pwsh ./scripts/ttd-arena.ps1 doctor --verbose
```

For automation, request the versioned structured report and check `$LASTEXITCODE`:

```powershell
pwsh ./scripts/ttd-arena.ps1 doctor --json
if ($LASTEXITCODE -ne 0) {
    throw "Arena doctor found blocking setup failures."
}
```

Phase 01 doctor blocks on:

- unsupported Windows host, PowerShell, .NET, Node, OpenTTD, or OBS version;
- unreadable isolated OpenTTD executable/configuration;
- missing ArenaGS/ModelProxyAI/package manifest or generated OBS template;
- missing or non-writable runtime, run, or recording directories;
- unavailable loopback OpenTTD game-server or AdminPort listener;
- missing Credential Manager references;
- failed OBS WebSocket authentication or missing required OBS scene/source names;
- insufficient recording disk space; and
- invalid local configuration.

Each blocking item includes a remediation. Warnings identify intentional phase boundaries: scenario schemas arrive in Phase 07, authenticated AdminPort handshake is exercised by the live Phase 03 bridge smoke, and live provider authentication arrives in Phase 05. A warning is not a successful benchmark check.

### Verify predictable failure handling

Run these checks only after the normal doctor command passes on the dedicated Windows host. They are deliberately reversible and never require placing a real secret in a file. Before editing, make a temporary copy of the local configuration:

```powershell
Copy-Item .config/arena.local.yaml .config/arena.local.yaml.phase01-backup
```

Use this helper after each temporary change. It requires doctor to return exit code `2` and to identify the expected blocking check rather than merely failing for an unrelated reason:

```powershell
function Assert-DoctorBlock([string]$ExpectedCheckId) {
    $reportJson = & ./scripts/ttd-arena.ps1 doctor --json
    if ($LASTEXITCODE -ne 2) {
        throw "Expected doctor exit code 2, received $LASTEXITCODE."
    }

    $report = $reportJson | ConvertFrom-Json
    $expected = $report.checks | Where-Object {
        $_.id -eq $ExpectedCheckId -and $_.status -eq "blocking_failure"
    }
    if ($null -eq $expected) {
        throw "Doctor did not report the expected blocking check: $ExpectedCheckId"
    }
}
```

Perform the following one at a time, restoring the changed state before continuing:

1. **OpenTTD:** rename `.runtime/openttd/openttd.exe` to `openttd.exe.phase01-disabled`, run `Assert-DoctorBlock "openttd.files"`, then rename it back.
2. **OBS:** temporarily set `obs.port` in `arena.local.yaml` to a different unused loopback port, run `Assert-DoctorBlock "obs.websocket"`, then restore the original port.
3. **Credential:** temporarily change `obs.credential_ref` to the valid but deliberately absent `credman:OpenTTDModelArena/Phase01DoctorMissing`, run `Assert-DoctorBlock "credential.obs"`, then restore the original reference. Do not remove or expose the real credential.
4. **Ports:** while OpenTTD is stopped, reserve both generated ports one at a time and verify their specific checks. Substitute the configured `openttd.admin_port` if it is not `3977`:

   ```powershell
   foreach ($case in @(
       @{ Port = 3979; Check = "network.game-port" },
       @{ Port = 3977; Check = "network.admin-port" }
   )) {
       $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $case.Port)
       $listener.Start()
       try {
           Assert-DoctorBlock $case.Check
       }
       finally {
           $listener.Stop()
       }
   }
   ```

Restore the saved configuration and remove the temporary backup, then rerun the normal doctor command and require exit code `0`:

```powershell
Move-Item -Force .config/arena.local.yaml.phase01-backup .config/arena.local.yaml
pwsh ./scripts/ttd-arena.ps1 doctor --json
if ($LASTEXITCODE -ne 0) {
    throw "Arena doctor did not return to a healthy state."
}
```

## Run the provider-free Phase 02 smoke

After bootstrap has copied a valid OpenTTD installation, run the unattended lifecycle smoke from the repository root:

```powershell
pwsh ./scripts/smoke.ps1 -DurationSeconds 10
```

The direct CLI form is equivalent:

```powershell
pwsh ./scripts/ttd-arena.ps1 smoke --duration-seconds 10
```

This command does not use a provider, provider credential, OBS, OBS WebSocket, or AdminPort. It needs the isolated OpenTTD runtime and an available loopback game port. Therefore an OBS-specific `doctor` block does not prevent this provider-free Phase 02 verification, although all doctor blocks must be resolved before later recording or benchmark phases.

The smoke command creates a unique directory below `.runtime/runs/`. It copies a cached fixed starting save into `input/`, starts an isolated dedicated server, verifies the ArenaGS/ModelProxyAI readiness markers, opens three spectator windows, saves a checkpoint, advances briefly, finalizes a save, and shuts down every process it launched. It writes `lifecycle.ndjson`, `run-result.json`, and separate component logs. Any OpenTTD-generated `secrets.cfg` is removed before results are indexed.

While it runs, you can visually validate three briefly visible spectator windows with titles beginning:

- `Arena - Wide`
- `Arena - Medium`
- `Arena - Close`

The dedicated server has no player window. Phase 02 does not yet alter OBS or create a recording, so the absence of an OBS scene switch or video is expected. When the command completes, all three spectator windows should close.

Verify the finished artifacts and cleanup:

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

Get-Content (Join-Path $run.FullName 'lifecycle.ndjson')
$result = Get-Content (Join-Path $run.FullName 'run-result.json') -Raw | ConvertFrom-Json
$result.final_state
$result.exit_reason
Get-ChildItem $run.FullName -Recurse -Filter '*.sav' | Select-Object FullName, Length
Get-ChildItem $run.FullName -Recurse -Filter 'secrets.cfg'
Get-Process openttd -ErrorAction SilentlyContinue
Get-NetTCPConnection -State Listen -LocalPort 3979 -ErrorAction SilentlyContinue
```

Expect `completed` for both result fields, at least the copied starting, checkpoint, and final save artifacts, and no output from the final three cleanup checks. For the complete requirement map, cancellation check, and five-sequential-run procedure, see [Phase 02 acceptance evidence](docs/phase-02-acceptance.md).

## Run the provider-free Phase 03 bridge smoke

After bootstrap has migrated the local configuration and you have created the dedicated AdminPort credential, run the real authenticated protocol proof:

```powershell
pwsh ./scripts/bridge-smoke.ps1
```

The equivalent direct CLI command is:

```powershell
pwsh ./scripts/ttd-arena.ps1 bridge-smoke
```

This starts a temporary isolated dedicated server, writes its AdminPort password only to the run-local OpenTTD `secrets.cfg`, and then proves the version gate, shared valid/invalid protocol fixtures, capabilities, heartbeat, stale-run handling, pause/resume, deferred typed boundaries, idempotency, 10 KiB chunk transfer, and finalization. It deletes that secret file before writing `bridge-result.json`. The bridge has been live-verified against OpenTTD 14.1 and 15.3; rerun this command after any OpenTTD upgrade.

Expect:

```text
Phase 03 bridge smoke completed.
```

The bridge smoke has no spectator window, OBS scene switch, or recording by design. For visual validation, run the separate Phase 02 smoke and observe its three spectator windows. Inspect the latest `bridge-*` run with the commands in [Phase 03 acceptance evidence](docs/phase-03-acceptance.md).

## Verify Phases 04–06

After the Phase 03 bridge smoke passes, the provider-free checks exercise authoritative observations, replayed decisions, deterministic road construction, fleet expansion, recovery, and save/load at every road-project stage:

```powershell
pwsh ./scripts/ttd-arena.ps1 observation-smoke
pwsh ./scripts/ttd-arena.ps1 road-smoke
pwsh ./scripts/ttd-arena.ps1 fleet-smoke
pwsh ./scripts/ttd-arena.ps1 road-budget-smoke

$stages = 'proposed', 'validating', 'surveying', 'building_infrastructure',
    'buying_vehicles', 'configuring_orders', 'verifying'
foreach ($stage in $stages) {
    pwsh ./scripts/ttd-arena.ps1 road-save-load-smoke --stage $stage
    if ($LASTEXITCODE -ne 0) { throw "Save/load smoke failed at $stage." }
}

# Phase 06 repeatability gate: twenty isolated replay route runs.
pwsh ./scripts/road-soak.ps1
```

Each command creates a new `bridge-*` run under `.runtime/runs/`; inspect `bridge-result.json`, `observations.ndjson`, `game-events.ndjson`, `decisions.ndjson`, `actions.ndjson`, and `provider-usage.ndjson` in that directory. To replay the latest recorded observation chain without launching OpenTTD:

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
pwsh ./scripts/ttd-arena.ps1 observations replay $run.FullName
```

For the artifact expectations and the opt-in DeepSeek verification, see [Phase 04–06 verification](docs/phase-04-06-verification.md). The road smokes use a dedicated server and have no spectator or recording window; the Phase 02 smoke remains the available visual lifecycle check until recording and camera phases.

## Build and test

Run the canonical repository quality gate on the supported Windows host:

```powershell
pwsh ./scripts/test-all.ps1
```

It runs schema, formatting, architecture, secret, OpenTTD package, .NET unit, CLI version, and overlay test/build checks. CI additionally parses every setup PowerShell script on Windows. `test-all.ps1` does not start OpenTTD, connect to OBS, or call a provider; run the explicit Phase 02–06 smokes above for live OpenTTD evidence.

## Cleanup

The default cleanup is conservative:

```powershell
pwsh ./scripts/clean.ps1
```

It removes only disposable build outputs, `.runtime/cache`, `.runtime/temp`, `.tmp`, and the overlay distribution output. It refuses to recurse through a symbolic link or junction, and preserves local configuration, credentials, `.runtime/runs`, `.runtime/recordings`, `artifacts`, and `logs`. Add `-IncludeDependencies` only when you explicitly want to remove Node dependency directories.

## Current boundary

Phases 01–06 make a Windows host repeatable, diagnosable, capable of provider-free isolated lifecycle/observation/road checks, and able to exchange authenticated versioned messages with ArenaGS. They also provide an opt-in live DeepSeek road proof using the same typed provider and action contracts. The following commands are not implemented yet and must not be treated as benchmark verification:

- `ttd-arena run`
- `ttd-arena verify-run`
- OBS scene switching or recording

Those operations remain in their corresponding later phase documents.
