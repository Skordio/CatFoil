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
# SAFETY NET. Settings.Save() writes the user's REAL settings.json --
# GetFolderPath uses the shell API, ignores $env:APPDATA and cannot be pointed
# elsewhere. A probe that edits a setting and then pumps for longer than the
# 500 ms debounce is enough to trigger it, which is easy to do by accident and
# silently destroys live configuration. Snapshot the file and put it back
# however this script exits, so the rule is enforced rather than remembered.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {


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
Check 'custom icon preserved' ($item.Appearance.CustomIconFile -eq 'overlay-normal.png') $item.Appearance.CustomIconFile
Check 'size preserved' ($item.Appearance.Size -eq 96) $item.Appearance.Size
Check 'fullscreen hidden -> ExceptFullscreen' ($item.ShowIn.ToString() -eq 'ExceptFullscreen') $item.ShowIn
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
# The per-state pair is legacy too now, and its Visible with it.
Check 'per-item Normal not written'     (-not ($out -match '"Normal"'))
Check 'per-item Fullscreen not written' (-not ($out -match '"Fullscreen"'))
Check 'Visible not written'             (-not ($out -match '"Visible"'))
Check 'Appearance written'              ($out -match '"Appearance"')
Check 'ShowIn written'                  ($out -match '"ShowIn"')

# --- 2. Reloading the migrated file must not change anything -----------------
$again = [System.Text.Json.JsonSerializer]::Deserialize($out, $sT, $opt)
$againList = $again.EnsureOverlays()
Check 'round-trip keeps 1 overlay' ($againList.Count -eq 1) "count=$($againList.Count)"
Check 'round-trip keeps the id' ($againList[0].Id -eq $item.Id) "$($againList[0].Id) vs $($item.Id)"
Check 'round-trip keeps the icon' ($againList[0].Appearance.CustomIconFile -eq 'overlay-normal.png')
Check 'round-trip keeps ShowIn' ($againList[0].ShowIn.ToString() -eq 'ExceptFullscreen') $againList[0].ShowIn
Check 'EnsureOverlays is idempotent' (($again.EnsureOverlays()).Count -eq 1)

# --- 3. Fresh install: no legacy keys at all --------------------------------
$fresh = [Activator]::CreateInstance($sT)
$freshList = $fresh.EnsureOverlays()
Check 'fresh gets one default overlay' ($freshList.Count -eq 1) "count=$($freshList.Count)"
Check 'fresh default shows except in fullscreen' ($freshList[0].ShowIn.ToString() -eq 'ExceptFullscreen') $freshList[0].ShowIn
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
  '{"Overlays":[{"Id":"x","Appearance":null}]}', $sT, $opt)
$ns = $nullStates.EnsureOverlays()
Check 'null appearance recovers' ($null -ne $ns[0].Appearance)

# The legacy pair can be null too, on a hand-edited 0.4 file.
$nullLegacy = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"x","Normal":null,"Fullscreen":null}]}', $sT, $opt)
Check 'null legacy state blocks recover' ($null -ne ($nullLegacy.EnsureOverlays()[0]).Appearance)

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
# The surviving block here is Normal, so its ShowBackground:false is the one
# that must have reached Shape. If the collapse and this migration ran in the
# wrong order, Shape would still be the RoundedSquare default.
Check '0.3 ShowBackground:false -> Shape None' ($li.Appearance.Shape.ToString() -eq 'None') $li.Appearance.Shape
Check '0.3 size still preserved alongside' ($li.Appearance.Size -eq 96) $li.Appearance.Size
Check '0.3 stats survive the migration' ($ls.StatLockedSeconds -eq 4242)

