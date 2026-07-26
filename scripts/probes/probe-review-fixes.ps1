# Regression cover for the code-review findings. Each check here maps to a bug
# that existed and was fixed, so a reintroduction fails loudly.
$ErrorActionPreference = 'Stop'

# Paths resolve from this script's location, so the probe runs from a clone
# anywhere. Output goes to artifacts/, which is gitignored.
$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
$ProbeOut   = Join-Path $RepoRoot 'artifacts\probes'
$ProbeShots = Join-Path $ProbeOut 'shots'
New-Item -ItemType Directory -Force -Path $ProbeShots | Out-Null
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
# SAFETY NET - see README. Restores settings.json however this script exits.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT   = $asm.GetType('CatFoil.Settings')
$fT   = $asm.GetType('CatFoil.SettingsForm')
$oT   = $asm.GetType('CatFoil.OverlayForm')
$stT  = $asm.GetType('CatFoil.OverlayAppearance')
$srcT = $asm.GetType('CatFoil.OverlayIconSource')
$ifl  = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump { param($ms = 300)
  $e = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $e) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}

$settings = [Activator]::CreateInstance($sT)
$item = $settings.EnsureOverlays()[0]
$settings.EnsureSounds()

$form = [Activator]::CreateInstance($fT, @($settings))
$form.Icon = [System.Drawing.SystemIcons]::Application
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(50, 30)
$form.Show(); Pump 450
($fT.GetField('_nav', $ifl).GetValue($form)).SelectedIndex = 2
Pump 400
$page = ($fT.GetField('_pages', $ifl).GetValue($form))[2]
$card = @($page.Controls[0].Controls | Where-Object { $_.GetType().Name -eq 'OverlayCard' })[0]
(@($card.Controls | Where-Object { $_.Text -eq 'Edit' })[0]).PerformClick()
Pump 500
$sub = $fT.GetField('_subPage', $ifl).GetValue($form)
$body = @($sub.Controls[0].Controls | Where-Object { $_.GetType().Name -eq 'Panel' })[0]

# --- Finding 2: rebuilding the editor body must dispose EVERY control --------
# Dispose() removes from the collection being enumerated, so a foreach skipped
# every other child and the following Clear() leaked their handles.
$before = @($body.Controls)
$countBefore = $before.Count
Check 'editor body has controls to rebuild' ($countBefore -gt 8) "count=$countBefore"

# Invoked directly rather than through a control. The only caller left is the
# icon-colour picker, which opens a modal ColorDialog and would hang the probe —
# and the assertion below is about the disposal loop itself, not about who
# starts it. (The state selector that used to trigger this is gone: an overlay
# has one appearance now, and a ShowIn dropdown that rebuilds nothing.)
$fT.Assembly.GetType('CatFoil.OverlayEditorPage').GetMethod('BuildBody', $ifl).Invoke($sub, @())
Pump 450

$leaked = @($before | Where-Object { -not $_.IsDisposed })
Check 'every control from the old body was disposed' ($leaked.Count -eq 0) `
  ("$($leaked.Count) leaked: " + (($leaked | ForEach-Object { $_.GetType().Name }) -join ','))

# --- Finding 8: the breadcrumb follows a rename ------------------------------
$hdr = $fT.GetField('_header', $ifl).GetValue($form)
$allc = New-Object System.Collections.ArrayList
function Walk2($c) { foreach ($k in $c.Controls) { [void]$allc.Add($k); Walk2 $k } }
Walk2 $sub
$nameBox = @($allc | Where-Object { $_.GetType().Name -eq 'TextBox' })[0]
$nameBox.Text = 'Renamed badge'
Pump 250
Check 'breadcrumb follows a rename' ($hdr.Text -like '*Renamed badge*') $hdr.Text

$fT.GetMethod('PopSubPage', $ifl).Invoke($form, @()) | Out-Null
Pump 350
$form.Dispose()

# --- Finding 4: choosing a custom image sets IconSource, not the legacy flag --
# Read the source of truth directly: the editor's import path must not write
# UseCustomIcon, which the 0.4 model only reads once and then nulls.
$editorSrc = Get-Content (Join-Path $RepoRoot 'src\Pages\OverlayEditorPage.cs') -Raw
Check 'editor never writes the legacy UseCustomIcon flag' (-not ($editorSrc -match 'x\.UseCustomIcon\s*='))
Check 'editor sets IconSource on import' ($editorSrc -match 'IconSource\s*=\s*OverlayIconSource\.Custom')

# A round trip through the model proves the flag stays absent.
$opt = [System.Text.Json.JsonSerializerOptions]::new()
$opt.Converters.Add([System.Text.Json.Serialization.JsonStringEnumConverter]::new())
$s2 = [Activator]::CreateInstance($sT)
$i2 = $s2.EnsureOverlays()[0]
$i2.Appearance.IconSource = [Enum]::Parse($srcT, 'Custom')
$i2.Appearance.CustomIconFile = 'icons\x-abc123.png'
$json = [System.Text.Json.JsonSerializer]::Serialize($s2, $sT, $opt)
Check 'saved overlay carries IconSource, not UseCustomIcon' `
  (($json -match '"IconSource"') -and -not ($json -match '"UseCustomIcon"'))
