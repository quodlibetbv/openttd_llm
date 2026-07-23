[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ArenaArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$arenaExitCode = 1
Push-Location $repositoryRoot
try {
    & dotnet run --project src/Arena.Cli/Arena.Cli.csproj --configuration Debug -- @ArenaArguments
    $arenaExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $arenaExitCode
