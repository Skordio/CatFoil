<#
.SYNOPSIS
  Builds the CatFoil installer.
.DESCRIPTION
  1. Publishes a self-contained single-file CatFoil.exe (offline payload).
  2. Compiles installer\CatFoil.iss with Inno Setup (ISCC.exe).
  Output: dist\CatFoil-Setup-<version>.exe

  The version comes from <Version> in CatFoil.csproj, so the EXE metadata and
  the installer filename always match. Close any running CatFoil first — the
  self-contained EXE is locked while it runs.

  To also produce the portable download from the same publish, use
  build-release.ps1 instead.

  Requires Inno Setup 6:  winget install JRSoftware.InnoSetup
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
Write-Host "==> Building CatFoil $ver installer" -ForegroundColor Cyan

Invoke-CatFoilPublish -Proj $proj -PubDir $pubDir | Out-Null
$setup = New-CatFoilInstaller -Iscc $iscc -Iss $iss -Ver $ver

Write-Host "==> Done: $setup" -ForegroundColor Green
