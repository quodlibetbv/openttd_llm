[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repositoryRoot
try {
    npm run check:format
    dotnet format OpenTTD.ModelArena.sln --verify-no-changes
}
finally {
    Pop-Location
}
