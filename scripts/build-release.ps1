<#
.SYNOPSIS
    Builds BOTH CatFoil release artifacts from a single publish.
.DESCRIPTION
    The per-release command. Publishes the self-contained single-file EXE once,
    then emits both:
        dist\CatFoil-<version>-portable.exe   (the no-install download)
        dist\CatFoil-Setup-<version>.exe      (the installer, also the Store artifact)
    Because both come from the same publish, they are byte-for-byte the same
    binary and can never drift. Version comes from <Version> in CatFoil.csproj.

    Requires Inno Setup 6:  winget install JRSoftware.InnoSetup
    Close any running CatFoil first — the self-contained EXE is locked while it runs.
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $root 'CatFoil.csproj'
$iss    = Join-Path $root 'installer\CatFoil.iss'
$pubDir = Join-Path $root 'dist\publish'

# Fail fast before the long publish: app not running, version known, ISCC present.
Assert-CatFoilNotRunning
$ver  = Get-CatFoilVersion $proj
$iscc = Find-Iscc
Write-Host "==> Building CatFoil $ver release (portable + installer)" -ForegroundColor Cyan

# Publish once, then produce both artifacts from that single payload.
Invoke-CatFoilPublish -Proj $proj -PubDir $pubDir | Out-Null
$portable  = New-CatFoilPortable -PubDir $pubDir -OutPath (Join-Path $root "dist\CatFoil-$ver-portable.exe")
$installer = New-CatFoilInstaller -Iscc $iscc -Iss $iss -Ver $ver

Write-Host ''
Write-Host '==> Done. Both artifacts built from one publish:' -ForegroundColor Green
Write-Host "  portable : $portable"
Write-Host "  installer: $installer"
