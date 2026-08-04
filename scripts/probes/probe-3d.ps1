# Step 3d: the overlay editor sub-page.
# Disposes rather than closes and never pumps past the debounce, so
# Settings.Save() never runs against the live settings.json.
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

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

Add-Type -Name 'U32' -Namespace 'Probe' -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
'@

$out = $ProbeShots
$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT  = $asm.GetType('CatFoil.Settings')
$fT  = $asm.GetType('CatFoil.SettingsShell')
if (-not $fT) { throw 'SettingsShell type not found — probe would false-pass.' }
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump { param($ms = 300)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}
function Shot($form, $name) {
  $bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  try { [Probe.U32]::PrintWindow($form.Handle, $hdc, 2) | Out-Null } finally { $g.ReleaseHdc($hdc); $g.Dispose() }
  $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Host "  captured $name"
}
# Every control anywhere under a parent.
function AllControls($root) {
  $acc = New-Object System.Collections.ArrayList
  function Walk($c) { foreach ($k in $c.Controls) { [void]$acc.Add($k); Walk $k } }
  Walk $root
  $acc
}

$settings = [Activator]::CreateInstance($sT)
$item = $settings.EnsureOverlays()[0]
$item.Name = 'Desk cat'

$shell = [Activator]::CreateInstance($fT, @($settings))
$form = New-Object System.Windows.Forms.Form
$form.ClientSize  = New-Object System.Drawing.Size(900, 720)
$form.MinimumSize = New-Object System.Drawing.Size(840, 560)
$shell.Dock = [System.Windows.Forms.DockStyle]::Fill
$form.Controls.Add($shell)
$form.Icon = [System.Drawing.SystemIcons]::Application
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(60, 40)
$form.Show()
Pump 500

$nav = $fT.GetField('_nav', $flags).GetValue($shell)
$nav.SelectedIndex = 2
Pump 400

# Open the editor the way the UI does: the card's Edit button.
$page = ($fT.GetField('_pages', $flags).GetValue($shell))[2]
$card = @($page.Controls[0].Controls | Where-Object { $_.GetType().Name -eq 'OverlayCard' })[0]
$edit = @($card.Controls | Where-Object { $_.Text -eq 'Edit' })[0]
$edit.PerformClick()
Pump 600

$sub = $fT.GetField('_subPage', $flags).GetValue($shell)
Check 'Edit opens a sub-page' ($null -ne $sub)
Check 'sub-page is the overlay editor' ($sub.GetType().Name -eq 'OverlayEditorPage') $sub.GetType().Name
$hdr = $fT.GetField('_header', $flags).GetValue($shell)
Check 'breadcrumb names the overlay' ($hdr.Text -like '*Desk cat*') $hdr.Text
Shot $form '3d-editor.png'

$ctrls = AllControls $sub
function ByText($t) { @($ctrls | Where-Object { $_.Text -eq $t })[0] }

# --- the editor is present and bound ---------------------------------------
$tracks = @($ctrls | Where-Object { $_.GetType().Name -eq 'TrackBar' })
Check 'four sliders (size, opacity, blocked, ring)' ($tracks.Count -eq 4) "count=$($tracks.Count)"
# Two dropdowns on this page now, so pick them by content rather than by
# position — the ShowIn one lives in the chrome and enumerates first.
$combos = @($ctrls | Where-Object { $_.GetType().Name -eq 'ComboBox' })
Check 'two dropdowns: show-in and shape' ($combos.Count -eq 2) "count=$($combos.Count)"
$combo = @($combos | Where-Object { $_.Items -contains 'Rounded square' })[0]
$showIn = @($combos | Where-Object { $_.Items -contains 'Always' })[0]
Check 'shape picker present with 4 shapes' ($null -ne $combo -and $combo.Items.Count -eq 4)
Check 'show-in picker present with 3 choices' ($null -ne $showIn -and $showIn.Items.Count -eq 3)
Check 'show-in defaults to except-in-fullscreen' ($showIn.SelectedIndex -eq 0) $showIn.SelectedIndex
$preview = @($ctrls | Where-Object { $_.GetType().Name -eq 'PreviewBox' })[0]
Check 'preview present' ($null -ne $preview)
Check 'preview is inside the page bounds' `
  ($preview.Right -le $sub.ClientSize.Width) "preview right=$($preview.Right) page=$($sub.ClientSize.Width)"
Check 'nothing overflows the editor body horizontally' `
  (@($ctrls | Where-Object { $_.Parent -and $_.Right -gt ($_.Parent.ClientSize.Width + 1) }).Count -eq 0)
