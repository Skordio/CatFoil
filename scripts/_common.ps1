<#
.SYNOPSIS
    Shared helpers for the CatFoil build scripts.

.DESCRIPTION
    Dot-source this from build-portable.ps1, build-installer.ps1, and
    build-release.ps1 so the portable EXE and the installer are always produced
    from a single publish, with identical version/ISCC handling — they can never
    drift apart.

        . (Join-Path $PSScriptRoot '_common.ps1')

    Nothing here runs on load; it only defines functions.
#>

# Read <Version> from the csproj so the EXE metadata and every artifact filename
# agree. @(...) forces array semantics so [0] returns the full string, not its
# first character.
function Get-CatFoilVersion {
    param([Parameter(Mandatory)][string]$Proj)
    [xml]$xml = Get-Content $Proj
    $ver = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $ver) { throw "No <Version> found in $Proj" }
    return $ver
}

# The self-contained EXE is locked while it runs, so a running CatFoil breaks the
# publish. Fail fast with a clear message.
function Assert-CatFoilNotRunning {
    if (Get-Process -Name CatFoil -ErrorAction SilentlyContinue) {
        throw 'CatFoil.exe is running — it locks the build output. Exit it from the tray, then re-run.'
    }
}

# Locate the Inno Setup compiler across the per-machine and per-user install
# locations (winget installs it per-user under %LOCALAPPDATA%).
function Find-Iscc {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
              "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 6 not found (ISCC.exe). Install it with:  winget install JRSoftware.InnoSetup"
    }
    return $iscc
}

# Publish the self-contained, single-file CatFoil.exe (the offline payload that
# both the portable download and the installer are built from). Cleans the target
# first and verifies the EXE landed.
function Invoke-CatFoilPublish {
    param(
        [Parameter(Mandatory)][string]$Proj,
        [Parameter(Mandatory)][string]$PubDir
    )
    if (Test-Path $PubDir) { Remove-Item $PubDir -Recurse -Force }
    Write-Host '==> dotnet publish (self-contained, single file)...' -ForegroundColor Cyan
    dotnet publish $Proj -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $PubDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    $exe = Join-Path $PubDir 'CatFoil.exe'
    if (-not (Test-Path $exe)) { throw "Publish reported success but $exe is missing." }
    return $exe
}

# Copy the published single-file EXE out as the named portable download. Returns
# the output path.
function New-CatFoilPortable {
    param(
        [Parameter(Mandatory)][string]$PubDir,
        [Parameter(Mandatory)][string]$OutPath
    )
    $src = Join-Path $PubDir 'CatFoil.exe'
    if (-not (Test-Path $src)) { throw "Cannot make portable: $src is missing (publish first)." }
    $outDir = Split-Path -Parent $OutPath
    if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    Copy-Item -LiteralPath $src -Destination $OutPath -Force
    if (-not (Test-Path $OutPath)) { throw "Copy reported success but $OutPath is missing." }
    return $OutPath
}

# Wrap the published payload into the Inno Setup installer. The .iss reads its
# payload from dist\publish (populated by Invoke-CatFoilPublish) and writes to
# dist\CatFoil-Setup-<ver>.exe. Returns the output path.
function New-CatFoilInstaller {
    param(
        [Parameter(Mandatory)][string]$Iscc,
        [Parameter(Mandatory)][string]$Iss,
        [Parameter(Mandatory)][string]$Ver
    )
    Write-Host '==> Compiling installer with Inno Setup...' -ForegroundColor Cyan
    & $Iscc "/DMyAppVersion=$Ver" $Iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
    $root  = Split-Path -Parent (Split-Path -Parent $Iss)  # ..\..  from installer\CatFoil.iss
    $setup = Join-Path $root "dist\CatFoil-Setup-$Ver.exe"
    if (-not (Test-Path $setup)) { throw "ISCC reported success but $setup is missing." }
    return $setup
}
