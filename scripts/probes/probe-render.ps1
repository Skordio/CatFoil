# Renders the badge in a spread of configurations and dumps raw ARGB.
#   -Mode baseline  : the pre-3b renderer (opaque Draw; the window applied 235/255)
#   -Mode check     : the post-3b renderer, asserted against the baseline
# Pixel-only; touches no settings.
# -Scale is the alpha the LAYERED WINDOW applied on top of Draw's output when
# the baseline was taken. It was 235 before 3b moved opacity into the renderer;
# from 3b on Draw emits the final composited alpha itself, so it is 255.
param(
  [ValidateSet('baseline','check')][string]$Mode = 'check',
  [int]$Scale = 255
)
$ErrorActionPreference = 'Stop'

# Paths resolve from this script's location, so the probe runs from a clone
# anywhere. Output (screenshots, render baselines) goes to artifacts/, which is
# gitignored.
$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
$ProbeOut   = Join-Path $RepoRoot 'artifacts\probes'
$ProbeShots = Join-Path $ProbeOut 'shots'
New-Item -ItemType Directory -Force -Path $ProbeShots | Out-Null
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
# SAFETY NET. Settings.Save() writes the user's REAL settings.json --
# GetFolderPath uses the shell API, ignores $env:APPDATA and cannot be pointed
# elsewhere. A probe that edits a setting and then pumps for longer than the
# 500 ms debounce is enough to trigger it, which is easy to do by accident and
# silently destroys live configuration. Snapshot the file and put it back
# however this script exits, so the rule is enforced rather than remembered.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$dir = (Join-Path $ProbeOut 'render')
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$rT  = $asm.GetType('CatFoil.OverlayRenderer')
$stT = $asm.GetType('CatFoil.OverlayAppearance')

$LegacyAlpha = $Scale

$shapeT = $asm.GetType('CatFoil.OverlayShape')

function NewState($size, $bg) {
  $s = [Activator]::CreateInstance($stT)
  $s.Size = $size
  $s.Shape = [Enum]::Parse($shapeT, $(if ($bg) { 'RoundedSquare' } else { 'None' }))
  $s
}

# A deterministic stand-in for the cat: opaque core, soft alpha edge, colour.
function MakeIcon($n = 256) {
  $b = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,90,180,240))), 10, 10, $n-20, $n-20)
  $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(128,240,120,60))), 40, 40, 60, 60)
  $g.Dispose()
  $b
}

$icon = MakeIcon
$cases = @(
  @{ name='plain64';      size=64;  bg=$true;  text=$null;   flash=$false }
  @{ name='nobg64';       size=64;  bg=$false; text=$null;   flash=$false }
  @{ name='countdown64';  size=64;  bg=$true;  text='2:05';  flash=$false }
  @{ name='flash64';      size=64;  bg=$true;  text=$null;   flash=$true  }
  @{ name='big160';       size=160; bg=$true;  text=$null;   flash=$true  }
  @{ name='small32';      size=32;  bg=$true;  text=$null;   flash=$false }
)

function RenderCase($c) {
  $n = [int]$c.size
  $bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $state = NewState $n ([bool]$c.bg)
  $rect = New-Object System.Drawing.Rectangle(0, 0, $n, $n)
  # Not $args — that is a PowerShell automatic. Every reference-type element
  # needs .PSObject.BaseObject or reflection sees a PSObject wrapper.
  $callArgs = New-Object object[] 7
  $callArgs[0] = $g.PSObject.BaseObject
  $callArgs[1] = $rect.PSObject.BaseObject
  $callArgs[2] = $state.PSObject.BaseObject
  $callArgs[3] = $icon.PSObject.BaseObject
  $callArgs[4] = if ($null -eq $c.text) { $null } else { [string]$c.text }
  $callArgs[5] = [bool]$c.flash
  $callArgs[6] = [bool]$c.blocked
  $rT.GetMethod('Draw').Invoke($null, $callArgs) | Out-Null
  $g.Dispose()
  $bmp
}

