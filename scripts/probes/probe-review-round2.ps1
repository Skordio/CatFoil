# Fixes from the 2026-08-11 code review of 0.5.0→0.6.0. Asserts the probeable
# ones: closing with "Hide to tray on close" OFF requests an app exit instead of
# disposing the only window (the tray kept a dead form before), entering
# settings keeps the grown window inside the screen's work area, and rebuilding
# the overlay editor's body disposes the gallery buttons' owned bitmaps.
# Constructs forms only — no keyboard hook, no tray, no lock, no Settings.Save.
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
# SAFETY NET (see probe-settings.ps1): Settings.Save() writes the user's REAL
# settings.json and cannot be redirected. Snapshot and restore byte-for-byte.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$asm   = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT    = $asm.GetType('CatFoil.Settings')
$mfT   = $asm.GetType('CatFoil.MainForm')
$shT   = $asm.GetType('CatFoil.SettingsShell')
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump { param($ms = 300)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}

# ---------------------------------------------------------------
# 1. Close with "Hide to tray on close" OFF: request exit, don't zombify.
#    The app is tray-first with no owning form, so a disposed MainForm used
#    to leave a tray whose every entry threw ObjectDisposedException.
# ---------------------------------------------------------------
$settings = [Activator]::CreateInstance($sT)
$settings.MinimizeToTrayOnClose = $false
$mf = [Activator]::CreateInstance($mfT, @($settings.PSObject.BaseObject))
$mf.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$mf.Location = New-Object System.Drawing.Point(80, 60)
$mf.Show(); Pump 300

$exitEvent = $mfT.GetEvent('ExitRequested', $flags)
Check 'MainForm.ExitRequested exists' ($null -ne $exitEvent)
$script:exitRequests = 0
if ($null -ne $exitEvent) {
  # Register-ObjectEvent never fires in a synchronous probe; AddEventHandler does.
  $exitEvent.AddEventHandler($mf, [Action]{ $script:exitRequests++ })
}

$mf.Close(); Pump 300
Check 'close with hide-to-tray off does not dispose the window' (-not $mf.IsDisposed)
Check 'close with hide-to-tray off requests an app exit' ($script:exitRequests -eq 1) "requests=$script:exitRequests"

# A real exit (AllowClose) still closes for real and asks nothing.
$mf.AllowClose = $true
$mf.Close(); Pump 300
Check 'AllowClose still really closes' ($mf.IsDisposed)
Check 'AllowClose does not request another exit' ($script:exitRequests -eq 1) "requests=$script:exitRequests"

# And with the setting ON, close hides — no exit request (existing behavior).
$settings2 = [Activator]::CreateInstance($sT)   # MinimizeToTrayOnClose defaults true
$mf2 = [Activator]::CreateInstance($mfT, @($settings2.PSObject.BaseObject))
$exit2 = $mfT.GetEvent('ExitRequested', $flags)
$script:exitRequests2 = 0
if ($null -ne $exit2) { $exit2.AddEventHandler($mf2, [Action]{ $script:exitRequests2++ }) }
$mf2.Show(); Pump 200
$mf2.Close(); Pump 200
Check 'close with hide-to-tray on still hides' ((-not $mf2.IsDisposed) -and (-not $mf2.Visible))
Check 'close with hide-to-tray on requests no exit' ($script:exitRequests2 -eq 0) "requests=$script:exitRequests2"

# ---------------------------------------------------------------
# 2. Entering settings keeps the window on screen. The settings view is much
#    larger than the main view and used to grow anchored at the old top-left,
#    hanging the nav and page bottom off the screen edge.
# ---------------------------------------------------------------
$area = [System.Windows.Forms.Screen]::FromControl($mf2).WorkingArea
# Park the small main window at the bottom-right corner of the work area.
$mf2.Show(); Pump 200
$mf2.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$mf2.Location = New-Object System.Drawing.Point(($area.Right - $mf2.Width - 4), ($area.Bottom - $mf2.Height - 4))
Pump 150

