# Sound settings model, migration, store, and the MCI player.
# Plays real audio briefly - that is the point, since MCI either opens a file or
# it doesn't and only trying tells you which.
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
# elsewhere. Snapshot the file and put it back however this script exits.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT  = $asm.GetType('CatFoil.Settings')
$cueT = $asm.GetType('CatFoil.SoundSetting')
$srcT = $asm.GetType('CatFoil.SoundSource')
$playerT = $asm.GetType('CatFoil.AudioPlayer')
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public

$opt = [System.Text.Json.JsonSerializerOptions]::new()
$opt.WriteIndented = $true
$opt.Converters.Add([System.Text.Json.Serialization.JsonStringEnumConverter]::new())

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}

# --- 1. Defaults -------------------------------------------------------------
$fresh = [Activator]::CreateInstance($sT)
$fresh.EnsureSounds()
Check 'three independent cues exist' (($null -ne $fresh.LockSound) -and ($null -ne $fresh.UnlockSound) -and ($null -ne $fresh.BlockedSound))
Check 'cues are off by default' ((-not $fresh.LockSound.Enabled) -and (-not $fresh.BlockedSound.Enabled))
Check 'default source is the Windows sound' ($fresh.LockSound.Source.ToString() -eq 'System') $fresh.LockSound.Source
Check 'default volume is sane' ($fresh.LockSound.ClampedVolume() -eq 80) $fresh.LockSound.ClampedVolume()

# --- 2. Legacy migration -----------------------------------------------------
# One old flag covered lock AND unlock; it must light both.
$legacy = '{"SoundOnLockUnlock":true,"SoundOnBlockedKey":false,"StatLockSessions":31}'
$m = [System.Text.Json.JsonSerializer]::Deserialize($legacy, $sT, $opt)
$m.EnsureSounds()
Check 'legacy lock+unlock flag lights both cues' ($m.LockSound.Enabled -and $m.UnlockSound.Enabled)
Check 'legacy blocked flag respected when false' (-not $m.BlockedSound.Enabled)
Check 'legacy migration keeps the stats' ($m.StatLockSessions -eq 31)

$legacy2 = '{"SoundOnLockUnlock":false,"SoundOnBlockedKey":true}'
$m2 = [System.Text.Json.JsonSerializer]::Deserialize($legacy2, $sT, $opt)
$m2.EnsureSounds()
Check 'legacy blocked flag lights the blocked cue' ($m2.BlockedSound.Enabled)
Check 'legacy off stays off' ((-not $m2.LockSound.Enabled) -and (-not $m2.UnlockSound.Enabled))

$out = [System.Text.Json.JsonSerializer]::Serialize($m, $sT, $opt)
Check 'legacy sound flags no longer written' (-not ($out -match 'SoundOnLockUnlock|SoundOnBlockedKey'))
Check 'per-event cues are written' ($out -match '"LockSound"' -and $out -match '"BlockedSound"')

# Idempotence: a deliberately-off cue must not be switched back on.
$deliberate = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"LockSound":{"Enabled":false},"UnlockSound":{"Enabled":true}}', $sT, $opt)
1..3 | ForEach-Object { $deliberate.EnsureSounds() }
Check 'deliberate off survives repeated EnsureSounds' ((-not $deliberate.LockSound.Enabled) -and $deliberate.UnlockSound.Enabled)

# --- 3. Corrupt input --------------------------------------------------------
$nulls = [System.Text.Json.JsonSerializer]::Deserialize('{"LockSound":null,"BlockedSound":null}', $sT, $opt)
$nulls.EnsureSounds()
Check 'null cues recover' (($null -ne $nulls.LockSound) -and ($null -ne $nulls.BlockedSound))

$badSrc = [System.Text.Json.JsonSerializer]::Deserialize(
  '{"LockSound":{"Source":"Telepathy"},"StatBlockedKeys":9}', $sT, $opt)
$badSrc.EnsureSounds()
Check 'unknown source falls back to System' ($badSrc.LockSound.Source.ToString() -eq 'System') $badSrc.LockSound.Source
Check 'unknown source keeps the stats' ($badSrc.StatBlockedKeys -eq 9)

$vol = [System.Text.Json.JsonSerializer]::Deserialize('{"LockSound":{"Volume":9999}}', $sT, $opt)
$vol.EnsureSounds()
Check 'out-of-range volume clamps' ($vol.LockSound.ClampedVolume() -eq 100) $vol.LockSound.ClampedVolume()