# Raw ARGB out of a bitmap, as a flat byte[] (BGRA order per pixel).
function GetBytes($bmp) {
  $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $len = $data.Stride * $bmp.Height
  $buf = New-Object byte[] $len
  [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $len)
  $bmp.UnlockBits($data)
  ,$buf
}

$fails = 0
foreach ($c in $cases) {
  $bmp = RenderCase $c
  $bytes = GetBytes $bmp
  $path = Join-Path $dir "$($c.name).bin"

  # Baselines live under artifacts/ and so are absent in a fresh clone. Seed
  # them rather than failing: there is nothing to compare against yet, and a red
  # run-all on a clean checkout would just teach people to ignore it.
  if ($Mode -eq 'baseline' -or -not (Test-Path $path)) {
    [IO.File]::WriteAllBytes($path, $bytes)
    $bmp.Save((Join-Path $dir "$($c.name).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $why = if ($Mode -eq 'baseline') { 'baseline' } else { 'seeded  ' }
    Write-Host "  $why $($c.name)  ($($bytes.Length) bytes)"
    $script:seeded = $true
  }
  else {
    $old = [IO.File]::ReadAllBytes($path)
    if ($old.Length -ne $bytes.Length) { Write-Host "FAIL  $($c.name) size changed" -ForegroundColor Red; $fails++; $bmp.Dispose(); continue }

    # Compare PREMULTIPLIED channels — that is what the compositor actually puts
    # on screen. Raw channels diverge harmlessly on near-transparent pixels,
    # because the ColorMatrix blit round-trips through premultiplied space and
    # loses low-order bits where alpha is small.
    $maxA = 0; $maxC = 0; $bad = 0
    for ($i = 0; $i -lt $old.Length; $i += 4) {
      $expA = [int][Math]::Round(($old[$i+3] * $LegacyAlpha) / 255.0)
      $dA = [Math]::Abs($bytes[$i+3] - $expA)
      if ($dA -gt $maxA) { $maxA = $dA }
      if ($dA -gt 1) { $bad++ }

      for ($k = 0; $k -lt 3; $k++) {
        $expC = ([int]$old[$i+$k] * $expA) / 255.0
        $actC = ([int]$bytes[$i+$k] * [int]$bytes[$i+3]) / 255.0
        $dC = [Math]::Abs($actC - $expC)
        if ($dC -gt $maxC) { $maxC = $dC }
      }
    }
    $maxC = [Math]::Round($maxC, 1)
    $bmp.Save((Join-Path $dir "$($c.name).after.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $ok = ($maxA -le 1) -and ($maxC -le 2)
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    $col = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0}  {1,-12} max alpha delta={2}  max rgb delta={3}  pixels off by >1: {4}" -f $tag, $c.name, $maxA, $maxC, $bad) -ForegroundColor $col
    if (-not $ok) { $fails++ }
  }
  $bmp.Dispose()
}
$icon.Dispose()

Write-Host ''
if ($Mode -eq 'baseline') { Write-Host 'BASELINE CAPTURED' -ForegroundColor Cyan }
elseif ($fails -gt 0) { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }
elseif ($seeded) { Write-Host 'BASELINE SEEDED — re-run to compare against it' -ForegroundColor Cyan }
else { Write-Host 'ALL PASS — composited output is unchanged' -ForegroundColor Green }

}
finally {
    # Restore byte-for-byte, including the case where there was no file at all.
    if ($null -ne $SettingsBackup) {
        $now = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
        $same = ($null -ne $now) -and ($now.Length -eq $SettingsBackup.Length)
        if ($same) { for ($i = 0; $i -lt $now.Length; $i++) { if ($now[$i] -ne $SettingsBackup[$i]) { $same = $false; break } } }
        if (-not $same) {
            [IO.File]::WriteAllBytes($LiveSettings, $SettingsBackup)
            Write-Host 'NOTE: this probe wrote settings.json; the original has been restored.' -ForegroundColor Yellow
        }
    }
    elseif (Test-Path $LiveSettings) {
        Remove-Item $LiveSettings -Force
    }
}