# ...and the mirror image: when Fullscreen is the surviving block, ITS
# ShowBackground is the one that has to be migrated. This is the case that
# breaks if the collapse ever starts unconditionally keeping Normal.
$legacyShapeFs = @'
{
  "OverlayNormal":     { "Visible": false, "ShowBackground": false },
  "OverlayFullscreen": { "Visible": true,  "ShowBackground": true, "Size": 111 }
}
'@
$lfs = ([System.Text.Json.JsonSerializer]::Deserialize($legacyShapeFs, $sT, $opt)).EnsureOverlays()[0]
Check 'surviving fullscreen block gets its own Shape migrated' `
  ($lfs.Appearance.Shape.ToString() -eq 'RoundedSquare') $lfs.Appearance.Shape
Check 'surviving fullscreen block keeps its own size' ($lfs.Appearance.Size -eq 111) $lfs.Appearance.Size

$lsOut = [System.Text.Json.JsonSerializer]::Serialize($ls, $sT, $opt)
Check 'ShowBackground no longer written' (-not ($lsOut -match '"ShowBackground"'))
Check 'Shape is written' ($lsOut -match '"Shape"')

# A current-shape file: ShowBackground absent, Shape honoured as-is.
$modern = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Appearance":{"Shape":"Circle"}}]}', $sT, $opt)
$mi = $modern.EnsureOverlays()[0]
Check 'current Shape read back' ($mi.Appearance.Shape.ToString() -eq 'Circle') $mi.Appearance.Shape

# Idempotence: a deliberately chosen None must survive repeated calls. Keying
# the migration on "Shape looks default" instead of "ShowBackground present"
# would turn it back into a box here.
$none = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Appearance":{"Shape":"None"}}]}', $sT, $opt)
$none.EnsureOverlays() | Out-Null
$none.EnsureOverlays() | Out-Null
$ni = $none.EnsureOverlays()[0]
Check 'deliberate None survives repeated EnsureOverlays' ($ni.Appearance.Shape.ToString() -eq 'None') $ni.Appearance.Shape

# --- 4c2. The two-state collapse, all four rows -----------------------------
# The riskiest part of the 0.4.0 -> single-appearance change: what a badge
# looked like and when it appeared were one setting, and are now two.
function Collapse($normalVisible, $fsVisible, $normalSize, $fsSize) {
  $json = '{"Overlays":[{"Id":"a",' +
          "`"Normal`":{`"Visible`":$normalVisible,`"Size`":$normalSize}," +
          "`"Fullscreen`":{`"Visible`":$fsVisible,`"Size`":$fsSize}}]}"
  ([System.Text.Json.JsonSerializer]::Deserialize($json, $sT, $opt)).EnsureOverlays()[0]
}

# Both visible -> Always, and the Normal look is the one kept.
$cBoth = Collapse 'true' 'true' 96 200
Check 'both visible -> Always' ($cBoth.ShowIn.ToString() -eq 'Always') $cBoth.ShowIn
Check 'both visible keeps the normal look' ($cBoth.Appearance.Size -eq 96) $cBoth.Appearance.Size
Check 'both visible stays enabled' ($cBoth.Enabled -eq $true)

# Normal only -> ExceptFullscreen (the overwhelmingly common case).
$cNorm = Collapse 'true' 'false' 96 200
Check 'normal only -> ExceptFullscreen' ($cNorm.ShowIn.ToString() -eq 'ExceptFullscreen') $cNorm.ShowIn
Check 'normal only keeps the normal look' ($cNorm.Appearance.Size -eq 96) $cNorm.Appearance.Size

# Fullscreen only -> OnlyFullscreen, and the FULLSCREEN look survives: that is
# the one that was actually on screen. Taking Normal here would silently
# restyle a badge whose normal appearance the user had never seen.
$cFs = Collapse 'false' 'true' 96 200
Check 'fullscreen only -> OnlyFullscreen' ($cFs.ShowIn.ToString() -eq 'OnlyFullscreen') $cFs.ShowIn
Check 'fullscreen only keeps the FULLSCREEN look' ($cFs.Appearance.Size -eq 200) $cFs.Appearance.Size
Check 'fullscreen only stays enabled' ($cFs.Enabled -eq $true)

# Neither visible -> the badge showed nothing, which ShowIn cannot express.
# Enabled can, and unlike a dropped setting it is visible and one click to undo.
$cNone = Collapse 'false' 'false' 96 200
Check 'neither visible -> disabled' ($cNone.Enabled -eq $false) $cNone.Enabled
Check 'neither visible -> ExceptFullscreen' ($cNone.ShowIn.ToString() -eq 'ExceptFullscreen') $cNone.ShowIn

# Idempotence: a deliberate ShowIn must not be reset by a later load. Keying
# the collapse on "ShowIn looks default" rather than "a legacy block is
# present" would silently undo the user's choice on every startup.
$keep = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","ShowIn":"OnlyFullscreen","Appearance":{"Size":77}}]}', $sT, $opt)
$keep.EnsureOverlays() | Out-Null
$ki = $keep.EnsureOverlays()[0]
Check 'deliberate ShowIn survives repeated EnsureOverlays' ($ki.ShowIn.ToString() -eq 'OnlyFullscreen') $ki.ShowIn
Check 'appearance not clobbered by a no-op collapse' ($ki.Appearance.Size -eq 77) $ki.Appearance.Size

# An unreadable ShowIn must land on the familiar behaviour, not throw away the
# file — same contract as every other enum here.
$badShow = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","ShowIn":"Sometimes"}],"StatLockSessions":31}', $sT, $opt)
$bs = $badShow.EnsureOverlays()[0]
Check 'unknown ShowIn falls back to ExceptFullscreen' ($bs.ShowIn.ToString() -eq 'ExceptFullscreen') $bs.ShowIn
Check 'unknown ShowIn keeps the stats' ($badShow.StatLockSessions -eq 31)

# --- 4d. An unreadable enum must not cost the user everything ---------------
# The stock converter throws, Load() catches and returns defaults, and the
# lifetime counters are gone for good. The lenient converter must prevent that.
$weird = '{"Overlays":[{"Id":"a","Appearance":{"Shape":"Hexagon"}}],"StatLockSessions":77,"StatBlockedKeys":999}'
$w = [System.Text.Json.JsonSerializer]::Deserialize($weird, $sT, $opt)
$wi = $w.EnsureOverlays()[0]
Check 'unknown shape falls back to the default' ($wi.Appearance.Shape.ToString() -eq 'RoundedSquare') $wi.Appearance.Shape
Check 'unknown shape does NOT discard lifetime stats' ($w.StatLockSessions -eq 77 -and $w.StatBlockedKeys -eq 999) `
  "sessions=$($w.StatLockSessions) keys=$($w.StatBlockedKeys)"

$numeric = [System.Text.Json.JsonSerializer]::Deserialize('{"Overlays":[{"Id":"a","Appearance":{"Shape":2}}]}', $sT, $opt)
Check 'numeric shape still readable' (($numeric.EnsureOverlays()[0]).Appearance.Shape.ToString() -eq 'Circle')

$structural = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Appearance":{"Shape":{"nonsense":1},"Size":77}}]}', $sT, $opt)
$si = $structural.EnsureOverlays()[0]
Check 'structural garbage for an enum is stepped over' ($si.Appearance.Shape.ToString() -eq 'RoundedSquare')
Check 'reader stays in sync after skipping it' ($si.Appearance.Size -eq 77) $si.Appearance.Size

# --- 4d2. UseCustomIcon -> IconSource, same ordering trap -------------------
$legacyIcon = @'
{
  "OverlayNormal":     { "UseCustomIcon": true,  "CustomIconFile": "overlay-normal.png" },
  "OverlayFullscreen": { "UseCustomIcon": false }
}
'@
$li2 = ([System.Text.Json.JsonSerializer]::Deserialize($legacyIcon, $sT, $opt)).EnsureOverlays()[0]
Check '0.3 UseCustomIcon:true -> Custom' ($li2.Appearance.IconSource.ToString() -eq 'Custom') $li2.Appearance.IconSource
Check '0.3 custom file still points at the legacy name' ($li2.Appearance.CustomIconFile -eq 'overlay-normal.png')

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
  '{"Overlays":[{"Id":"a","Appearance":{"IconSource":"Gallery","GalleryIconId":"heart"}}]}', $sT, $opt)
$gal.EnsureOverlays() | Out-Null
$gi = $gal.EnsureOverlays()[0]
Check 'Gallery source survives repeated EnsureOverlays' ($gi.Appearance.IconSource.ToString() -eq 'Gallery')
Check 'gallery id preserved' ($gi.Appearance.GalleryIconId -eq 'heart')

$badSrc = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Appearance":{"IconSource":"Telepathy"}}],"StatLockSessions":5}', $sT, $opt)
$bi = $badSrc.EnsureOverlays()[0]
Check 'unknown icon source falls back to Default' ($bi.Appearance.IconSource.ToString() -eq 'Default') $bi.Appearance.IconSource
Check 'unknown icon source keeps the stats' ($badSrc.StatLockSessions -eq 5)

# --- 4e. Opacity defaults and clamping --------------------------------------
$fresh2 = [Activator]::CreateInstance($sT)
$f2 = $fresh2.EnsureOverlays()[0]
Check 'default opacity is 92' ($f2.Appearance.Opacity -eq 92) $f2.Appearance.Opacity
Check 'default ring opacity is 100' ($f2.Appearance.RingOpacity -eq 100)
Check 'blocked opacity off by default' ($f2.Appearance.BlockedOpacityEnabled -eq $false)

$clamp = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"Overlays":[{"Id":"a","Appearance":{"Opacity":9999,"RingOpacity":-5}}]}', $sT, $opt)
$ci = $clamp.EnsureOverlays()[0]
Check 'out-of-range opacity clamps' ($ci.Appearance.ClampedOpacity() -eq 100) $ci.Appearance.ClampedOpacity()
Check 'negative ring opacity clamps to 0' ($ci.Appearance.ClampedRingOpacity() -eq 0) $ci.Appearance.ClampedRingOpacity()

# --- 5. The REAL settings.json on this machine loads and migrates -----------
# Read-only: Load() never writes.
$live = $sT.GetMethod('Load').Invoke($null, @())
$liveList = $live.EnsureOverlays()
Write-Host ''
Write-Host "live settings.json -> $($liveList.Count) overlay(s); id=$($liveList[0].Id)"
Write-Host "  show in:    $($liveList[0].ShowIn)"
Write-Host "  appearance: size=$($liveList[0].Appearance.Size) source=$($liveList[0].Appearance.IconSource) file=$($liveList[0].Appearance.CustomIconFile)"
Write-Host "  position:   $($liveList[0].Position)"
Check 'live settings migrate to >=1 overlay' ($liveList.Count -ge 1)

Write-Host ''
if ($fails -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green } else { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }

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
