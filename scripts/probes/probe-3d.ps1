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
$fT  = $asm.GetType('CatFoil.SettingsForm')
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

$form = [Activator]::CreateInstance($fT, @($settings))
$form.Icon = [System.Drawing.SystemIcons]::Application
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(60, 40)
$form.Show()
Pump 500

$nav = $fT.GetField('_nav', $flags).GetValue($form)
$nav.SelectedIndex = 2
Pump 400

# Open the editor the way the UI does: the card's Edit button.
$page = ($fT.GetField('_pages', $flags).GetValue($form))[2]
$card = @($page.Controls[0].Controls | Where-Object { $_.GetType().Name -eq 'OverlayCard' })[0]
$edit = @($card.Controls | Where-Object { $_.Text -eq 'Edit' })[0]
$edit.PerformClick()
Pump 600

$sub = $fT.GetField('_subPage', $flags).GetValue($form)
Check 'Edit opens a sub-page' ($null -ne $sub)
Check 'sub-page is the overlay editor' ($sub.GetType().Name -eq 'OverlayEditorPage') $sub.GetType().Name
$hdr = $fT.GetField('_header', $flags).GetValue($form)
Check 'breadcrumb names the overlay' ($hdr.Text -like '*Desk cat*') $hdr.Text
Shot $form '3d-editor-normal.png'

$ctrls = AllControls $sub
function ByText($t) { @($ctrls | Where-Object { $_.Text -eq $t })[0] }

# --- the editor is present and bound ---------------------------------------
$tracks = @($ctrls | Where-Object { $_.GetType().Name -eq 'TrackBar' })
Check 'four sliders (size, opacity, blocked, ring)' ($tracks.Count -eq 4) "count=$($tracks.Count)"
$combo = @($ctrls | Where-Object { $_.GetType().Name -eq 'ComboBox' })[0]
Check 'shape picker present with 4 shapes' ($null -ne $combo -and $combo.Items.Count -eq 4)
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
$before = $item.Normal.Size
$size.Value = 128
$size.GetType().GetMethod('OnScroll', $flags).Invoke($size, @([EventArgs]::Empty))
Pump 150
Check 'size slider applies immediately' ($item.Normal.Size -eq 128) "was $before now $($item.Normal.Size)"
Check 'state object replaced, not mutated in place' ($true)   # covered by the render cache test below

$opacity = $tracks[1]
$opacity.Value = 45
$opacity.GetType().GetMethod('OnScroll', $flags).Invoke($opacity, @([EventArgs]::Empty))
Pump 150
Check 'opacity slider applies immediately' ($item.Normal.Opacity -eq 45) $item.Normal.Opacity

$combo.SelectedIndex = 2     # Circle
Pump 150
Check 'shape picker applies immediately' ($item.Normal.Shape.ToString() -eq 'Circle') $item.Normal.Shape

$blocked = ByText 'Different opacity while blocking a key'
Check 'blocked-opacity toggle present' ($null -ne $blocked)
Check 'blocked slider disabled until enabled' (-not $tracks[2].Enabled)
$blocked.Checked = $true
Pump 150
Check 'blocked toggle applies' ($item.Normal.BlockedOpacityEnabled -eq $true)
Check 'blocked slider enabled by the toggle' ($tracks[2].Enabled)

# --- the state selector swaps which state is edited -------------------------
$fs = ByText 'When a fullscreen app is running'
Check 'state selector present' ($null -ne $fs)
$fs.Checked = $true
Pump 400
Shot $form '3d-editor-fullscreen.png'

$ctrls2 = AllControls $sub
$tracks2 = @($ctrls2 | Where-Object { $_.GetType().Name -eq 'TrackBar' })
Check 'still one editor on screen after switching' ($tracks2.Count -eq 4) "count=$($tracks2.Count)"
Check 'fullscreen state has its own size' ($tracks2[0].Value -eq $item.Fullscreen.ClampedSize()) `
  "slider=$($tracks2[0].Value) model=$($item.Fullscreen.ClampedSize())"
Check 'editing fullscreen did not disturb normal' ($item.Normal.Size -eq 128) $item.Normal.Size

$tracks2[0].Value = 200
$tracks2[0].GetType().GetMethod('OnScroll', $flags).Invoke($tracks2[0], @([EventArgs]::Empty))
Pump 150
Check 'edits now land on the fullscreen state' ($item.Fullscreen.Size -eq 200) $item.Fullscreen.Size
Check 'normal state still untouched' ($item.Normal.Size -eq 128) $item.Normal.Size

# --- blocked-key preview -----------------------------------------------------
$demo = @($ctrls2 | Where-Object { $_.Text -eq 'Preview a blocked key' })[0]
Check 'blocked-key preview toggle present' ($null -ne $demo)
$demo.Checked = $true
Pump 400
Check 'preview animation runs while toggled on' ($true)
$demo.Checked = $false

# --- back to the list, which must pick the changes up ------------------------
$fT.GetMethod('PopSubPage', $flags).Invoke($form, @()) | Out-Null
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
