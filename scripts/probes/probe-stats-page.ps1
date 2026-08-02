# Step 5 of the settings restructure: Statistics is a page in the settings
# shell, not a separate StatsForm dialog. Asserts the page renders the three
# counters (with a live in-progress lock added in), reset zeroes them and tells
# the owner, the refresh timer only runs while the page is on screen, and the
# tray can route to the page (SelectPage). Constructs forms only — no keyboard
# hook, no tray, no lock.
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
# SAFETY NET (see probe-settings.ps1): Settings.Save() writes the user's REAL
# settings.json and cannot be redirected. Snapshot and restore byte-for-byte.
# This probe never calls Save itself — ResetCounters deliberately doesn't save —
# and never pumps after an edit, but the guard is enforced, not remembered.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$asm      = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$formType = $asm.GetType('CatFoil.SettingsForm')
$pageType = $asm.GetType('CatFoil.StatisticsPage')
$settingsType = $asm.GetType('CatFoil.Settings')
$flags    = [Reflection.BindingFlags]'Instance,NonPublic'

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Pump { param($ms = 300)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
}

Check 'StatisticsPage type exists' ($null -ne $pageType)
Check 'StatsForm is gone' ($null -eq $asm.GetType('CatFoil.StatsForm'))
if ($null -eq $pageType) { throw 'no StatisticsPage — nothing further to probe' }

# Lifetime counters the page must render. 3700 s + the fake 90 s in-progress
# lock = 3790 s = "1h 3m 10s".
$settings = [Activator]::CreateInstance($settingsType)
$settings.StatLockSessions = 12
$settings.StatLockedSeconds = 3700
$settings.StatBlockedKeys = 345

$script:resetHits = 0
$inProgress = [Func[long]]{ 90L }
$onReset    = [Action]{ $script:resetHits++ }

$form = [Activator]::CreateInstance($formType,
  @($settings.PSObject.BaseObject, $inProgress, $onReset))
try {
  $form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
  $form.Location = New-Object System.Drawing.Point(80, 60)
  $form.Show()
  Pump 400

  $nav   = $formType.GetField('_nav', $flags).GetValue($form)
  $pages = $formType.GetField('_pages', $flags).GetValue($form)
  $statsIndex = -1
  for ($i = 0; $i -lt $nav.Items.Count; $i++) {
    if ($nav.Items[$i] -eq 'Statistics') { $statsIndex = $i }
  }
  Check 'nav has a Statistics entry' ($statsIndex -ge 0) "items: $($nav.Items -join ', ')"

  $page = $pages | Where-Object { $_.GetType() -eq $pageType } | Select-Object -First 1
  Check 'shell owns a StatisticsPage' ($null -ne $page)

  # Not selected yet: the refresh timer must not be burning a tick per second
  # for a page nobody is looking at.
  $refresh = $pageType.GetField('_refresh', $flags).GetValue($page)
  Check 'refresh timer idle while page never shown' (-not $refresh.Enabled)

  $nav.SelectedIndex = $statsIndex
  Pump 400

  $sessions = $pageType.GetField('_sessions', $flags).GetValue($page)
  $time     = $pageType.GetField('_time', $flags).GetValue($page)
  $blocked  = $pageType.GetField('_blocked', $flags).GetValue($page)
  Check 'sessions rendered' ($sessions.Text -eq '12') "got '$($sessions.Text)'"
  Check 'blocked keys rendered' ($blocked.Text -eq '345') "got '$($blocked.Text)'"
  Check 'time includes the in-progress lock' ($time.Text -eq '1h 3m 10s') "got '$($time.Text)'"
  Check 'refresh timer ticking while page shown' $refresh.Enabled

  # Reset: zeroes the counters, tells the owner (so an in-progress session's
  # clock restarts), repaints — but does NOT save; the click handler owns the
  # confirm + save, which a probe must never trigger.
  $pageType.GetMethod('ResetCounters', $flags).Invoke($page.PSObject.BaseObject, @()) | Out-Null
  Check 'reset zeroes sessions' ($settings.StatLockSessions -eq 0)
  Check 'reset zeroes locked seconds' ($settings.StatLockedSeconds -eq 0)
  Check 'reset zeroes blocked keys' ($settings.StatBlockedKeys -eq 0)
  Check 'reset notified the owner' ($script:resetHits -eq 1) "hits: $($script:resetHits)"
  Check 'display refreshed after reset' ($time.Text -eq '1m 30s') "got '$($time.Text)'"

  # Leaving the page stops the ticking.
  $nav.SelectedIndex = 0
  Pump 200
  Check 'refresh timer stops when page hidden' (-not $refresh.Enabled)

  # The tray routes "Statistics..." here: SelectPage<StatisticsPage>().
  $select = $formType.GetMethod('SelectPage', $flags)
  Check 'SelectPage<T> exists' ($null -ne $select)
  if ($null -ne $select) {
    $select.MakeGenericMethod($pageType).Invoke($form.PSObject.BaseObject, @()) | Out-Null
    Pump 200
    Check 'SelectPage lands on Statistics' ($nav.SelectedIndex -eq $statsIndex)
  }
}
finally { $form.Dispose() }

# The one-arg constructor other probes use must keep working (no live hooks:
# the page shows the counters without an in-progress session).
$form2 = [Activator]::CreateInstance($formType, @([Activator]::CreateInstance($settingsType).PSObject.BaseObject))
try { Check 'one-arg constructor still works' ($null -ne $form2) }
finally { $form2.Dispose() }

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
