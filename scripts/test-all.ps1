[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repositoryRoot
try {
    npm ci
    npm run verify

    dotnet restore OpenTTD.ModelArena.sln
    dotnet format OpenTTD.ModelArena.sln --verify-no-changes --no-restore
    dotnet build OpenTTD.ModelArena.sln -c Debug --no-restore
    dotnet test OpenTTD.ModelArena.sln -c Debug --no-build

    npm ci --prefix src/Arena.Overlay
    npm test --prefix src/Arena.Overlay
}
finally {
    Pop-Location
}
