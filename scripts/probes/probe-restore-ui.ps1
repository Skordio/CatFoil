# "Run as administrator" relaunches CatFoil elevated — and the new instance
# used to start with whatever StartMinimized said, so a start-closed preference
# made the app vanish mid-settings-visit (Steven, 2026-08-02). The fix under
# test: the relaunch command line carries the UI state (RestoreUi.Encode), and
# the elevated instance re-opens exactly the windows that were up — the main
# window if it was visible, the settings window on the page the user was on —
# overriding StartMinimized for that one launch.
#
# TryRelaunchElevated is never INVOKED here (it would pop a real UAC prompt);
# only its signature is checked. SettingsShell construction keeps the standard
# settings.json guard.
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
# 1. RestoreUi: encode for the command line, parse what Windows delivers
# ---------------------------------------------------------------
$rT = $asm.GetType('CatFoil.RestoreUi')
Check 'RestoreUi exists' ($null -ne $rT)

if ($rT) {
  function Enc($main, $page) {
    [string]$rT.GetMethod('Encode').Invoke($null, @($main, $page))
  }
  function ParseMain([string[]]$a)      { [bool]$rT.GetMethod('MainWindow').Invoke($null, @(,$a)) }
  function ParsePage([string[]]$a)      { $rT.GetMethod('SettingsPage').Invoke($null, @(,$a)) }
  function ParseRequested([string[]]$a) { [bool]$rT.GetMethod('Requested').Invoke($null, @(,$a)) }

  $both = Enc $true 'Advanced'
  Write-Host "      encoded: '$both'"
  Check 'encodes the main flag'       ($both -match '--restore-main')
  Check 'encodes the settings page'   ($both -match '--restore-settings "Advanced"')
  Check 'nothing open encodes empty'  ((Enc $false $null) -eq '')
  Check 'settings-only omits main'    ((Enc $false 'Advanced') -notmatch 'restore-main')

  # What the relaunched process actually sees (quotes already stripped).
  $argv = @('CatFoil.exe', '--await-exit', '1234', '--restore-main', '--restore-settings', 'Advanced')
  Check 'parses main'            (ParseMain $argv)
  Check 'parses the page'        ((ParsePage $argv) -eq 'Advanced')
  Check 'parses requested'       (ParseRequested $argv)

  $plain = @('CatFoil.exe')
  Check 'plain launch: not requested' (-not (ParseRequested $plain))
  Check 'plain launch: no main'       (-not (ParseMain $plain))
  Check 'plain launch: no page'       ($null -eq (ParsePage $plain))

  # A trailing flag with no value must not read past the end.
  $cut = @('CatFoil.exe', '--restore-settings')
  Check 'truncated page flag parses as none' ($null -eq (ParsePage $cut))
  Check 'truncated page flag still counts as requested' (ParseRequested $cut)
}

# ---------------------------------------------------------------
# 2. Elevation.TryRelaunchElevated can carry the extra arguments
# ---------------------------------------------------------------
$eT = $asm.GetType('CatFoil.Elevation')
$m  = $eT.GetMethod('TryRelaunchElevated')
$ps = $m.GetParameters()
Check 'TryRelaunchElevated takes the restore args' `
      ($ps.Length -eq 1 -and $ps[0].ParameterType -eq [string] -and $ps[0].IsOptional) `
      "params=$($ps.Length)"

# ---------------------------------------------------------------
# 3. SettingsShell: the page the user is on, by name
# ---------------------------------------------------------------
$sT   = $asm.GetType('CatFoil.Settings')
$settings = [Text.Json.JsonSerializer]::Deserialize('{}', $sT)
$fT   = $asm.GetType('CatFoil.SettingsShell')
Check 'SettingsShell exists' ($null -ne $fT)
$form = [Activator]::CreateInstance($fT, @($settings.PSObject.BaseObject))
try {
  # Both members are internal, so NonPublic binding is needed to see them.
  $inst = [Reflection.BindingFlags]'Instance,NonPublic,Public'
  $title = $fT.GetProperty('CurrentPageTitle', $inst)
  $byName = $fT.GetMethod('SelectPage', $inst, $null, [Type[]]@([string]), $null)
  Check 'CurrentPageTitle exists'    ($null -ne $title)
  Check 'SelectPage(string) exists'  ($null -ne $byName)
  if ($title -and $byName) {
    Check 'starts on General' (($title.GetValue($form)) -eq 'General') "got $($title.GetValue($form))"
    $byName.Invoke($form.PSObject.BaseObject, @('Advanced'.PSObject.BaseObject)) | Out-Null
    Check 'SelectPage lands on Advanced' (($title.GetValue($form)) -eq 'Advanced')
    $byName.Invoke($form.PSObject.BaseObject, @('No Such Page'.PSObject.BaseObject)) | Out-Null
    Check 'an unknown page changes nothing' (($title.GetValue($form)) -eq 'Advanced')
  }
}
finally {
  $form.Dispose()   # dispose, never EndVisit — the visit-end hook flushes settings
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
