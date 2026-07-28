# A stale tracked-down modifier kills the unlock hotkey while blocking keeps
# working — the bug Steven hit 2026-07-27. The hook misses a key-UP whenever
# input goes somewhere it can't see (secure desktop: Ctrl+Alt+Del / UAC / Win+L,
# or the dead window before the watchdog re-installs a dropped hook), and
# MatchesUnlockCombo requires non-combo modifiers to be UP, so the flag jams the
# hotkey until that key is physically pressed again.
#
# The fix under test: KeyboardHook.ResyncModifiers() — called by the 60 s
# watchdog — clears tracked-down keys that GetAsyncKeyState reports up, guarded
# by a quiet period so a genuinely-held (swallowed, hence OS-"up") modifier
# mid-combo is not wrongly cleared.
#
# The hook is NEVER installed here: state is driven purely by reflection, so the
# probe cannot lock the keyboard. No settings file is touched either (snapshot
# guard kept anyway, by convention).
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$hT  = $asm.GetType('CatFoil.KeyboardHook')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}

$hook = [Activator]::CreateInstance($hT)
try {
  $hook.UnlockCombo = [System.Windows.Forms.Keys]([System.Windows.Forms.Keys]::G -bor [System.Windows.Forms.Keys]::Alt)

  $matches = $hT.GetMethod('MatchesUnlockCombo', $flags)
  function TryUnlock { [bool]$matches.Invoke($hook.PSObject.BaseObject, @([System.Windows.Forms.Keys]::G)) }
  function SetFlag($name, $value) { $hT.GetField($name, $flags).SetValue($hook.PSObject.BaseObject, $value) }
  function GetFlag($name) { $hT.GetField($name, $flags).GetValue($hook.PSObject.BaseObject) }

  # Baseline behavior, true before and after the fix.
  SetFlag '_lAlt' $true
  Check 'sanity: Alt held -> Alt+G matches' (TryUnlock)
  SetFlag '_lCtrl' $true
  Check 'symptom: stale Ctrl flag jams Alt+G' (-not (TryUnlock))
  SetFlag '_lAlt' $false; SetFlag '_lCtrl' $false

  # --- The fix ---
  $resync  = $hT.GetMethod('ResyncModifiers')
  $tickFld = $hT.GetField('_lastTrackedKeyTick', $flags)
  Check 'ResyncModifiers() exists' ($null -ne $resync)
  Check '_lastTrackedKeyTick exists' ($null -ne $tickFld)

  if ($resync -and $tickFld) {
    $now = [Environment]::TickCount
    function AgeTick { $tickFld.SetValue($hook.PSObject.BaseObject, [Environment]::TickCount - 6000) }

    # Stale Ctrl (no physical Ctrl held while this probe runs), quiet period
    # elapsed -> the watchdog resync clears it and the hotkey works again.
    SetFlag '_lCtrl' $true
    AgeTick
    $resync.Invoke($hook.PSObject.BaseObject, @()) | Out-Null
    Check 'quiet resync clears a stale modifier' (-not (GetFlag '_lCtrl'))
    SetFlag '_lAlt' $true   # the user presses Alt+G again
    Check 'hotkey works after resync' (TryUnlock)
    SetFlag '_lAlt' $false

    # A modifier event seen moments ago means the "down" may be a genuinely
    # held, swallowed key whose OS state lies "up" — resync must not touch it.
    SetFlag '_lCtrl' $true
    $tickFld.SetValue($hook.PSObject.BaseObject, [Environment]::TickCount)
    $resync.Invoke($hook.PSObject.BaseObject, @()) | Out-Null
    Check 'recent activity guards a held modifier' (GetFlag '_lCtrl')
    SetFlag '_lCtrl' $false

    # Chord keys jam the same way (their downs are swallowed while locked, and
    # CompletesChord needs every key down at once) and resync the same way.
    $hook.SetChordKeys(@([System.Windows.Forms.Keys]::C, [System.Windows.Forms.Keys]::F))
    $chordDown = $hT.GetField('_chordDown', $flags).GetValue($hook.PSObject.BaseObject)
    $chordDown[0] = $true
    AgeTick
    $resync.Invoke($hook.PSObject.BaseObject, @()) | Out-Null
    Check 'quiet resync clears a stale chord key' (-not $chordDown[0])
  }
}
finally { $hook.Dispose() }

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
