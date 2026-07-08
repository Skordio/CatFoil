<#
.SYNOPSIS
    Builds the portable CatFoil single-file EXE.
.DESCRIPTION
    Publishes the self-contained, single-file CatFoil.exe and copies it out as
    dist\CatFoil-<version>-portable.exe — a no-install download that runs directly
    (settings still live in %APPDATA%\CatFoil). The version comes from <Version>
    in CatFoil.csproj.

    This is the same payload the installer wraps; to produce both formats from a
    single publish in one shot, use build-release.ps1 instead.

    Close any running CatFoil first — the self-contained EXE is locked while it runs.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $root 'CatFoil.csproj'
$pubDir = Join-Path $root 'dist\publish'

Assert-CatFoilNotRunning
$ver = Get-CatFoilVersion $proj
Write-Host "==> Building CatFoil $ver portable" -ForegroundColor Cyan

Invoke-CatFoilPublish -Proj $proj -PubDir $pubDir | Out-Null
$portable = New-CatFoilPortable -PubDir $pubDir -OutPath (Join-Path $root "dist\CatFoil-$ver-portable.exe")

Write-Host "==> Done: $portable" -ForegroundColor Green