$reloaded = [System.Text.Json.JsonSerializer]::Deserialize($json, $sT, $opt)
Check 'reload keeps Custom rather than forcing it' `
  (($reloaded.EnsureOverlays()[0]).Appearance.IconSource.ToString() -eq 'Custom')

# --- Finding 6: automatic placement centres on the badge's real size ---------
# The form is created at the default 64px, so placing before ApplyAppearance
# centred a 200px badge as though it were 64.
$big = [Activator]::CreateInstance($stT); $big.Size = 200
$showIn = [Enum]::Parse($asm.GetType('CatFoil.OverlayShowIn'), 'ExceptFullscreen')
$ov = [Activator]::CreateInstance($oT, @([System.Drawing.SystemIcons]::Application, 'probe-size'))
try {
  $ov.ApplyAppearance($big, $showIn)
  Check 'appearance sizes a hidden badge' ($ov.ClientSize.Width -eq 200) $ov.ClientSize.Width
  $ov.ApplySavedPosition($null, 0)
  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $cx = ($ov.Left + $ov.Width / 2 - $wa.Left) / $wa.Width
  $cy = ($ov.Top + $ov.Height / 2 - $wa.Top) / $wa.Height
  Check 'a 200px badge still centres at 3/4 across' ([Math]::Abs($cx - 0.75) -lt 0.03) $cx
  Check 'a 200px badge still centres at 1/4 down' ([Math]::Abs($cy - 0.25) -lt 0.03) $cy
} finally { $ov.Dispose() }

# --- Finding 5: the blocked ring keeps its antialiasing ---------------------
# Draw paints the ring on the CALLER's Graphics, which arrives with smoothing
# off. Without setting it, the curve renders as hard stair-steps.
$rT = $asm.GetType('CatFoil.OverlayRenderer')
function RingEdgeAlphas {
  $n = 96
  $bmp = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $st = [Activator]::CreateInstance($stT); $st.Size = $n; $st.Opacity = 100
  $icon = New-Object System.Drawing.Bitmap(8, 8, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $a = New-Object object[] 7
  $a[0] = $g.PSObject.BaseObject
  $a[1] = (New-Object System.Drawing.Rectangle(0,0,$n,$n)).PSObject.BaseObject
  $a[2] = $st.PSObject.BaseObject; $a[3] = $icon.PSObject.BaseObject
  $a[4] = $null; $a[5] = $true; $a[6] = $false
  $rT.GetMethod('Draw').Invoke($null, $a) | Out-Null
  $g.Dispose()
  $r = New-Object System.Drawing.Rectangle(0,0,$n,$n)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $buf = New-Object byte[] ($d.Stride * $n)
  [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($d); $bmp.Dispose(); $icon.Dispose()
  # Count partially-transparent pixels in all four corners, where the ring's arc
  # sits on bare transparency rather than over the badge's own background. With
  # smoothing off GDI+ writes hard edges and this is essentially zero; with it
  # on, every corner arc contributes a band of partial coverage.
  $partial = 0
  foreach ($cy in 0, ($n - 34)) {
    foreach ($cx in 0, ($n - 34)) {
      for ($y = $cy; $y -lt $cy + 34; $y++) {
        for ($x = $cx; $x -lt $cx + 34; $x++) {
          $al = $buf[$y * $d.Stride + $x * 4 + 3]
          if ($al -gt 12 -and $al -lt 243) { $partial++ }
        }
      }
    }
  }
  $partial
}
$partial = RingEdgeAlphas
Check 'the blocked ring is antialiased' ($partial -gt 60) "partial-alpha pixels across the four corner arcs: $partial"

# --- Finding 7: MCI devices are released before the sweep -------------------
$formSrc = Get-Content (Join-Path $RepoRoot 'src\SettingsForm.cs') -Raw
$closeAt = $formSrc.IndexOf('AudioPlayer.CloseAll()')
$sweepAt = $formSrc.IndexOf('SoundStore.CollectGarbage')
Check 'audio devices are closed before sounds are swept' `
  (($closeAt -gt 0) -and ($sweepAt -gt $closeAt)) "close@$closeAt sweep@$sweepAt"

# --- Finding 1: a live edit does not redo hotkey/startup registration -------
$traySrc = Get-Content (Join-Path $RepoRoot 'src\TrayAppContext.cs') -Raw
Check 'live edits are gated behind a change check' ($traySrc -match '_appliedHotkeyState')
Check 'startup registration is gated too' ($traySrc -match '_appliedStartupState')
Check 'the settings handler no longer re-registers unconditionally' `
  (-not ($traySrc -match 'SettingsSaved \+= \(\) =>'))

# --- Finding 3: the capture hold is released when the window deactivates ----
$lockSrc = Get-Content (Join-Path $RepoRoot 'src\Pages\LockingPage.cs') -Raw
Check 'capture releases on window deactivate' ($lockSrc -match 'Deactivate \+= OnOwnerDeactivate')
Check 'capture re-arms on window activate' ($lockSrc -match 'Activated \+= OnOwnerActivated')
Check 'activation handlers are detached on dispose' ($lockSrc -match 'Deactivate -= OnOwnerDeactivate')

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