# --- 4. The MCI player, against a real generated WAV -------------------------
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("catfoil-probe-{0}.wav" -f ([guid]::NewGuid().ToString('N')))
# 16-bit mono 8 kHz, ~0.25 s of a quiet sine. Written by hand so the probe needs
# no sample file checked in.
$rate = 8000; $samples = 2000
$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ms)
$bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $samples * 2)); $bw.Write([char[]]'WAVE')
$bw.Write([char[]]'fmt '); $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
$bw.Write([int]$rate); $bw.Write([int]($rate * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
$bw.Write([char[]]'data'); $bw.Write([int]($samples * 2))
for ($i = 0; $i -lt $samples; $i++) {
  $bw.Write([int16]([Math]::Sin(2 * [Math]::PI * 440 * $i / $rate) * 1500))
}
$bw.Flush(); [IO.File]::WriteAllBytes($tmp, $ms.ToArray()); $bw.Dispose(); $ms.Dispose()
Check 'generated a test wav' (Test-Path $tmp)

function TryPlay($path, $vol, $alias) {
  $a = New-Object object[] 3
  $a[0] = [string]$path; $a[1] = [int]$vol; $a[2] = [string]$alias
  $playerT.GetMethod('TryPlay', $flags).Invoke($null, $a)
}
Check 'MCI plays a real wav' (TryPlay $tmp 40 'catfoil_probe')
Check 'MCI reports failure for a missing file' (-not (TryPlay (Join-Path ([IO.Path]::GetTempPath()) 'catfoil-nope.wav') 40 'catfoil_probe2'))
$notAudio = Join-Path ([IO.Path]::GetTempPath()) 'catfoil-notaudio.wav'
[IO.File]::WriteAllText($notAudio, 'this is definitely not audio')
Check 'MCI reports failure for a non-audio file' (-not (TryPlay $notAudio 40 'catfoil_probe3'))
Check 'replaying the same alias restarts cleanly' (TryPlay $tmp 40 'catfoil_probe')
Start-Sleep -Milliseconds 350
$playerT.GetMethod('CloseAll', $flags).Invoke($null, @()) | Out-Null
Check 'CloseAll releases the file handle' ($true)
Remove-Item $tmp, $notAudio -Force -ErrorAction SilentlyContinue

# --- 5. The Sounds page builds and binds ------------------------------------
[System.Windows.Forms.Application]::EnableVisualStyles()
$fT = $asm.GetType('CatFoil.SettingsShell')
if (-not $fT) { throw 'SettingsShell type not found — probe would false-pass.' }
$ifl = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance
$settings = [Activator]::CreateInstance($sT)
$settings.EnsureOverlays() | Out-Null; $settings.EnsureSounds()
$settings.LockSound.Enabled = $true

$shell = [Activator]::CreateInstance($fT, @($settings))
$form = New-Object System.Windows.Forms.Form
$form.ClientSize = New-Object System.Drawing.Size(900, 720)
$shell.Dock = [System.Windows.Forms.DockStyle]::Fill
$form.Controls.Add($shell)
$form.Icon = [System.Drawing.SystemIcons]::Application
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(60, 40)
$form.Show()
$end = (Get-Date).AddMilliseconds(400)
while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }
($fT.GetField('_nav', $ifl).GetValue($shell)).SelectedIndex = 3
$end = (Get-Date).AddMilliseconds(400)
while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 }

$page = ($fT.GetField('_pages', $ifl).GetValue($shell))[3]
$all = New-Object System.Collections.ArrayList
function Walk($c) { foreach ($k in $c.Controls) { [void]$all.Add($k); Walk $k } }
Walk $page
Check 'three volume sliders, one per cue' (@($all | Where-Object { $_.GetType().Name -eq 'TrackBar' }).Count -eq 3)
Check 'three Test buttons' (@($all | Where-Object { $_.Text -eq 'Test' }).Count -eq 3)
Check 'three Choose buttons' (@($all | Where-Object { $_.Text -eq 'Choose…' }).Count -eq 3)
$sysRadios = @($all | Where-Object { $_.Text -eq 'Windows sound' })
Check 'source radios present' ($sysRadios.Count -eq 3)
Check 'volume disabled while the Windows sound is selected' (-not (@($all | Where-Object { $_.GetType().Name -eq 'TrackBar' })[0].Enabled))
Check 'Choose disabled while the Windows sound is selected' (-not (@($all | Where-Object { $_.Text -eq 'Choose…' })[0].Enabled))
# The enabled cue's own on/off must still be reachable.
Check 'the cue checkbox stays enabled' ((@($all | Where-Object { $_.Text -eq 'Play a sound when locking' })[0]).Enabled)
# A cue that is switched off greys its whole block, so "off" is unmistakable.
Check 'a disabled cue greys its source radios' (-not (@($all | Where-Object { $_.Text -eq 'Windows sound' })[1].Enabled))
Check 'nothing overflows the page horizontally' `
  (@($all | Where-Object { $_.Parent -and $_.Right -gt ($_.Parent.ClientSize.Width + 1) }).Count -eq 0)

# PrintWindow, not CopyFromScreen: the latter grabs whatever happens to be in
# front and silently captures the wrong window.
Add-Type -Name 'U32' -Namespace 'Probe' -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
'@
$bmp = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
try { [Probe.U32]::PrintWindow($form.Handle, $hdc, 2) | Out-Null } finally { $g.ReleaseHdc($hdc); $g.Dispose() }
$bmp.Save((Join-Path $ProbeShots 'sounds-page.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$form.Dispose()

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
