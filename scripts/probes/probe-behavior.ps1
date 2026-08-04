# Verifies the immediate-apply wiring WITHOUT ever writing to disk.
# The debounce is 500ms, so the probe asserts and tears down before it can tick,
# and never closes the form (OnFormClosed flushes). Nothing touches
# %APPDATA%\CatFoil\settings.json.
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

$asm  = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT   = $asm.GetType('CatFoil.Settings')
$fT   = $asm.GetType('CatFoil.SettingsShell')
if (-not $fT) { throw 'SettingsShell type not found — probe would false-pass.' }
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance

$settings = [Activator]::CreateInstance($sT)
$shell    = [Activator]::CreateInstance($fT, @($settings))
$form = New-Object System.Windows.Forms.Form
$form.ClientSize = New-Object System.Drawing.Size(900, 720)
$shell.Dock = [System.Windows.Forms.DockStyle]::Fill
$form.Controls.Add($shell)

$script:changed  = 0
$script:captures = @()
# A real delegate, not Register-ObjectEvent: the latter queues the handler for
# the PowerShell event loop, so it would never run in a probe that deliberately
# avoids pumping after the edit.
$fT.GetEvent('SettingsSaved').AddEventHandler($shell, [Action]{ $script:changed++ })

$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(80, 60)
$form.Show()

function Pump { param($ms = 300)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}
function Find-Control { param($root, $predicate)
  foreach ($c in $root.Controls) {
    if (& $predicate $c) { return $c }
    $hit = Find-Control $c $predicate
    if ($hit) { return $hit }
  }
  return $null
}

Pump 600   # safe: no edit yet, so a debounce tick would have nothing to write

$pages   = $fT.GetField('_pages', $flags).GetValue($shell)
$session = $fT.GetField('_session', $flags).GetValue($shell)
$sessT   = $session.GetType()

$pass = $true
function Check { param($name, $ok, $detail = '')
  $status = if ($ok) { 'PASS' } else { 'FAIL'; }
  if (-not $ok) { $script:pass = $false }
  Write-Host ("[{0}] {1} {2}" -f $status, $name, $detail)
}

# --- 1. Hotkey capture suspends/resumes the global hotkey ---
$nav = $fT.GetField('_nav', $flags).GetValue($shell)
$nav.SelectedIndex = 1        # Locking
Pump 300
$txt = Find-Control $pages[1] { param($c) $c -is [System.Windows.Forms.TextBox] }
Check 'hotkey box found' ($null -ne $txt)
Check 'hotkey box shows default binding' ($txt.Text -eq 'Alt + G') "(got '$($txt.Text)')"

$capture = $null
$handler = [Action[bool]]{ param($v) $script:captures += $v }
$evt = $fT.GetEvent('HotkeyCaptureChanged')
$evt.AddEventHandler($shell, $handler)

$txt.Focus() | Out-Null
Pump 200
$other = Find-Control $pages[1] { param($c) $c -is [System.Windows.Forms.CheckBox] }
$other.Focus() | Out-Null
Pump 200
Check 'capture raised true then false' ("$($script:captures -join ',')" -eq 'True,False') "(got '$($script:captures -join ',')')"

# --- 2. A checkbox edit reaches Settings and announces itself, no Save yet ---
$nav.SelectedIndex = 0        # General
Pump 300
$chk = Find-Control $pages[0] { param($c) $c -is [System.Windows.Forms.CheckBox] -and $c.Text -like 'Start CatFoil*' }
Check 'startup checkbox found' ($null -ne $chk)

$before = $settings.StartWithWindows
$changedBefore = $script:changed
$chk.Checked = -not $before
# NOTE: no Pump here — the 500ms debounce must not get a chance to tick.

Check 'edit applied to Settings in memory' ($settings.StartWithWindows -eq (-not $before))
Check 'change announced live' ($script:changed -gt $changedBefore) "(fired $($script:changed - $changedBefore)x)"

$pending = $sessT.GetField('_savePending', $flags).GetValue($session)
Check 'a disk write is queued' ($pending -eq $true)

# --- 3. Nothing was written ---
$json = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'CatFoil\settings.json'
$stamp = (Get-Item $json -ErrorAction SilentlyContinue).LastWriteTimeUtc
Write-Host "settings.json last written: $stamp (probe made no writes)"

# Dispose directly rather than Close(): Close() would flush the pending edit.
$form.Dispose()

if ($pass) { Write-Host 'ALL PASS' } else { Write-Host 'FAILURES ABOVE'; exit 1 }

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
