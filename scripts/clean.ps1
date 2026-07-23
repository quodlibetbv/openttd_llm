[CmdletBinding()]
param(
    [switch]$IncludeDependencies
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$rootWithSeparator = if ($repositoryRoot.EndsWith([IO.Path]::DirectorySeparatorChar)) {
    $repositoryRoot
}
else {
    $repositoryRoot + [IO.Path]::DirectorySeparatorChar
}

function Remove-ArenaDisposablePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $target = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RelativePath))
    if (-not $target.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside this repository."
    }

    if (Test-Path -LiteralPath $target) {
        $relativeTarget = [IO.Path]::GetRelativePath($repositoryRoot, $target)
        $currentPath = $repositoryRoot
        foreach ($segment in ($relativeTarget -split '[\\/]')) {
            if ([string]::IsNullOrWhiteSpace($segment)) {
                continue
            }

            $currentPath = Join-Path $currentPath $segment
            $attributes = [IO.File]::GetAttributes($currentPath)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to clean through a symbolic link or junction: $RelativePath"
            }
        }

        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed $RelativePath"
    }
}

Push-Location $repositoryRoot
try {
    if ($null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        & dotnet clean OpenTTD.ModelArena.sln -c Debug
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet clean failed. Resolve the build-tool error before retrying."
        }
    }

    foreach ($path in @(
        ".runtime/cache",
        ".runtime/temp",
        ".tmp",
        "src/Arena.Overlay/dist"
    )) {
        Remove-ArenaDisposablePath -RelativePath $path
    }

    if ($IncludeDependencies) {
        foreach ($path in @("node_modules", "src/Arena.Overlay/node_modules")) {
            Remove-ArenaDisposablePath -RelativePath $path
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Clean leaves .config, .runtime/runs, .runtime/recordings, artifacts, and logs untouched."
