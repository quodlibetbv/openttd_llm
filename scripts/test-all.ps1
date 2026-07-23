[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repositoryRoot
try {
    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("ci")
    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("run", "verify")

    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("restore", "OpenTTD.ModelArena.sln")
    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("format", "OpenTTD.ModelArena.sln", "--verify-no-changes", "--no-restore")
    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("build", "OpenTTD.ModelArena.sln", "-c", "Debug", "--no-restore")
    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("test", "OpenTTD.ModelArena.sln", "-c", "Debug", "--no-build")
    Invoke-ArenaCommand -FilePath "dotnet" -ArgumentList @("run", "--project", "src/Arena.Cli/Arena.Cli.csproj", "-c", "Debug", "--no-build", "--", "--version")

    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("ci", "--prefix", "src/Arena.Overlay")
    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("test", "--prefix", "src/Arena.Overlay")
    Invoke-ArenaCommand -FilePath "npm" -ArgumentList @("run", "build", "--prefix", "src/Arena.Overlay")
}
finally {
    Pop-Location
}
