# A fullscreen app (e.g. MPC-HC) that raises itself topmost AFTER the badge is
# shown lands above it in the topmost band, and nothing ever put the badge back
# — the bug Steven hit 2026-07-27. The visibility poll must re-assert topmost
# so the badge climbs back above a later-raised topmost window.
# Touches no settings file and never goes near the keyboard hook.
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
# SAFETY NET (see probe-multi-overlay.ps1): snapshot the live settings.json and
# restore it however this script exits.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
'@ -Name Win32Z -Namespace Probe

# Walks the desktop's children top-to-bottom; returns $true when $upper is
# encountered before $lower (i.e. $upper is higher in the z-order).
function IsAbove([IntPtr]$upper, [IntPtr]$lower) {
  $GW_HWNDNEXT = 2
  $h = [Probe.Win32Z]::GetTopWindow([IntPtr]::Zero)
  while ($h -ne [IntPtr]::Zero) {
    if ($h -eq $upper) { return $true }
    if ($h -eq $lower) { return $false }
    $h = [Probe.Win32Z]::GetWindow($h, $GW_HWNDNEXT)
  }
  throw 'neither window found in the z-order walk'
}

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$oT  = $asm.GetType('CatFoil.OverlayForm')
$stT = $asm.GetType('CatFoil.OverlayAppearance')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump { param($ms = 300)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}

$icon   = [System.Drawing.SystemIcons]::Application
$always = [Enum]::Parse($asm.GetType('CatFoil.OverlayShowIn'), 'Always')
$state  = [Activator]::CreateInstance($stT)

$badge = [Activator]::CreateInstance($oT, @($icon, 'probe-topmost'))
$rival = New-Object System.Windows.Forms.Form
try {
  $badge.ApplyAppearance($state.PSObject.BaseObject, $always)
  $badge.ApplySavedPosition($null, 0)
  $badge.SetActive($true)
  Pump 300
  Check 'badge shown' $badge.Visible

  # A topmost window raised AFTER the badge — stands in for MPC-HC fullscreen.
  $rival.TopMost = $true
  $rival.FormBorderStyle = 'None'
  $rival.StartPosition = 'Manual'
  $rival.Bounds = New-Object System.Drawing.Rectangle 0, 0, 60, 60
  $rival.Show()
  Pump 300
  Check 'setup: later topmost window covers the badge' (IsAbove $rival.Handle $badge.Handle)

  # One tick of the visibility poll must climb back above it.
  $upd = $oT.GetMethod('UpdateVisibility', $flags)
  $upd.Invoke($badge, @()) | Out-Null
  Pump 300
  Check 'poll tick re-asserts topmost over a later topmost window' (IsAbove $badge.Handle $rival.Handle)

  # And the rival taking the top again must not stick either (steady state).
  $rival.TopMost = $false; $rival.TopMost = $true
  Pump 100
  $upd.Invoke($badge, @()) | Out-Null
  Check 'reassert holds on every tick' (IsAbove $badge.Handle $rival.Handle)
}
finally { $rival.Dispose(); $badge.Dispose() }

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
