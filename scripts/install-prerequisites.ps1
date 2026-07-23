[CmdletBinding()]
param(
    [switch]$Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
if (-not $isWindowsHost) {
    throw "Prerequisite installation is supported only on the Windows 11 target host."
}

$packages = @(
    [PSCustomObject]@{ Name = "Git for Windows"; Id = "Git.Git"; Manual = "https://git-scm.com/download/win" },
    [PSCustomObject]@{ Name = "PowerShell 7"; Id = "Microsoft.PowerShell"; Manual = "https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows" },
    [PSCustomObject]@{ Name = ".NET 8 SDK"; Id = "Microsoft.DotNet.SDK.8"; Manual = "https://dotnet.microsoft.com/download/dotnet/8.0" },
    [PSCustomObject]@{ Name = "Node.js LTS"; Id = "OpenJS.NodeJS.LTS"; Manual = "https://nodejs.org/" },
    [PSCustomObject]@{ Name = "OpenTTD"; Id = "OpenTTD.OpenTTD"; Manual = "https://www.openttd.org/downloads/openttd-releases/latest" },
    [PSCustomObject]@{ Name = "OBS Studio"; Id = "OBSProject.OBSStudio"; Manual = "https://obsproject.com/download" }
)

if (-not $Install) {
    Write-Host "This script does not install software unless -Install is supplied."
    Write-Host "Manual installation links:"
    foreach ($package in $packages) {
        Write-Host "  $($package.Name): $($package.Manual)"
    }

    Write-Host "To use winget after reviewing the package list, run:"
    Write-Host "  pwsh ./scripts/install-prerequisites.ps1 -Install"
    return
}

if ($null -eq (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget was not found. Install the prerequisites manually using the links printed by this script without -Install."
}

foreach ($package in $packages) {
    Write-Host "Installing $($package.Name) with winget..."
    & winget install --id $package.Id --exact --source winget --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget could not install $($package.Name). Use its documented manual installer: $($package.Manual)"
    }
}

Write-Host "Prerequisite installation completed. Start a new PowerShell 7 session, then run pwsh ./scripts/bootstrap.ps1."
