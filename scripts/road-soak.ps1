[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Count = 20,

    [ValidateRange(5, 300)]
    [int]$StartupTimeoutSeconds = 60,

    [ValidateRange(8, 60)]
    [int]$RequestTimeoutSeconds = 20,

    [ValidateRange(2, 120)]
    [int]$ShutdownTimeoutSeconds = 15,

    [string]$Config
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$arenaScript = Join-Path $PSScriptRoot "ttd-arena.ps1"
for ($runIndex = 1; $runIndex -le $Count; $runIndex++) {
    Write-Host "Starting Phase 06 replay road smoke $runIndex of $Count."
    $arguments = @(
        "road-smoke",
        "--startup-timeout-seconds", $StartupTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "--request-timeout-seconds", $RequestTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture),
        "--shutdown-timeout-seconds", $ShutdownTimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    )
    if (-not [string]::IsNullOrWhiteSpace($Config)) {
        $arguments += @("--config", $Config)
    }

    & $arenaScript @arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        [Console]::Error.WriteLine("Phase 06 replay road smoke $runIndex of $Count failed with exit code $exitCode.")
        exit $exitCode
    }
}

Write-Host "Phase 06 replay road soak completed: $Count successful isolated runs."
