[CmdletBinding()]
param(
    [string]$OpenTtdSource,
    [string]$ConfigPath = ".config/arena.local.yaml",
    [string]$ProvidersConfigPath = ".config/providers.local.yaml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-SupportedBootstrapHost {
    $isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    if (-not $isWindowsHost) {
        throw "OpenTTD Model Arena bootstrap supports Windows 11 64-bit hosts only. Use doctor on the target Windows host."
    }

    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 22000) {
        throw "OpenTTD Model Arena bootstrap requires Windows 11 64-bit (build 22000 or later)."
    }

    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw "Run this script with PowerShell 7 or later: pwsh ./scripts/bootstrap.ps1"
    }
}

function Invoke-ArenaCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command '$FilePath' failed with exit code $exitCode."
    }
}

Assert-SupportedBootstrapHost

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repositoryRoot
try {
    foreach ($command in @("dotnet", "node", "npm")) {
        if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Missing required command '$command'. Run pwsh ./scripts/install-prerequisites.ps1 for documented installation options."
        }
    }

    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("ci")
    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("restore", "OpenTTD.ModelArena.sln")
    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("build", "OpenTTD.ModelArena.sln", "-c", "Debug", "--no-restore")
    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("ci", "--prefix", "src/Arena.Overlay")
    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("run", "build", "--prefix", "src/Arena.Overlay")

    $cliArguments = @(
        "run",
        "--project",
        "src/Arena.Cli/Arena.Cli.csproj",
        "--configuration",
        "Debug",
        "--no-build",
        "--no-restore",
        "--",
        "bootstrap",
        "--config",
        $ConfigPath,
        "--providers-config",
        $ProvidersConfigPath
    )
    if (-not [string]::IsNullOrWhiteSpace($OpenTtdSource)) {
        $cliArguments += "--openttd-source"
        $cliArguments += $OpenTtdSource
    }

    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList $cliArguments

    Write-Host "Bootstrap is complete. Local configuration and credentials were preserved if they already existed."
    Write-Host "Next steps:"
    Write-Host "  1. Configure OBS WebSocket on 127.0.0.1 and use .runtime/obs/Arena-Scene-Collection.template.json as the dedicated scene checklist."
    Write-Host "  2. Run: pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/OBS"
    Write-Host "  3. Run: pwsh ./scripts/ttd-arena.ps1 credentials set OpenTTDModelArena/AdminPort"
    Write-Host "  4. Run: pwsh ./scripts/ttd-arena.ps1 doctor --verbose"
    Write-Host "  5. Run: pwsh ./scripts/bridge-smoke.ps1"
}
finally {
    Pop-Location
}
