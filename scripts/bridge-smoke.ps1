[CmdletBinding()]
param(
    [ValidateRange(5, 300)]
    [int]$StartupTimeoutSeconds = 60,

    [ValidateRange(8, 60)]
    [int]$RequestTimeoutSeconds = 20,

    [ValidateRange(2, 120)]
    [int]$ShutdownTimeoutSeconds = 15,

    [string]$Config,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$arguments = @(
    "bridge-smoke",
    "--startup-timeout-seconds", $StartupTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--request-timeout-seconds", $RequestTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--shutdown-timeout-seconds", $ShutdownTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
)
if (-not [string]::IsNullOrWhiteSpace($Config)) {
    $arguments += @("--config", $Config)
}

if ($Json) {
    $arguments += "--json"
}

& (Join-Path $PSScriptRoot "ttd-arena.ps1") @arguments
exit $LASTEXITCODE
