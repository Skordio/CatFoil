# Step 3a: the Overlays list + shell sub-page navigation.
# NEVER pumps 500ms after an edit and disposes rather than closes, so
# Settings.Save() never runs and the live settings.json is untouched.
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

$root = $RepoRoot
$out  = $ProbeShots
$asm  = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT   = $asm.GetType('CatFoil.Settings')
$fT   = $asm.GetType('CatFoil.SettingsShell')
if (-not $fT) { throw 'SettingsShell type not found — probe would false-pass.' }
$itT  = $asm.GetType('CatFoil.OverlayItem')

# The shell is a UserControl; every construction below hosts it in a bare form
# sized like the old SettingsForm so pages lay out at designed dimensions.
function New-ShellHost($settingsObj) {
  $s = [Activator]::CreateInstance($script:fT, @($settingsObj))
  $f = New-Object System.Windows.Forms.Form
  $f.ClientSize  = New-Object System.Drawing.Size(900, 720)
  $f.MinimumSize = New-Object System.Drawing.Size(840, 560)
  $s.Dock = [System.Windows.Forms.DockStyle]::Fill
  $f.Controls.Add($s)
  @($f, $s)   # unrolled on output, so `$form, $shell = New-ShellHost ...` works
}
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

# Build a settings object with THREE overlays, in memory only.
$settings = [Activator]::CreateInstance($sT)
$list = $settings.EnsureOverlays()
foreach ($n in 'Desk cat','Second screen') {
  $it = [Activator]::CreateInstance($itT); $it.Name = $n; $list.Add($it)
}
$list[0].Name = 'Overlay'
$list[1].Appearance.Size = 128
$list[2].Enabled = $false
$list[2].ShowIn = [Enum]::Parse($asm.GetType('CatFoil.OverlayShowIn'), 'OnlyFullscreen')

$form, $shell = New-ShellHost $settings
$form.Icon = [System.Drawing.SystemIcons]::Application
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(80, 60)
$form.Show()
Pump 500

$nav  = $fT.GetField('_nav', $flags).GetValue($shell)
$back = $fT.GetField('_back', $flags).GetValue($shell)
$hdr  = $fT.GetField('_header', $flags).GetValue($shell)
$host_ = $fT.GetField('_host', $flags).GetValue($shell)
$pages = $fT.GetField('_pages', $flags).GetValue($shell)

$nav.SelectedIndex = 2      # Overlays
Pump 500
Shot $form '3a-overlays-list.png'

# --- the list itself --------------------------------------------------------
$page = $pages[2]
$stack = $page.Controls[0]
$cards = @($stack.Controls | Where-Object { $_.GetType().Name -eq 'OverlayCard' })
Check 'one card per overlay' ($cards.Count -eq 3) "cards=$($cards.Count)"
Check 'cards stretch to the page width' ($cards[0].Width -gt 380) "w=$($cards[0].Width)"
# Read the constant rather than repeating it: a layout tweak shouldn't fail this.
$declaredH = $cards[0].GetType().GetField('CardHeight').GetValue($null)
Check 'cards are the declared height' ($cards[0].Height -eq $declaredH) "h=$($cards[0].Height) declared=$declaredH"
Check 'cards do not overlap' ($cards[1].Top -ge $cards[0].Bottom) "$($cards[0].Bounds) / $($cards[1].Bounds)"
Check 'card children stay inside the card' `
  (@($cards[0].Controls | Where-Object { $_.Right -gt $cards[0].ClientSize.Width -or $_.Bottom -gt $cards[0].ClientSize.Height }).Count -eq 0)

# Duplicate/Remove live in the card's overflow menu, not as buttons.
$cardT = $cards[0].GetType()
function MenuItem($card, $text) {
  $menu = $cardT.GetField('_menu', $flags).GetValue($card)
  @($menu.Items | Where-Object { $_.Text -eq $text })[0]
}
Check 'card offers Duplicate' ($null -ne (MenuItem $cards[0] 'Duplicate'))
Check 'Remove enabled with 3 overlays' ((MenuItem $cards[0] 'Remove').Enabled)
Check 'disabled overlay shows unchecked' `
  ((@($cards[2].Controls | Where-Object { $_.Text -eq 'Enabled' })[0]).Checked -eq $false)

# --- sub-page navigation ----------------------------------------------------
Check 'back button hidden on a nav page' (-not $back.Visible)
Check 'header is the page title' ($hdr.Text -eq 'Overlays') $hdr.Text

$subT = $asm.GetType('CatFoil.AboutPage')   # any SettingsPage will do as a stand-in
$sess = $fT.GetField('_session', $flags).GetValue($shell)
$sub  = [Activator]::CreateInstance($subT, @($sess))
$fT.GetMethod('ShowSubPage', $flags).Invoke($shell, @($sub))
Pump 300
Shot $form '3a-subpage.png'

