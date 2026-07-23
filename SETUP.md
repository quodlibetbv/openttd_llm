# Windows Setup Guide

> Status: this guide describes the target Phase 01-and-later Windows workflow. The current Phase 00 baseline provides solution, contract, fixture, and quality-gate scaffolding only. `bootstrap`, `doctor`, credential commands, isolated runtime generation, OBS configuration validation, and smoke runs are not implemented yet.

## 1. Supported environment

Recommended development and production host:

- Windows 11 64-bit is the supported production baseline. Windows 10 is not supported until a later ADR and CI matrix approve it.
- Hardware virtualization is optional and not required for the core product.
- A dedicated GPU is recommended for high-resolution OBS recording but is not required for headless benchmark runs.
- At least 16 GB RAM, 20 GB free disk space, and a modern four-core CPU.
- A stable network connection for remote model providers.

Use a dedicated Windows user profile for unattended production recording. Disable sleep and automatic display timeout during runs.

## 2. Required software

Install the following from their official distribution channels:

1. Git for Windows: <https://git-scm.com/download/win>
2. PowerShell 7: <https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows>
3. .NET 8 SDK: <https://dotnet.microsoft.com/download/dotnet/8.0>
4. Node.js LTS: <https://nodejs.org/>
5. OpenTTD: <https://www.openttd.org/downloads/openttd-releases/latest>
6. OBS Studio: <https://obsproject.com/download>
7. Optional development editor: <https://code.visualstudio.com/>

The repository should eventually provide `scripts/install-prerequisites.ps1` using `winget`. Until that script exists, install manually and verify that `git`, `pwsh`, `dotnet`, `node`, `npm`, `openttd`, and `obs64` are discoverable or configured through repository settings.

## 3. Clone and bootstrap

```powershell
git clone <repository-url> openttd-model-arena
cd openttd-model-arena
pwsh ./scripts/bootstrap.ps1
```

`bootstrap.ps1` must be idempotent and perform these operations:

- Verify supported PowerShell and Windows versions.
- Restore .NET tools and NuGet packages.
- Install Node dependencies with the repository lock file.
- Create runtime, artifacts, logs, and local configuration directories.
- Copy example configuration files without overwriting existing local files.
- Build development binaries.
- Install or link Arena GameScript and ModelProxyAI into the isolated OpenTTD runtime.
- Print the next required credential and OBS steps.

Expected repository-local directories:

```text
.runtime/
  openttd/
  obs/
  runs/
  cache/
  temp/
.config/
  arena.local.yaml
  providers.local.yaml
artifacts/
logs/
```

These directories must be excluded from source control where they may contain machine-specific state or generated artifacts.

## 4. Isolated OpenTTD runtime

Do not depend on the user’s normal OpenTTD profile. The arena must launch OpenTTD with a repository-controlled configuration and data location.

The bootstrap process should copy or reference:

```text
.runtime/openttd/
  openttd.exe
  openttd.cfg
  baseset/
  game/ArenaGS/
  ai/ModelProxyAI/
  content-manifest.json
```

The generated configuration must:

- Use a fixed language, resolution, GUI scale, autosave policy, and simulation settings.
- Enable the dedicated server and required local networking.
- Enable AdminPort on a loopback interface or private interface selected by configuration.
- Load only benchmark-certified GameScript, AI, base sets, and NewGRFs.
- Disable interactive content downloads during benchmark execution.
- Keep production savegames and logs under the active run directory.

Never edit the user’s global OpenTTD configuration from the installer.

## 5. Provider credentials

Provider secrets must be stored in Windows Credential Manager. Scenario files and provider files contain references, not keys.

Example references:

```yaml
providers:
  deepseek:
    type: deepseek
    base_url: https://api.deepseek.com
    key_ref: credman:OpenTTDModelArena/DeepSeek
```

The CLI should expose credential commands:

```powershell
ttd-arena credentials set OpenTTDModelArena/DeepSeek
ttd-arena credentials test deepseek
ttd-arena credentials list
ttd-arena credentials remove OpenTTDModelArena/DeepSeek
```

The secret value must be entered through a secure prompt, never a command-line argument. It must not appear in process listings, shell history, logs, manifests, crash reports, or overlay messages.

## 6. OBS configuration

OBS Studio 28 and later includes WebSocket control. Configure a dedicated profile and scene collection for the arena.

Required sources:

```text
Arena-Wide          Window Capture
Arena-Medium        Window Capture
Arena-Close         Window Capture
Arena-Sidebar       Browser Source
Arena-Results       Browser Source or Media Source
Arena-Audio         Application Audio Capture, optional
```

Recommended canvas:

```text
Base canvas:   2560 x 1440
Game region:   2048 x 1440
Sidebar:        512 x 1440
Output:        2560 x 1440 at 60 FPS or 30 FPS
```

Configure OBS WebSocket authentication with a dedicated password and store the password in Credential Manager under a separate target. Bind control to localhost. The arena must never reuse a provider credential for OBS.

Create these scenes:

- `Arena - Starting`
- `Arena - Wide`
- `Arena - Medium`
- `Arena - Close`
- `Arena - Results`
- `Arena - Failure`

The orchestrator must be able to connect, inspect sources, switch scenes, update browser-source URLs if necessary, start recording, stop recording, and confirm the final output path.

## 7. Local configuration

Copy the example configuration:

```powershell
Copy-Item .config/arena.example.yaml .config/arena.local.yaml
```

Expected structure:

```yaml
runtime:
  root: .runtime
  runs: .runtime/runs

openttd:
  executable: .runtime/openttd/openttd.exe
  server_config: .runtime/openttd/server.cfg
  spectator_config: .runtime/openttd/spectator.cfg

obs:
  host: 127.0.0.1
  port: 4455
  credential_ref: credman:OpenTTDModelArena/OBS
  scene_collection: OpenTTD Model Arena

network:
  bind_address: 127.0.0.1

logging:
  level: Information
  redact_secrets: true
```

Machine-local paths belong in `arena.local.yaml`. Benchmark semantics do not.

## 8. Build and test

```powershell
dotnet restore
dotnet build -c Debug
dotnet test -c Debug

npm ci --prefix src/Arena.Overlay
npm run build --prefix src/Arena.Overlay
npm test --prefix src/Arena.Overlay
```

The canonical command is:

```powershell
pwsh ./scripts/test-all.ps1
```

It should run formatting checks, static analysis, unit tests, protocol tests, schema tests, Squirrel package validation, overlay tests, and a short smoke run where available.

## 9. Environment diagnostics

Run:

```powershell
ttd-arena doctor --verbose
```

The doctor command must validate:

- Supported Windows, PowerShell, .NET, Node, OpenTTD, and OBS versions.
- Required executables and write permissions.
- GameScript and AI package installation.
- Loopback port availability.
- AdminPort authentication and protocol compatibility.
- OBS WebSocket connectivity, scenes, and required sources.
- Provider credential resolution and optional provider test request.
- Scenario and content manifest hashes.
- Available disk space and recording path.
- Ability to create, launch, pause, save, and terminate a short OpenTTD test run.

Every failed check must include an actionable remediation, not only an exception message.

## 10. First smoke run

```powershell
ttd-arena run `
  --scenario scenarios/smoke-road-v1.yaml `
  --provider replay `
  --model smoke-script `
  --record false `
  --keep-runtime
```

The smoke scenario should last only a few in-game months and use a recorded deterministic decision sequence. It verifies the OpenTTD lifecycle and action executor without consuming provider credits.

Then test the first live provider:

```powershell
ttd-arena run `
  --scenario scenarios/road-profit-v1.yaml `
  --provider deepseek `
  --model <configured-model-id> `
  --key-ref credman:OpenTTDModelArena/DeepSeek `
  --runs 1 `
  --record
```

## 11. Run output validation

A successful development run should contain:

```text
.runtime/runs/<run-id>/
  recording.mp4
  final-save.sav
  score.json
  run-manifest.json
  decisions.ndjson
  observations.ndjson
  actions.ndjson
  game-events.ndjson
  camera-events.ndjson
  provider-usage.ndjson
  component-logs/
  checkpoints/
```

Validate the result with:

```powershell
ttd-arena verify-run .runtime/runs/<run-id>
```

Verification checks schema validity, required files, hashes, timestamps, media duration, score reproducibility, and absence of known secret patterns.

## 12. Production host preparation

Before unattended recording:

- Use a dedicated Windows account.
- Disable sleep, hibernation, and automatic restart during the recording window.
- Reserve sufficient disk space for raw recordings and run artifacts.
- Pin OpenTTD, OBS, runtime, scenario, and content versions.
- Disable pop-up notifications and unrelated overlays.
- Test hardware encoding and audio capture.
- Configure automatic cleanup only for explicitly disposable temporary files.
- Ensure Windows Firewall limits Arena control ports to loopback unless remote operation is intentionally configured.
- Run one complete replay benchmark before spending provider credits.

## 13. Common failure categories

### OpenTTD starts but GameScript is absent

Confirm the isolated runtime contains `game/ArenaGS`, the package metadata is valid, and the generated server configuration selects it.

### AdminPort cannot connect

Check that the server reached ready state, the expected port is bound to loopback, credentials match, and another process has not claimed the port. Do not weaken authentication as a workaround.

### OBS connects but records a blank window

Confirm the spectator client title matches the configured Window Capture source and that the client uses a stable graphics mode. Recreate only the affected source, not the full scene collection.

### Overlay is visible in a browser but not OBS

Confirm the Browser Source points to the run-scoped local URL and includes the per-run token. Verify that the overlay server is bound and that the scene is not using a cached stale source.

### Provider output is repeatedly invalid

Capture the redacted request, raw response metadata, schema errors, and retry outcome. Do not bypass validation or execute partially parsed actions.

### A run cannot be reproduced

Compare OpenTTD version, starting save hash, content manifest, scenario hash, tool-contract version, accepted-action sequence, and random seed. Reproducibility applies to accepted game actions, not necessarily to fresh provider responses.
