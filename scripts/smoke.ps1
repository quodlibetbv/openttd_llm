[CmdletBinding()]
param(
    [ValidateRange(0, 300)]
    [int]$DurationSeconds = 10,

    [ValidateRange(5, 300)]
    [int]$StartupTimeoutSeconds = 60,

    [ValidateRange(2, 120)]
    [int]$ShutdownTimeoutSeconds = 15,

    [string]$Config,

    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$arguments = @(
    "smoke",
    "--duration-seconds", $DurationSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "--startup-timeout-seconds", $StartupTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
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
