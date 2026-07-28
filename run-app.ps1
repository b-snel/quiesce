<#
.SYNOPSIS
    Builds Quiesce and launches it from a temp copy.

.DESCRIPTION
    Quiesce.App is elevated (requireAdministrator), so a running instance locks its own build
    output and an unelevated shell cannot terminate it - which blocks the next build until the
    window is closed by hand. Running from a copy keeps bin\ free, so build-run-build iterations
    never deadlock on a window someone forgot to close.

    The copy carries the catalog with it so catalog resolution behaves like an installed layout.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'src\Quiesce.App\Quiesce.App.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$source = Get-ChildItem (Join-Path $root "src\Quiesce.App\bin\$Configuration") -Recurse -Filter Quiesce.exe |
          Select-Object -First 1
if (-not $source) { throw "Quiesce.exe not found. Build first." }

$stage = Join-Path $env:TEMP 'quiesce-run'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item (Join-Path $source.DirectoryName '*') $stage -Recurse -Force
Copy-Item (Join-Path $root 'catalog') $stage -Recurse -Force

Write-Host "Launching $stage\Quiesce.exe (expect a UAC prompt)"
Start-Process (Join-Path $stage 'Quiesce.exe')