$shell = [Activator]::CreateInstance($shT, @($settings2.PSObject.BaseObject))
$enter = $mfT.GetMethod('EnterSettings', $flags)
$enter.Invoke($mf2, @($shell.PSObject.BaseObject)) | Out-Null
Pump 300

$bounds = $mf2.Bounds
Check 'settings view stays inside the work area (right)'  ($bounds.Right  -le $area.Right)  "right=$($bounds.Right) area=$($area.Right)"
Check 'settings view stays inside the work area (bottom)' ($bounds.Bottom -le $area.Bottom) "bottom=$($bounds.Bottom) area=$($area.Bottom)"
Check 'settings view stays inside the work area (left/top)' ($bounds.Left -ge $area.Left -and $bounds.Top -ge $area.Top) "$bounds"

$mf2.AllowClose = $true
$mf2.Close(); Pump 150

# ---------------------------------------------------------------
# 3. Overlay editor: rebuilding the body must dispose the gallery buttons'
#    bitmaps. IconGallery.Render hands the caller an owned copy, and WinForms
#    never disposes Control.Image — every colour pick leaked ten of them.
# ---------------------------------------------------------------
$galT = $asm.GetType('CatFoil.IconGallery')
$galleryAvailable = $galT.GetProperty('IsAvailable', [Reflection.BindingFlags]'Static,NonPublic,Public').GetValue($null)
if (-not $galleryAvailable) {
  Write-Host 'SKIP  gallery bitmaps (no symbol font on this machine)'
} else {
  $sessT = $asm.GetType('CatFoil.SettingsSession')
  $itemT = $asm.GetType('CatFoil.OverlayItem')
  $edT   = $asm.GetType('CatFoil.OverlayEditorPage')

  $settings3 = [Activator]::CreateInstance($sT)
  $null = $settings3.EnsureOverlays()
  $item  = $settings3.Overlays[0]
  $sess  = [Activator]::CreateInstance($sessT, @($settings3.PSObject.BaseObject))
  $page  = [Activator]::CreateInstance($edT, $flags, $null, @($sess.PSObject.BaseObject, $item.PSObject.BaseObject), $null)

  $hostForm = New-Object System.Windows.Forms.Form
  $hostForm.Controls.Add($page)
  $hostForm.Show(); Pump 400   # OnLoad runs BuildChrome + BuildBody

  # Collect the images currently owned by gallery buttons.
  function GalleryImages($root) {
    $found = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    while ($stack.Count -gt 0) {
      $c = $stack.Pop()
      foreach ($child in $c.Controls) { $stack.Push($child) }
      if ($c -is [System.Windows.Forms.Button] -and $null -ne $c.Image) { $null = $found.Add($c.Image) }
    }
    ,$found
  }
  $before = GalleryImages $page
  Check 'gallery buttons carry images' ($before.Count -ge 5) "count=$($before.Count)"

  $edT.GetMethod('BuildBody', $flags).Invoke($page, @()) | Out-Null
  Pump 300

  # A disposed Bitmap's Width throws — but only through reflection: PowerShell's
  # own property access swallows the getter exception and yields $null.
  $widthProp = [System.Drawing.Image].GetProperty('Width')
  $leaked = 0
  foreach ($img in $before) {
    try { $null = $widthProp.GetValue($img.PSObject.BaseObject); $leaked++ } catch { }
  }
  Check 'rebuild disposes the old gallery bitmaps' ($leaked -eq 0) "leaked=$leaked of $($before.Count)"

  # Page disposal releases the final generation too.
  $after = GalleryImages $page
  $hostForm.Controls.Remove($page)
  $page.Dispose()
  $leakedFinal = 0
  foreach ($img in $after) {
    try { $null = $widthProp.GetValue($img.PSObject.BaseObject); $leakedFinal++ } catch { }
  }
  Check 'page dispose releases the last gallery bitmaps' ($leakedFinal -eq 0) "leaked=$leakedFinal of $($after.Count)"
  $hostForm.Dispose()
}

Write-Host ''
if ($fails -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green } else { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }

}
finally {
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