Check 'sub-page is visible' ($sub.Visible)
Check 'parent page hidden' (-not $page.Visible)
Check 'back button shown' ($back.Visible)
Check 'header shows a breadcrumb' ($hdr.Text -like 'Overlays*About') $hdr.Text

# Escape must go back, not close the window. Invoke ProcessCmdKey directly:
# SendKeys needs a genuinely foreground window, which a probe doesn't have, and
# it silently delivers nothing rather than failing.
function SendEscape {
  $msg = New-Object System.Windows.Forms.Message
  $a = New-Object object[] 2
  $a[0] = $msg
  $a[1] = [System.Windows.Forms.Keys]::Escape
  $fT.GetMethod('ProcessCmdKey', $flags).Invoke($shell, $a) | Out-Null
}
SendEscape
Pump 400
Check 'Escape pops instead of closing' (-not $form.IsDisposed)
Check 'parent page restored' ($page.Visible)
Check 'back button hidden again' (-not $back.Visible)
Check 'sub-page disposed on pop' ($sub.IsDisposed)
Check 'sub-page removed from host' (-not $host_.Controls.Contains($sub))

# Switching nav away from a sub-page must not leave it on screen.
$sub2 = [Activator]::CreateInstance($subT, @($sess))
$fT.GetMethod('ShowSubPage', $flags).Invoke($shell, @($sub2))
Pump 250
$nav.SelectedIndex = 0      # General
Pump 300
Check 'nav switch discards the sub-page' ($sub2.IsDisposed)
Check 'nav switch shows the new page' ($pages[0].Visible)
Check 'only one page visible' (@($host_.Controls | Where-Object { $_.Visible }).Count -eq 1)

# Re-clicking the already-selected nav row pops (no SelectedIndexChanged fires).
$nav.SelectedIndex = 2
Pump 250
$sub3 = [Activator]::CreateInstance($subT, @($sess))
$fT.GetMethod('ShowSubPage', $flags).Invoke($shell, @($sub3))
Pump 250
$rowY = $nav.GetItemRectangle(2).Y + 10
$md = New-Object System.Windows.Forms.MouseEventArgs(
  [System.Windows.Forms.MouseButtons]::Left, 1, 20, $rowY, 0)
# .PSObject.BaseObject — PowerShell re-wraps the arg on the way into an
# object[], and reflection refuses the wrapper. A plain cast is not enough.
$margs = New-Object object[] 1
$margs[0] = $md.PSObject.BaseObject
$nav.GetType().GetMethod('OnMouseDown', $flags).Invoke($nav, $margs)
Pump 300
Check 're-click on the same nav row pops' ($sub3.IsDisposed)
Check 'overlays page restored by re-click' ($page.Visible)

# --- the single-overlay case: Remove must be refused ------------------------
$solo = [Activator]::CreateInstance($sT)
$solo.EnsureOverlays() | Out-Null
$soloForm, $soloShell = New-ShellHost $solo
$soloForm.Icon = [System.Drawing.SystemIcons]::Application
$soloForm.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$soloForm.Location = New-Object System.Drawing.Point(900, 60)
$soloForm.Show()
Pump 400
($fT.GetField('_nav', $flags).GetValue($soloShell)).SelectedIndex = 2
Pump 400
$soloPage = ($fT.GetField('_pages', $flags).GetValue($soloShell))[2]
$soloCards = @($soloPage.Controls[0].Controls | Where-Object { $_.GetType().Name -eq 'OverlayCard' })
Check 'single overlay renders one card' ($soloCards.Count -eq 1) "cards=$($soloCards.Count)"
Check 'Remove DISABLED at one overlay' (-not (MenuItem $soloCards[0] 'Remove').Enabled)
Check 'Duplicate still allowed at one overlay' ((MenuItem $soloCards[0] 'Duplicate').Enabled)
$soloForm.Dispose()

# --- default position: three quarters across and up -------------------------
$oT = $asm.GetType('CatFoil.OverlayForm')
$ov = [Activator]::CreateInstance($oT, @([System.Drawing.SystemIcons]::Application, 'probe'))
try {
  $ov.ApplySavedPosition($null, 0)
  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $cx = $ov.Left + $ov.Width / 2
  $cy = $ov.Top + $ov.Height / 2
  $fx = ($cx - $wa.Left) / $wa.Width
  $fy = ($cy - $wa.Top) / $wa.Height
  Write-Host ("  default badge centre: {0:P0} across, {1:P0} down" -f $fx, $fy)
  Check 'default sits ~3/4 across' ([Math]::Abs($fx - 0.75) -lt 0.03) $fx
  Check 'default sits ~3/4 up (1/4 down)' ([Math]::Abs($fy - 0.25) -lt 0.03) $fy
} finally { $ov.Dispose() }

# Dispose (not Close) so the session never flushes to disk.
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
