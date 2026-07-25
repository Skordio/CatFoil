# Two OverlayForms alive at once: distinct identity, independent appearance,
# and cascaded placement so a second badge doesn't spawn atop the first.
# Touches no settings file and never goes near the keyboard hook.
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
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$oT  = $asm.GetType('CatFoil.OverlayForm')
$stT = $asm.GetType('CatFoil.OverlayStateSettings')

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump { param($ms = 300)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}
function NewState($size, $visible) {
  $s = [Activator]::CreateInstance($stT); $s.Size = $size; $s.Visible = $visible; $s
}

$icon = [System.Drawing.SystemIcons]::Application
$hidden = NewState 64 $false

$a = [Activator]::CreateInstance($oT, @($icon, 'overlay-a'))
$b = [Activator]::CreateInstance($oT, @($icon, 'overlay-b'))
try {
  $a.ApplyAppearance((NewState 64 $true), $hidden)
  $b.ApplyAppearance((NewState 128 $true), $hidden)

  # Neither has ever been dragged, so both fall back to the automatic spot.
  $a.ApplySavedPosition($null, 0)
  $b.ApplySavedPosition($null, 1)

  Check 'ids kept distinct' ($a.OverlayId -eq 'overlay-a' -and $b.OverlayId -eq 'overlay-b') "$($a.OverlayId)/$($b.OverlayId)"
  Check 'cascade separates them' ($b.Location.Y -gt $a.Location.Y) "a=$($a.Location) b=$($b.Location)"
  Check 'cascade steps by 88px' (($b.Location.Y - $a.Location.Y) -eq 88) "delta=$($b.Location.Y - $a.Location.Y)"

  $a.SetActive($true); $b.SetActive($true)
  Pump 500
  Check 'both badges on screen' ($a.Visible -and $b.Visible) "a=$($a.Visible) b=$($b.Visible)"
  Check 'independent sizes' ($a.ClientSize.Width -eq 64 -and $b.ClientSize.Width -eq 128) "a=$($a.ClientSize) b=$($b.ClientSize)"
  Check 'no overlap' (($a.Bounds.IntersectsWith($b.Bounds)) -eq $false) "a=$($a.Bounds) b=$($b.Bounds)"

  # A remembered position must win over the cascade.
  $c = [Activator]::CreateInstance($oT, @($icon, 'overlay-c'))
  try {
    $c.ApplyAppearance((NewState 64 $true), $hidden)
    $c.ApplySavedPosition([System.Drawing.Point]::new(300, 400), 5)
    Check 'saved position beats cascade' ($c.Location.X -eq 300 -and $c.Location.Y -eq 400) $c.Location
  } finally { $c.Dispose() }

  # Disabling one leaves the other alone — the per-item Enabled switch.
  $b.SetActive($false)
  Pump 300
  Check 'one hides without touching the other' ($a.Visible -and -not $b.Visible) "a=$($a.Visible) b=$($b.Visible)"
}
finally { $a.Dispose(); $b.Dispose() }

Write-Host ''
if ($fails -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green } else { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }
