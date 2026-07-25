# Step 2: OverlayItem model + legacy migration.
# Works purely on JSON strings + Settings.EnsureOverlays(), and only ever READS
# the live settings.json — Settings.Save() is never called, because
# GetFolderPath ignores $env:APPDATA and would overwrite the real file.
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

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT  = $asm.GetType('CatFoil.Settings')

$opt = [System.Text.Json.JsonSerializerOptions]::new()
$opt.WriteIndented = $true
$opt.Converters.Add([System.Text.Json.Serialization.JsonStringEnumConverter]::new())

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}

# --- 1. A 0.3-shaped file, with a custom icon and a dragged position ---------
$legacy = @'
{
  "Hotkey": "Alt, G",
  "HotkeyEnabled": true,
  "ShowOverlay": true,
  "OverlayPosition": { "X": 1500, "Y": 40 },
  "OverlayNormal": {
    "Visible": true, "UseCustomIcon": true,
    "CustomIconFile": "overlay-normal.png", "Size": 96, "ShowBackground": true
  },
  "OverlayFullscreen": {
    "Visible": false, "UseCustomIcon": false,
    "CustomIconFile": null, "Size": 64, "ShowBackground": true
  },
  "StatLockSessions": 12
}
'@
$s = [System.Text.Json.JsonSerializer]::Deserialize($legacy, $sT, $opt)
$list = $s.EnsureOverlays()

Check '1 overlay synthesized from legacy' ($list.Count -eq 1) "count=$($list.Count)"
$item = $list[0]
Check 'custom icon preserved' ($item.Normal.CustomIconFile -eq 'overlay-normal.png') $item.Normal.CustomIconFile
Check 'size preserved' ($item.Normal.Size -eq 96) $item.Normal.Size
Check 'fullscreen hidden preserved' ($item.Fullscreen.Visible -eq $false)
# PowerShell auto-unwraps Nullable<Point>, so reach for .X directly (not .Value.X).
Check 'position preserved' ($item.Position.X -eq 1500 -and $item.Position.Y -eq 40) $item.Position
Check 'item enabled by default' ($item.Enabled -eq $true)
Check 'item has an id' (-not [string]::IsNullOrWhiteSpace($item.Id)) $item.Id
Check 'unrelated settings untouched' ($s.StatLockSessions -eq 12 -and $s.Hotkey.ToString() -eq 'G, Alt') $s.Hotkey

# Legacy keys must stop being written.
$out = [System.Text.Json.JsonSerializer]::Serialize($s, $sT, $opt)
Check 'OverlayNormal not written'     (-not ($out -match '"OverlayNormal"'))
Check 'OverlayFullscreen not written' (-not ($out -match '"OverlayFullscreen"'))
Check 'OverlayPosition not written'   (-not ($out -match '"OverlayPosition"'))
Check 'Overlays written'              ($out -match '"Overlays"')

# --- 2. Reloading the migrated file must not change anything -----------------
$again = [System.Text.Json.JsonSerializer]::Deserialize($out, $sT, $opt)
$againList = $again.EnsureOverlays()
Check 'round-trip keeps 1 overlay' ($againList.Count -eq 1) "count=$($againList.Count)"
Check 'round-trip keeps the id' ($againList[0].Id -eq $item.Id) "$($againList[0].Id) vs $($item.Id)"
Check 'round-trip keeps the icon' ($againList[0].Normal.CustomIconFile -eq 'overlay-normal.png')
Check 'EnsureOverlays is idempotent' (($again.EnsureOverlays()).Count -eq 1)

# --- 3. Fresh install: no legacy keys at all --------------------------------
$fresh = [Activator]::CreateInstance($sT)
$freshList = $fresh.EnsureOverlays()
Check 'fresh gets one default overlay' ($freshList.Count -eq 1) "count=$($freshList.Count)"
Check 'fresh default shows normally' ($freshList[0].Normal.Visible -eq $true)
Check 'fresh default hides on fullscreen' ($freshList[0].Fullscreen.Visible -eq $false)
Check 'fresh default has no position' ($null -eq $freshList[0].Position)
Check 'two fresh items get distinct ids' `
  (([Activator]::CreateInstance($sT).EnsureOverlays()[0].Id) -ne $freshList[0].Id)

# --- 4. Corrupt/partial JSON still yields a usable overlay ------------------
$partial = [System.Text.Json.JsonSerializer]::Deserialize('{"ShowOverlay":false}', $sT, $opt)
Check 'partial json yields one overlay' ($partial.EnsureOverlays().Count -eq 1)

# --- 4b. A hand-edited / truncated file must not crash startup --------------
$nullList = [System.Text.Json.JsonSerializer]::Deserialize('{"Overlays":null}', $sT, $opt)
Check 'null Overlays recovers' ($nullList.EnsureOverlays().Count -eq 1)

$nullStates = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"x","Normal":null,"Fullscreen":null}]}', $sT, $opt)
$ns = $nullStates.EnsureOverlays()
Check 'null state blocks recover' ($null -ne $ns[0].Normal -and $null -ne $ns[0].Fullscreen)

$dupes = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"same"},{"Id":"same"},{"Id":""}]}', $sT, $opt)
$d = $dupes.EnsureOverlays()
Check 'duplicate/blank ids made unique' `
  ((($d | ForEach-Object { $_.Id } | Sort-Object -Unique).Count) -eq 3) `
  (($d | ForEach-Object { $_.Id }) -join ',')
Check 'duplicate ids keep all 3 overlays' ($d.Count -eq 3) "count=$($d.Count)"

# --- 4c. ShowBackground -> Shape, on a 0.3-shaped file ----------------------
# The ordering trap: on a 0.3 file the states only exist as OverlayNormal /
# OverlayFullscreen until the legacy fold, so a per-state migration written into
# the loop that runs BEFORE that fold would silently skip them.
$legacyShape = @'
{
  "OverlayNormal":     { "Visible": true, "ShowBackground": false, "Size": 96 },
  "OverlayFullscreen": { "Visible": false, "ShowBackground": true },
  "StatLockedSeconds": 4242
}
'@
$ls = [System.Text.Json.JsonSerializer]::Deserialize($legacyShape, $sT, $opt)
$li = $ls.EnsureOverlays()[0]
Check '0.3 ShowBackground:false -> Shape None' ($li.Normal.Shape.ToString() -eq 'None') $li.Normal.Shape
Check '0.3 ShowBackground:true -> Shape RoundedSquare' ($li.Fullscreen.Shape.ToString() -eq 'RoundedSquare') $li.Fullscreen.Shape
Check '0.3 size still preserved alongside' ($li.Normal.Size -eq 96) $li.Normal.Size
Check '0.3 stats survive the migration' ($ls.StatLockedSeconds -eq 4242)

$lsOut = [System.Text.Json.JsonSerializer]::Serialize($ls, $sT, $opt)
Check 'ShowBackground no longer written' (-not ($lsOut -match '"ShowBackground"'))
Check 'Shape is written' ($lsOut -match '"Shape"')

# A 0.4-shaped file: ShowBackground absent, Shape honoured as-is.
$modern = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Normal":{"Shape":"Circle"},"Fullscreen":{"Shape":"Square"}}]}', $sT, $opt)
$mi = $modern.EnsureOverlays()[0]
Check '0.4 Shape read back' ($mi.Normal.Shape.ToString() -eq 'Circle') $mi.Normal.Shape

# Idempotence: a deliberately chosen None must survive repeated calls. Keying
# the migration on "Shape looks default" instead of "ShowBackground present"
# would turn it back into a box here.
$none = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Normal":{"Shape":"None"}}]}', $sT, $opt)
$none.EnsureOverlays() | Out-Null
$none.EnsureOverlays() | Out-Null
$ni = $none.EnsureOverlays()[0]
Check 'deliberate None survives repeated EnsureOverlays' ($ni.Normal.Shape.ToString() -eq 'None') $ni.Normal.Shape

# --- 4d. An unreadable enum must not cost the user everything ---------------
# The stock converter throws, Load() catches and returns defaults, and the
# lifetime counters are gone for good. The lenient converter must prevent that.
$weird = '{"Overlays":[{"Id":"a","Normal":{"Shape":"Hexagon"}}],"StatLockSessions":77,"StatBlockedKeys":999}'
$w = [System.Text.Json.JsonSerializer]::Deserialize($weird, $sT, $opt)
$wi = $w.EnsureOverlays()[0]
Check 'unknown shape falls back to the default' ($wi.Normal.Shape.ToString() -eq 'RoundedSquare') $wi.Normal.Shape
Check 'unknown shape does NOT discard lifetime stats' ($w.StatLockSessions -eq 77 -and $w.StatBlockedKeys -eq 999) `
  "sessions=$($w.StatLockSessions) keys=$($w.StatBlockedKeys)"

$numeric = [System.Text.Json.JsonSerializer]::Deserialize('{"Overlays":[{"Id":"a","Normal":{"Shape":2}}]}', $sT, $opt)
Check 'numeric shape still readable' (($numeric.EnsureOverlays()[0]).Normal.Shape.ToString() -eq 'Circle')

$structural = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Normal":{"Shape":{"nonsense":1},"Size":77}}]}', $sT, $opt)
$si = $structural.EnsureOverlays()[0]
Check 'structural garbage for an enum is stepped over' ($si.Normal.Shape.ToString() -eq 'RoundedSquare')
Check 'reader stays in sync after skipping it' ($si.Normal.Size -eq 77) $si.Normal.Size

