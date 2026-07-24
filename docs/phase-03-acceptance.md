# Phase 03 Acceptance Evidence

Phase 03 implements the AdminPort bridge objective in [`phases/PHASE-03-ADMINPORT-BRIDGE.md`](../phases/PHASE-03-ADMINPORT-BRIDGE.md). It is a provider-free protocol proof, not a benchmark: no model, paid provider, rich observation contract, route construction, score, recording, or overlay is involved.

| Requirement | Evidence in the repository | Verification |
|---|---|---|
| Authenticated loopback client | [`AdminPortBridgeClient`](../src/Arena.AdminProtocol/AdminPortBridgeClient.cs) speaks OpenTTD Admin protocol v3, requires loopback, uses OpenTTD 15+ secure PAKE/encrypted records, and permits the 14.x password flow only after an executable-version decision. It subscribes only to GameScript updates and uses ping/reconnect bounds. | `AdminPortProtocolTests` record-authentication, explicit-fallback, incompatible-version, timeout, and reconnect tests; live `bridge-smoke` reconnect proof on OpenTTD 14.1 and 15.3 |
| Closed versioned envelope | [`ProtocolEnvelopeValidator`](../src/Arena.Contracts/ProtocolContracts.cs) and [`protocol-envelope.v1.json`](../schemas/protocol/protocol-envelope.v1.json) reject unknown fields, unsupported versions/types, invalid identifiers, non-integer values, and oversized payloads. | shared `phase03-adminport-fixtures.v1.json`, `AdminPortProtocolTests` |
| Cross-language dispatcher | [`ArenaGS`](../openttd/game/ArenaGS/main.nut) validates the same v1 envelope, binds a run at `hello`, rejects stale runs, and returns typed correlated results. | `bridge-smoke` checks `hello-capabilities`, `version-gate`, `stale-run`, heartbeat, control results, and finalize |
| Chunk transfer bounds and integrity | [`ProtocolChunking`](../src/Arena.AdminProtocol/ProtocolChunking.cs), [`adminport-chunk.v1.json`](../schemas/protocol/adminport-chunk.v1.json), and ArenaGS limit chunk count/data, preserve correlation/idempotency metadata, verify Adler-32, and expire incomplete transfers. | 10 KiB C# reassembly test; deterministic checksum/expiry test; live inbound/outbound 10 KiB and incomplete-transfer timeout `bridge-smoke` checks |
| Idempotent control commands | ArenaGS persists a bounded result ledger through save/load and returns the original response for a matching retry. | duplicate pause check in `bridge-smoke`; protocol test coverage |
| Safe credential handling | [`AdminPortSecretFile`](../src/Arena.Orchestrator/AdminPortSecretFile.cs) writes a temporary run-local OpenTTD `secrets.cfg`, validates the safe password subset, clears in-memory buffers, and deletes the file before result reporting. | `doctor` credential check, `bridge-smoke` cleanup inspection |
| Classified failures | [`ArenaErrorCodes`](../src/Arena.Contracts/ErrorCodes.cs) and [`docs/error-codes.md`](error-codes.md) define AdminPort, chunk, correlation, authentication, timeout, and compatibility failure codes. | validator, fake-server, and bridge-smoke result checks |

## Windows migration from Phase 02

Run these commands after the Phase 03 change is merged. Bootstrap preserves an existing Phase 02 local configuration and inserts only the non-secret `openttd.admin_credential_ref` line when it is absent.

```powershell
git fetch origin
git switch main
git pull --ff-only

pwsh ./scripts/bootstrap.ps1 -OpenTtdSource "C:\Program Files\OpenTTD"
Get-Content .config/arena.local.yaml
```

Confirm that the `openttd` section contains this reference, not a password:

```yaml
admin_credential_ref: credman:OpenTTDModelArena/AdminPort
```

Create the dedicated AdminPort password through the hidden prompt. It must be unique to AdminPort, 1–31 printable ASCII characters, and contain no spaces, `=`, `;`, or `#`. Do not reuse the OBS or provider password.

```powershell
pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/AdminPort
pwsh ./scripts/ttd-arena.ps1 credentials test OpenTTDModelArena/AdminPort
pwsh ./scripts/ttd-arena.ps1 doctor --verbose
```

`doctor` now reports `credential.adminport` as a pass when the dedicated value has the required OpenTTD-safe shape. Its `adminport-handshake` item remains a warning by design: OpenTTD is not running during doctor, so the real authenticated exchange belongs to the next command.

OpenTTD 15+ uses its native secure PAKE login automatically. OpenTTD 14.x uses a narrowly scoped compatibility path selected from the installed executable version; do not attempt to enable legacy login for a newer server.

## Run the live bridge smoke

Run this on the supported Windows host after bootstrap and the dedicated credential are ready:

```powershell
pwsh ./scripts/bridge-smoke.ps1
```

The direct CLI form is equivalent:

```powershell
pwsh ./scripts/ttd-arena.ps1 bridge-smoke `
    --startup-timeout-seconds 60 `
    --request-timeout-seconds 20 `
    --shutdown-timeout-seconds 15
```

Expected terminal result:

```text
Phase 03 bridge smoke completed.
```

The command starts one isolated dedicated server, authenticates over loopback AdminPort, validates the capability/heartbeat/version/stale-run/control boundaries, proves duplicate idempotency, deliberately reconnects, transfers 10 KiB in both directions, expires an incomplete chunk transfer, and finalizes the bridge. It does not contact OBS or a provider. An OBS-specific doctor block therefore does not prevent this protocol verification, although OBS still must be configured before later recording phases. Live acceptance has covered OpenTTD 14.1 and 15.3.

## Inspect the result and cleanup

```powershell
$run = Get-ChildItem .runtime/runs -Directory |
    Where-Object Name -like 'bridge-*' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

$result = Get-Content (Join-Path $run.FullName 'bridge-result.json') -Raw | ConvertFrom-Json
$result.succeeded
$result.checks | Format-Table id, passed, error_code, detail -AutoSize
Get-Content (Join-Path $run.FullName 'lifecycle.ndjson')
Get-ChildItem $run.FullName -Recurse -Filter 'secrets.cfg'
Get-Process openttd -ErrorAction SilentlyContinue
Get-NetTCPConnection -State Listen -LocalPort 3977 -ErrorAction SilentlyContinue
```

Expect `$result.succeeded` to be `True`, every check to be passed, a terminal `completed` lifecycle state, and no output from the final three cleanup checks. The live command creates no spectator window, OBS scene switch, or recording: it intentionally runs a dedicated server to validate the protocol boundary. For visual validation of the existing process lifecycle, run the separate Phase 02 smoke and observe the three spectator windows described in [Phase 02 acceptance evidence](phase-02-acceptance.md).

## Repetition check

Run five bridge checks sequentially before treating a Windows host as Phase 03-ready:

```powershell
1..5 | ForEach-Object {
    pwsh ./scripts/bridge-smoke.ps1
    if ($LASTEXITCODE -ne 0) {
        throw "Bridge smoke run $_ failed with exit code $LASTEXITCODE."
    }
}
```

Keep the bridge run directories for diagnosis. They contain lifecycle data, component logs, and a redacted `bridge-result.json`; a credential value or `secrets.cfg` must never remain in the indexed result.