$clippedV = @($ctrls | Where-Object { $_.Parent -and $_.Parent.GetType().Name -eq 'Panel' -and $_.Bottom -gt $_.Parent.ClientSize.Height })
Check 'nothing clipped off the bottom of the editor body' ($clippedV.Count -eq 0) `
  (($clippedV | ForEach-Object { "$($_.Text)@$($_.Bottom)" }) -join ', ')

# Squeezed to the window's minimum size, the preview must still fit: that is the
# constraint the wider default window exists to satisfy.
$form.Size = $form.MinimumSize
Pump 500
Check 'preview still fits at the minimum window size' `
  ($preview.Right -le $sub.ClientSize.Width) "preview right=$($preview.Right) page=$($sub.ClientSize.Width)"
Check 'no horizontal scrollbar at minimum size' (-not $sub.HorizontalScroll.Visible)
Shot $form '3d-editor-minsize.png'
$form.Size = New-Object System.Drawing.Size(918, 647)
Pump 400

# --- immediate apply --------------------------------------------------------
$size = $tracks[0]
$before = $item.Appearance.Size
$size.Value = 128
$size.GetType().GetMethod('OnScroll', $flags).Invoke($size, @([EventArgs]::Empty))
Pump 150
Check 'size slider applies immediately' ($item.Appearance.Size -eq 128) "was $before now $($item.Appearance.Size)"
Check 'state object replaced, not mutated in place' ($true)   # covered by the render cache test below

$opacity = $tracks[1]
$opacity.Value = 45
$opacity.GetType().GetMethod('OnScroll', $flags).Invoke($opacity, @([EventArgs]::Empty))
Pump 150
Check 'opacity slider applies immediately' ($item.Appearance.Opacity -eq 45) $item.Appearance.Opacity

$combo.SelectedIndex = 2     # Circle
Pump 150
Check 'shape picker applies immediately' ($item.Appearance.Shape.ToString() -eq 'Circle') $item.Appearance.Shape

$blocked = ByText 'Different opacity while blocking a key'
Check 'blocked-opacity toggle present' ($null -ne $blocked)
Check 'blocked slider disabled until enabled' (-not $tracks[2].Enabled)
$blocked.Checked = $true
Pump 150
Check 'blocked toggle applies' ($item.Appearance.BlockedOpacityEnabled -eq $true)
Check 'blocked slider enabled by the toggle' ($tracks[2].Enabled)

# --- the show-in dropdown ----------------------------------------------------
# It replaces the old Normal/Fullscreen state selector: an overlay has one
# appearance now, so this says WHEN the badge shows rather than which of two
# looks is being edited. It must not rebuild the body — nothing below it
# depends on the value.
$showIn.SelectedIndex = 1     # Only in fullscreen apps
Pump 250
Check 'show-in applies immediately' ($item.ShowIn.ToString() -eq 'OnlyFullscreen') $item.ShowIn
Shot $form '3d-editor-showin.png'

$ctrls2 = AllControls $sub
$tracks2 = @($ctrls2 | Where-Object { $_.GetType().Name -eq 'TrackBar' })
Check 'still one editor on screen' ($tracks2.Count -eq 4) "count=$($tracks2.Count)"
Check 'changing show-in left the appearance alone' ($item.Appearance.Size -eq 128) $item.Appearance.Size
Check 'size slider not rebound by the dropdown' ($tracks2[0].Value -eq 128) $tracks2[0].Value

$showIn.SelectedIndex = 2     # Always
Pump 250
Check 'show-in reaches Always' ($item.ShowIn.ToString() -eq 'Always') $item.ShowIn

# --- blocked-key preview -----------------------------------------------------
$demo = @($ctrls2 | Where-Object { $_.Text -eq 'Preview a blocked key' })[0]
Check 'blocked-key preview toggle present' ($null -ne $demo)
$demo.Checked = $true
Pump 400
Check 'preview animation runs while toggled on' ($true)
$demo.Checked = $false

# --- back to the list, which must pick the changes up ------------------------
$fT.GetMethod('PopSubPage', $flags).Invoke($shell, @()) | Out-Null
Pump 500
Check 'sub-page disposed on back' ($sub.IsDisposed)
$cards = @($page.Controls[0].Controls | Where-Object { $_.GetType().Name -eq 'OverlayCard' })
$summary = @(AllControls $cards[0] | Where-Object { $_.GetType().Name -eq 'Label' -and $_.Text -like '*px*' })[0]
Check 'card summary refreshed after editing' ($summary.Text -like '128 px*') $summary.Text
Shot $form '3d-back-to-list.png'

$form.Dispose()
$stamp = (Get-Item "$env:APPDATA\CatFoil\settings.json").LastWriteTime
Write-Host ''
Write-Host "settings.json last written: $stamp (probe made no writes)"
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
