# Unlocking plays the blocked-key sound (reported by Steven 2026-08-01). Nobody
# hits both keys of Alt+G in the same instant: Alt lands first, and while locked
# every key-down that isn't the COMPLETED combo raises BlockedKeyPress — cue
# sound, overlay flash, +1 on the blocked-keys stat. The unlock gesture itself
# is treated as a cat.
#
# The fix under test: key-downs that are PART of the unlock gesture — the
# combo's required modifiers, and the chord's keys/modifiers when a chord is
# configured — are still swallowed but raise no BlockedKeyPress. Anything else
# (letters, and modifiers the combo does NOT use) still cues.
#
# The hook is NEVER installed here: HookCallback is invoked directly by
# reflection with a hand-built KBDLLHOOKSTRUCT, so the probe cannot lock the
# keyboard. No settings file is touched (snapshot guard kept by convention).
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms

$asm   = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$hT    = $asm.GetType('CatFoil.KeyboardHook')
$flags = [Reflection.BindingFlags]'Instance,NonPublic'

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}

$WM_KEYDOWN    = 0x0100
$WM_KEYUP      = 0x0101
$WM_SYSKEYDOWN = 0x0104

$hook = [Activator]::CreateInstance($hT)
# KBDLLHOOKSTRUCT stand-in: vkCode is the first DWORD, nothing else is read.
$kbd = [Runtime.InteropServices.Marshal]::AllocHGlobal(40)
try {
  $cb = $hT.GetMethod('HookCallback', $flags)

  # Counters via inline handlers (Register-ObjectEvent never fires in a
  # synchronous probe — see the probes README).
  $counts = @{ blocked = 0; unlock = 0; chord = 0 }
  $hT.GetEvent('BlockedKeyPress').AddEventHandler($hook,  [Action]{ $counts.blocked++ })
  $hT.GetEvent('UnlockComboPressed').AddEventHandler($hook, [Action]{ $counts.unlock++ })
  $hT.GetEvent('ChordPressed').AddEventHandler($hook,     [Action]{ $counts.chord++ })

  function Key($msg, $vk) {
    [Runtime.InteropServices.Marshal]::WriteInt32($kbd, [int][System.Windows.Forms.Keys]$vk)
    $r = $cb.Invoke($hook.PSObject.BaseObject, @(0, [IntPtr]$msg, $kbd))
    [long]$r
  }
  function ResetCounts { $counts.blocked = 0; $counts.unlock = 0; $counts.chord = 0 }

  $hook.UnlockCombo = [System.Windows.Forms.Keys]([System.Windows.Forms.Keys]::G -bor [System.Windows.Forms.Keys]::Alt)
  $hook.Lock()

  # A plain letter is a cat: swallowed AND cued.
  ResetCounts
  $r = Key $WM_KEYDOWN 'A'
  Check 'letter while locked is swallowed' ($r -eq 1)
  Check 'letter while locked cues'         ($counts.blocked -eq 1)

  # THE BUG: Alt is the combo's modifier — the first half of Alt+G.
  ResetCounts
  $r = Key $WM_SYSKEYDOWN 'LMenu'
  Check 'combo modifier is still swallowed' ($r -eq 1)
  Check 'combo modifier does NOT cue'       ($counts.blocked -eq 0) "blocked=$($counts.blocked)"

  # Holding Alt auto-repeats; repeats must stay silent too.
  ResetCounts
  $null = Key $WM_SYSKEYDOWN 'LMenu'
  Check 'combo modifier repeat does not cue' ($counts.blocked -eq 0)

  # ...and half a second later, G completes the combo.
  ResetCounts
  $r = Key $WM_SYSKEYDOWN 'G'
  Check 'G with Alt held unlocks'   ($counts.unlock -eq 1)
  Check 'the unlock itself is silent' ($counts.blocked -eq 0)
  Check 'the unlock key is swallowed' ($r -eq 1)
  $null = Key $WM_KEYUP 'G'
  $null = Key $WM_KEYUP 'LMenu'

  # RAlt is the same modifier; both sides stay silent.
  ResetCounts
  $null = Key $WM_SYSKEYDOWN 'RMenu'
  Check 'right-hand combo modifier does not cue' ($counts.blocked -eq 0)
  $null = Key $WM_KEYUP 'RMenu'

  # A modifier the combo does NOT use is just a blocked key.
  ResetCounts
  $r = Key $WM_KEYDOWN 'LControlKey'
  Check 'non-combo modifier still cues' ($counts.blocked -eq 1)
  $null = Key $WM_KEYUP 'LControlKey'

  # G without Alt is not the combo — a cat on G still cues.
  ResetCounts
  $r = Key $WM_KEYDOWN 'G'
  Check 'combo key without its modifier still cues' ($counts.blocked -eq 1)
  $null = Key $WM_KEYUP 'G'

  # Chord mode: its keys and modifiers are unlock-gesture keys too. Chord
  # Alt+C+F pressed one key at a time must not cue on the way in.
  $hook.ChordModifiers = [System.Windows.Forms.Keys]::Alt
  $hook.SetChordKeys(@([System.Windows.Forms.Keys]::C, [System.Windows.Forms.Keys]::F))

  ResetCounts
  $null = Key $WM_SYSKEYDOWN 'LMenu'
  $r = Key $WM_SYSKEYDOWN 'C'
  Check 'first chord key does not cue' ($counts.blocked -eq 0) "blocked=$($counts.blocked)"
  Check 'first chord key is swallowed' ($r -eq 1)
  $r = Key $WM_SYSKEYDOWN 'F'
  Check 'completing the chord fires ChordPressed' ($counts.chord -eq 1)
  Check 'the chord completion is silent' ($counts.blocked -eq 0)
  $null = Key $WM_KEYUP 'F'
  $null = Key $WM_KEYUP 'C'
  $null = Key $WM_KEYUP 'LMenu'

  # With no chord configured, C is just a letter again.
  $hook.SetChordKeys(@())
  ResetCounts
  $null = Key $WM_KEYDOWN 'C'
  Check 'chord key cues again once chord is off' ($counts.blocked -eq 1)
  $null = Key $WM_KEYUP 'C'

  # Combo disabled entirely: nothing is an unlock gesture, everything cues.
  $hook.UnlockCombo = [System.Windows.Forms.Keys]::None
  ResetCounts
  $null = Key $WM_SYSKEYDOWN 'LMenu'
  Check 'with no combo, Alt cues like any key' ($counts.blocked -eq 1)
  $null = Key $WM_KEYUP 'LMenu'
}
finally {
  [Runtime.InteropServices.Marshal]::FreeHGlobal($kbd)
  $hook.Dispose()
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
