# The multi-key chord OPTION is removed (Steven, 2026-08-02): it felt wrong to
# offer a lock hotkey whose leading keys leak into the focused app while
# unlocked. The KeyboardHook chord ENGINE stays, dormant and probe-covered
# (probe-hook-resync, probe-unlock-cue), so the option can come back cheaply —
# but nothing may arm it: no UI, no ApplyHotkeySettings branch, no display path.
# The stored chord settings survive in settings.json, unread, so a future
# re-add restores the user's chord instead of forgetting it.
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}

# ---------------------------------------------------------------
# 1. Settings still carry the chord fields (data preserved, just unread)
# ---------------------------------------------------------------
$sT = $asm.GetType('CatFoil.Settings')
foreach ($p in 'UseChordHotkey', 'ChordModifiers', 'ChordKeys') {
  Check "Settings.$p survives (legacy, for a future re-add)" ($null -ne $sT.GetProperty($p))
}

# Numeric Keys values (Alt=262144, C=67, F=70, Alt|G=262215): plain Deserialize
# here has no enum-string converter, and the numbers load either way.
$json = '{"UseChordHotkey":true,"ChordModifiers":262144,"ChordKeys":[67,70],"Hotkey":262215}'
$settings = [Text.Json.JsonSerializer]::Deserialize($json, $sT)
Check 'legacy chord JSON still loads' ($null -ne $settings)

# ---------------------------------------------------------------
# 2. Nothing renders the chord: the active hotkey is the classic combo
#    even when the old settings say chord mode
# ---------------------------------------------------------------
$hText = $asm.GetType('CatFoil.HotkeyText')
$parts = [string[]]$hText.GetMethod('ActiveParts').Invoke($null, @($settings.PSObject.BaseObject))
Check 'ActiveParts ignores chord mode' (($parts -join '+') -eq 'Alt+G') "got '$($parts -join '+')'"

# ---------------------------------------------------------------
# 3. The Locking page offers no chord option
# ---------------------------------------------------------------
$lT = $asm.GetType('CatFoil.LockingPage')
$flags = [Reflection.BindingFlags]'Instance,NonPublic,Public'
Check 'LockingPage has no chord checkbox field' `
      ($null -eq ($lT.GetFields($flags) | Where-Object { $_.Name -like '*hord*' }))

$sessT = $asm.GetType('CatFoil.SettingsSession')
$sess  = [Activator]::CreateInstance($sessT, @($settings.PSObject.BaseObject))
$page  = [Activator]::CreateInstance($lT, @($sess.PSObject.BaseObject))
try {
  # A List, not @() — += inside a nested scriptblock writes a new local array
  # and the outer one stays empty, false-passing the "no chord" check.
  $texts = [Collections.Generic.List[string]]::new()
  $walk = { param($c) $texts.Add([string]$c.Text); foreach ($k in $c.Controls) { & $walk $k } }
  & $walk $page
  Check 'controls were actually walked' ($texts.Count -gt 3) "count=$($texts.Count)"
  Check 'no control on the page mentions a chord' `
        ($null -eq ($texts | Where-Object { $_ -match 'chord' }))
  # The classic hotkey box must still be there and showing the combo.
  Check 'the hotkey box still renders the classic combo' `
        ($null -ne ($texts | Where-Object { $_ -eq 'Alt + G' }))
}
finally {
  $page.Dispose()
  $sess.Dispose()
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
