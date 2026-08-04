# One window: settings lives INSIDE MainForm (SettingsForm deleted). Asserts
# the enter/leave view switch and its sizing, the persistent return button on
# every page and sub-page, EndVisit on the close path, Escape semantics,
# lock-does-not-resize, and the per-view size persistence model.
# Constructs forms only — no keyboard hook, no tray, no lock. RecordViewSize is
# invoked directly instead of simulating a drag, so Settings.Save() NEVER runs.
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
# 0. The one-window world exists
# ---------------------------------------------------------------
Check 'SettingsShell exists' ($null -ne $shT)
Check 'SettingsForm is gone' ($null -eq $asm.GetType('CatFoil.SettingsForm'))
if ($null -eq $shT) { throw 'no shell — nothing further to probe' }

# ---------------------------------------------------------------
# 1. Size persistence model (JSON only — no file I/O, no Save)
# ---------------------------------------------------------------
$json = '{"MainWindowSize":{"Width":777,"Height":555},"SettingsWindowSize":{"Width":902,"Height":722}}'
$loaded = [Text.Json.JsonSerializer]::Deserialize($json, $sT)
Check 'MainWindowSize round-trips'     ($loaded.MainWindowSize.Width -eq 777 -and $loaded.MainWindowSize.Height -eq 555)
Check 'SettingsWindowSize round-trips' ($loaded.SettingsWindowSize.Width -eq 902 -and $loaded.SettingsWindowSize.Height -eq 722)
$fresh = [Activator]::CreateInstance($sT)
$out = [Text.Json.JsonSerializer]::Serialize($fresh, $sT)
Check 'unset sizes are not written' (($out -notmatch 'MainWindowSize') -and ($out -notmatch 'SettingsWindowSize'))

# ---------------------------------------------------------------
# 2. MainForm: one size, and locking never changes it
# ---------------------------------------------------------------
$settings = [Activator]::CreateInstance($sT)
$mf = [Activator]::CreateInstance($mfT, @($settings.PSObject.BaseObject))
$mf.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$mf.Location = New-Object System.Drawing.Point(80, 60)
$mf.Show(); Pump 400

$mainSize = $mf.ClientSize
Check 'main view opens at the one default size' ($mainSize.Width -eq 700 -and $mainSize.Height -eq 440) "$mainSize"

$mf.SetLockedUi($true);  Pump 150
Check 'locking does not resize the window'   ($mf.ClientSize -eq $mainSize) "$($mf.ClientSize)"
$mf.SetLockedUi($false); Pump 150
Check 'unlocking does not resize the window' ($mf.ClientSize -eq $mainSize) "$($mf.ClientSize)"

# ---------------------------------------------------------------
# 3. Enter settings: the window grows, the view switches
# ---------------------------------------------------------------
$shell = [Activator]::CreateInstance($shT, @($settings.PSObject.BaseObject))
$enter  = $mfT.GetMethod('EnterSettings', $flags)
$leave  = $mfT.GetMethod('LeaveSettings', $flags)
Check 'EnterSettings exists' ($null -ne $enter)
Check 'LeaveSettings exists' ($null -ne $leave)

$enter.Invoke($mf, @($shell.PSObject.BaseObject)) | Out-Null
Pump 400

