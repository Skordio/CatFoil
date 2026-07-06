#Requires -Version 5
<#
.SYNOPSIS
  Builds the CatFoil per-user installer.
.DESCRIPTION
  1. Publishes a self-contained single-file CatFoil.exe (offline payload).
  2. Compiles installer\CatFoil.iss with Inno Setup (ISCC.exe).
  Output: dist\CatFoil-Setup-<version>.exe

  The version comes from <Version> in CatFoil.csproj, so the EXE metadata and
  the installer filename always match. Close any running CatFoil first — the
  self-contained EXE is locked while it runs.

  Requires Inno Setup 6:  winget install JRSoftware.InnoSetup
#>
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$proj   = Join-Path $root 'CatFoil.csproj'
$iss    = Join-Path $root 'installer\CatFoil.iss'
$pubDir = Join-Path $root 'dist\publish'

# Version from the csproj so the EXE and installer always agree.
# @(...) forces array semantics so [0] returns the full string, not its first char.
[xml]$xml = Get-Content $proj
$ver = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $ver) { throw "No <Version> found in $proj" }
Write-Host "==> Building CatFoil $ver installer" -ForegroundColor Cyan

# Locate the Inno compiler up front so we fail fast before the long publish.
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  throw "Inno Setup 6 not found (ISCC.exe). Install it with:  winget install JRSoftware.InnoSetup"
}

# Clean the previous payload, then publish the self-contained single-file EXE.
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
Write-Host "==> dotnet publish (self-contained, single file)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o $pubDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Compile the installer.
Write-Host "==> Compiling installer with Inno Setup..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$ver" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Join-Path $root "dist\CatFoil-Setup-$ver.exe"
Write-Host "==> Done: $setup" -ForegroundColor Green
