# Confirms a page scrolls once its content outgrows the window — the layout
# contract every future settings page depends on. No disk writes.
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

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT  = $asm.GetType('CatFoil.Settings')
$fT  = $asm.GetType('CatFoil.SettingsForm')
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance

$form = [Activator]::CreateInstance($fT, @([Activator]::CreateInstance($sT)))
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(80, 60)
$form.MinimumSize = New-Object System.Drawing.Size(420, 220)
$form.Show()

function Pump { param($ms = 400)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}
Pump 500

$nav = $fT.GetField('_nav', $flags).GetValue($form)
$nav.SelectedIndex = 1        # Locking — the tallest page
Pump 400

# Squeeze the window until the page can't fit its content.
$form.Size = New-Object System.Drawing.Size(560, 260)
Pump 600

$pages = $fT.GetField('_pages', $flags).GetValue($form)
$page  = $pages[1]
Write-Host "page client height : $($page.ClientSize.Height)"
Write-Host "content height     : $($page.DisplayRectangle.Height)"
Write-Host "VScroll visible    : $($page.VerticalScroll.Visible)"

$outDir = $ProbeShots
$bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
try { [Probe.U32]::PrintWindow($form.Handle, $hdc, 2) | Out-Null } finally { $g.ReleaseHdc($hdc); $g.Dispose() }
$bmp.Save((Join-Path $outDir 'squeezed.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$ok = $page.VerticalScroll.Visible
$form.Dispose()
if ($ok) { Write-Host 'PASS: page scrolls when squeezed' } else { Write-Host 'FAIL: content clipped, no scrollbar'; exit 1 }