$mainView   = $mfT.GetField('_mainView', $flags).GetValue($mf)
$returnBtn  = $mfT.GetField('_returnButton', $flags).GetValue($mf)
Check 'settings view is at least the shell minimum' `
  ($mf.ClientSize.Width -ge 840 -and $mf.ClientSize.Height -ge 560) "$($mf.ClientSize)"
Check 'minimum size raised for settings' ($mf.MinimumSize.Width -ge 840) "$($mf.MinimumSize)"
Check 'main view hidden in settings' (-not $mainView.Visible)
Check 'shell visible in settings' ($shell.Visible)
Check 'return button visible on a page' ($returnBtn.Visible)

# The button stays put across page navigation and on a sub-page.
$shT.GetMethod('SelectPage', $flags, $null, [Type[]]@([string]), $null).Invoke($shell, @('Advanced')) | Out-Null
Pump 250
Check 'return button visible after SelectPage' ($returnBtn.Visible)

$sess = $shT.GetField('_session', $flags).GetValue($shell)
$sub  = [Activator]::CreateInstance($asm.GetType('CatFoil.AboutPage'), @($sess))
$shT.GetMethod('ShowSubPage', $flags).Invoke($shell, @($sub))
Pump 250
Check 'return button visible on a sub-page' ($returnBtn.Visible)

# ---------------------------------------------------------------
# 4. Escape: sub-page pops; top level leaves; the window stays open
# ---------------------------------------------------------------
function SendEscape($target, $type) {
  $msg = New-Object System.Windows.Forms.Message
  $a = New-Object object[] 2
  $a[0] = $msg
  $a[1] = [System.Windows.Forms.Keys]::Escape
  $type.GetMethod('ProcessCmdKey', $flags).Invoke($target, $a) | Out-Null
}
SendEscape $mf $mfT
Pump 250
Check 'Escape pops the sub-page' ($null -eq $shT.GetField('_subPage', $flags).GetValue($shell))
Check 'still in settings after the pop' (-not $mainView.Visible)

SendEscape $mf $mfT
Pump 250
Check 'Escape on a page leaves settings' ($mainView.Visible)
Check 'the window is still open' ((-not $mf.IsDisposed) -and $mf.Visible)
Check 'leaving restores the main size' ($mf.ClientSize -eq $mainSize) "$($mf.ClientSize)"
Check 'minimum size restored' ($mf.MinimumSize.Width -lt 840) "$($mf.MinimumSize)"

$endVisits = $shT.GetProperty('EndVisitCount', $flags)
Check 'leaving ended the visit' ($endVisits.GetValue($shell) -eq 1) "count=$($endVisits.GetValue($shell))"

# ---------------------------------------------------------------
# 5. Per-view sizes: recorded into the right Settings fields, remembered
# ---------------------------------------------------------------
$record = $mfT.GetMethod('RecordViewSize', $flags)
Check 'RecordViewSize exists' ($null -ne $record)

$enter.Invoke($mf, @($shell.PSObject.BaseObject)) | Out-Null; Pump 250
$mf.ClientSize = New-Object System.Drawing.Size(940, 760); Pump 150
$record.Invoke($mf, @()) | Out-Null
Check 'settings-view size lands in SettingsWindowSize' `
  ($settings.SettingsWindowSize.Width -eq 940 -and $settings.SettingsWindowSize.Height -eq 760) "$($settings.SettingsWindowSize)"
Check 'main size untouched by a settings resize' ($null -eq $settings.MainWindowSize)

$leave.Invoke($mf, @()) | Out-Null; Pump 250
$mf.ClientSize = New-Object System.Drawing.Size(720, 460); Pump 150
$record.Invoke($mf, @()) | Out-Null
Check 'main-view size lands in MainWindowSize' `
  ($settings.MainWindowSize.Width -eq 720 -and $settings.MainWindowSize.Height -eq 460) "$($settings.MainWindowSize)"

$enter.Invoke($mf, @($shell.PSObject.BaseObject)) | Out-Null; Pump 250
Check 'settings view remembers its size' ($mf.ClientSize.Width -eq 940 -and $mf.ClientSize.Height -eq 760) "$($mf.ClientSize)"
$leave.Invoke($mf, @()) | Out-Null; Pump 250
Check 'main view remembers its size' ($mf.ClientSize.Width -eq 720 -and $mf.ClientSize.Height -eq 460) "$($mf.ClientSize)"

# ---------------------------------------------------------------
# 6. The close path while in settings ends the visit and resets the view
# ---------------------------------------------------------------
$enter.Invoke($mf, @($shell.PSObject.BaseObject)) | Out-Null; Pump 250
$countBefore = $endVisits.GetValue($shell)
$mf.Close()          # MinimizeToTrayOnClose default: cancels + hides
Pump 250
Check 'close hides to tray, not disposes' (-not $mf.IsDisposed)
Check 'window hidden by close' (-not $mf.Visible)
# A child's Visible getter reports effective visibility, which is false while
# the whole form is hidden — assert the view flag, then what re-opening shows.
Check 'close while in settings resets to the main view' `
  (-not $mfT.GetField('_inSettings', $flags).GetValue($mf))
Check 'close ended the visit' ($endVisits.GetValue($shell) -gt $countBefore) "count=$($endVisits.GetValue($shell))"

# ---------------------------------------------------------------
# 7. Locking while in settings snaps to the main view, same size
# ---------------------------------------------------------------
$mf.Show(); Pump 250
Check 'reopening after close shows the main view' ($mainView.Visible)
$enter.Invoke($mf, @($shell.PSObject.BaseObject)) | Out-Null; Pump 250
$mf.SetLockedUi($true); Pump 250
Check 'locking in settings shows the main view' ($mainView.Visible)
Check 'locking in settings lands on the main size' ($mf.ClientSize.Width -eq 720 -and $mf.ClientSize.Height -eq 460) "$($mf.ClientSize)"
$mf.SetLockedUi($false); Pump 150

$mf.Dispose()   # dispose, never EndVisit again — nothing was edited, nothing to flush

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