# --- 4d2. UseCustomIcon -> IconSource, same ordering trap -------------------
$legacyIcon = @'
{
  "OverlayNormal":     { "UseCustomIcon": true,  "CustomIconFile": "overlay-normal.png" },
  "OverlayFullscreen": { "UseCustomIcon": false }
}
'@
$li2 = ([System.Text.Json.JsonSerializer]::Deserialize($legacyIcon, $sT, $opt)).EnsureOverlays()[0]
Check '0.3 UseCustomIcon:true -> Custom' ($li2.Normal.IconSource.ToString() -eq 'Custom') $li2.Normal.IconSource
Check '0.3 custom file still points at the legacy name' ($li2.Normal.CustomIconFile -eq 'overlay-normal.png')
Check '0.3 UseCustomIcon:false -> Default' ($li2.Fullscreen.IconSource.ToString() -eq 'Default') $li2.Fullscreen.IconSource

$iconOut = [System.Text.Json.JsonSerializer]::Serialize(
  [System.Text.Json.JsonSerializer]::Deserialize($legacyIcon, $sT, $opt), $sT, $opt)
Check 'UseCustomIcon still readable before migration' ($iconOut -match '"UseCustomIcon"')
$migrated = [System.Text.Json.JsonSerializer]::Deserialize($legacyIcon, $sT, $opt)
$migrated.EnsureOverlays() | Out-Null
$migOut = [System.Text.Json.JsonSerializer]::Serialize($migrated, $sT, $opt)
Check 'UseCustomIcon not written after migration' (-not ($migOut -match '"UseCustomIcon"'))
Check 'IconSource written instead' ($migOut -match '"IconSource"')

# A deliberately chosen Gallery must survive repeated calls.
$gal = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Normal":{"IconSource":"Gallery","GalleryIconId":"heart"}}]}', $sT, $opt)
$gal.EnsureOverlays() | Out-Null
$gi = $gal.EnsureOverlays()[0]
Check 'Gallery source survives repeated EnsureOverlays' ($gi.Normal.IconSource.ToString() -eq 'Gallery')
Check 'gallery id preserved' ($gi.Normal.GalleryIconId -eq 'heart')

$badSrc = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Normal":{"IconSource":"Telepathy"}}],"StatLockSessions":5}', $sT, $opt)
$bi = $badSrc.EnsureOverlays()[0]
Check 'unknown icon source falls back to Default' ($bi.Normal.IconSource.ToString() -eq 'Default') $bi.Normal.IconSource
Check 'unknown icon source keeps the stats' ($badSrc.StatLockSessions -eq 5)

# --- 4e. Opacity defaults and clamping --------------------------------------
$fresh2 = [Activator]::CreateInstance($sT)
$f2 = $fresh2.EnsureOverlays()[0]
Check 'default opacity is 92' ($f2.Normal.Opacity -eq 92) $f2.Normal.Opacity
Check 'default ring opacity is 100' ($f2.Normal.RingOpacity -eq 100)
Check 'blocked opacity off by default' ($f2.Normal.BlockedOpacityEnabled -eq $false)

$clamp = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Normal":{"Opacity":9999,"RingOpacity":-5}}]}', $sT, $opt)
$ci = $clamp.EnsureOverlays()[0]
Check 'out-of-range opacity clamps' ($ci.Normal.ClampedOpacity() -eq 100) $ci.Normal.ClampedOpacity()
Check 'negative ring opacity clamps to 0' ($ci.Normal.ClampedRingOpacity() -eq 0) $ci.Normal.ClampedRingOpacity()

# --- 5. The REAL settings.json on this machine loads and migrates -----------
# Read-only: Load() never writes.
$live = $sT.GetMethod('Load').Invoke($null, @())
$liveList = $live.EnsureOverlays()
Write-Host ''
Write-Host "live settings.json -> $($liveList.Count) overlay(s); id=$($liveList[0].Id)"
Write-Host "  normal:     visible=$($liveList[0].Normal.Visible) size=$($liveList[0].Normal.Size) custom=$($liveList[0].Normal.UseCustomIcon) file=$($liveList[0].Normal.CustomIconFile)"
Write-Host "  fullscreen: visible=$($liveList[0].Fullscreen.Visible) size=$($liveList[0].Fullscreen.Size) custom=$($liveList[0].Fullscreen.UseCustomIcon)"
Write-Host "  position:   $($liveList[0].Position)"
Check 'live settings migrate to >=1 overlay' ($liveList.Count -ge 1)

Write-Host ''
if ($fails -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green } else { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }
