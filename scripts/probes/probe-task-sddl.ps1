# The elevated startup task survives uninstall (found by the 2026-07-27 live E2E,
# explained at the bit level 2026-08-01 by reading the real task's descriptor):
#
#   O:BA G:<user> D:(A;ID;0x1f019f;;;BA)(A;ID;0x1f019f;;;SY)
#                   (A;ID;FA;;;BA)(A;;FR;;;<user sid>)
#
# The user's one explicit ACE is FR — read, no DELETE. The task is created by an
# elevated process, so the inherited CREATOR OWNER ACE resolves to Administrators
# rather than to the user (on an unelevated task the same ACE reads
# "(A;ID;FA;;;<user sid>)", which is why ordinary tasks delete fine). An admin's
# everyday token carries Administrators as DENY-ONLY, so the per-user uninstaller
# is denied, the cleanup swallows it by design, and an orphan task is left
# pointing at a deleted exe.
#
# The fix under test: after creating the task, stamp a DACL that also grants this
# user's SID read + DELETE — and nothing writable. DisableTask/UninstallCleanup
# are unchanged; they start working because the object now permits it.
#
# What this canNOT do, and why it doesn't try: reproduce the symptom. The
# denial needs a task created by an elevated process, and an unelevated probe
# cannot make one — nor should it, since a task it could not delete afterwards
# IS the orphan this fix exists to prevent. Both routes to forcing the shape are
# booby-trapped, and both were found by springing them (2026-08-01):
#   - Passing an SDDL to RegisterTask: it checks the caller keeps write access
#     and refuses otherwise — AFTER creating the task. Result: a task nobody
#     unelevated can update or delete.
#   - Stamping a protected DACL ("D:P", inheritance blocked): refused the same
#     way, AFTER the inherited ACEs are gone. Same result. TrySetTaskSddl now
#     rejects those outright, and the check below holds it to that.
# So the fixture here keeps a plain, inheriting DACL throughout, and proving the
# fix on the real shape belongs to the live E2E.
#
# The real task ("CatFoil Startup (elevated)") is only ever READ here.
$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatFoilDll = Join-Path $RepoRoot 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $CatFoilDll)) { throw "Build first - not found: $CatFoilDll" }
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {

Add-Type -AssemblyName System.Windows.Forms

$asm   = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$sT    = $asm.GetType('CatFoil.Startup')
$flags = [Reflection.BindingFlags]'Static,NonPublic,Public'

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function M($name) { $sT.GetMethod($name, $flags) }

$sid       = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
# Unique per run: if a run ever does strand its fixture, the next run must not
# inherit a name it can neither stamp nor delete.
$ProbeTask = "CatFoil probe $PID (safe to delete)"

# ---------------------------------------------------------------
# 1. The DACL builder
# ---------------------------------------------------------------
$build = M 'BuildTaskSddl'
Check 'BuildTaskSddl() exists' ($null -ne $build)

$sddl = if ($build) { [string]$build.Invoke($null, @()) } else { '' }
Write-Host "      SDDL: $sddl"

Check 'grants SYSTEM full control'         ($sddl -match '\(A;;FA;;;SY\)')
Check 'grants Administrators full control' ($sddl -match '\(A;;FA;;;BA\)')
Check 'contains an ACE for this user'      ($sddl -match [regex]::Escape(";;;$sid)"))
# The owner clause was dropped on purpose: the task is created elevated so its
# owner is already BA, and setting it is the one part the service rejects
# (ERROR_INVALID_OWNER) when the caller isn't elevated.
Check 'sets no owner or group'             ($sddl.StartsWith('D:'))

$userAce = ''
foreach ($ace in [regex]::Matches($sddl, '\(([^)]*)\)')) {
  if ($ace.Groups[1].Value.EndsWith(";;;$sid")) { $userAce = $ace.Groups[1].Value }
}
Check 'user ACE found' ($userAce -ne '') $sddl
if ($userAce) {
  $rights = ($userAce -split ';')[2]
  Write-Host "      user rights: '$rights'"
  Check 'user ACE is an allow ACE' ($userAce.StartsWith('A;'))
  Check 'user is granted delete (SD)' ($rights -match 'SD')
  Check 'user is granted read (GR)'   ($rights -match 'GR')
  # THE security assertion: write on a HighestAvailable task is an elevation
  # primitive — rewrite the action, get arbitrary code run elevated at logon.
  foreach ($w in 'GA', 'GW', 'FA', 'FW', 'KA', 'WD', 'WO', 'CC', 'DC', 'WP') {
    Check "user is NOT granted $w" ($rights -notmatch $w) "rights='$rights'"
  }
}

# ---------------------------------------------------------------
# 2. The delete-grant test that drives idempotent self-repair
# ---------------------------------------------------------------
$grants = M 'SddlGrantsDeleteTo'
Check 'SddlGrantsDeleteTo() exists' ($null -ne $grants)
if ($grants) {
  function GrantsDelete($s, $forSid) {
    [bool]$grants.Invoke($null, @($s.PSObject.BaseObject, $forSid.PSObject.BaseObject))
  }
  Check 'true for the built DACL'      (GrantsDelete $sddl $sid)
  Check 'false when read-only'         (-not (GrantsDelete "D:(A;;GR;;;$sid)" $sid))
  Check 'true for full control'        (GrantsDelete "D:(A;;GA;;;$sid)" $sid)
  Check 'false for a different SID'    (-not (GrantsDelete 'D:(A;;GRSD;;;S-1-5-21-1-2-3-9999)' $sid))
  Check 'false for a deny ACE'         (-not (GrantsDelete "D:(D;;GRSD;;;$sid)" $sid))
  Check 'false on an empty descriptor' (-not (GrantsDelete '' $sid))
  # Windows re-renders a descriptor on read-back and a mask it has no token for
  # comes back as hex: GR+SD stored on a task reads as 0x130089
  # (FILE_GENERIC_READ 0x120089 | DELETE 0x10000). Missing this branch would make
  # self-repair rewrite the descriptor on every single elevated launch.
  Check 'true for the hex form it reads back as' (GrantsDelete "D:(A;;0x130089;;;$sid)" $sid)
  Check 'false for hex read access alone'        (-not (GrantsDelete "D:(A;;0x120089;;;$sid)" $sid))
  # The shape the real task has today, which is exactly what must NOT count.
  Check 'false for the unrepaired real-task shape' `
        (-not (GrantsDelete "O:BAG:$sid`D:(A;ID;0x1f019f;;;BA)(A;ID;FA;;;BA)(A;;FR;;;$sid)" $sid))
}

# ---------------------------------------------------------------
# 3. Stamping a real task, on a throwaway
# ---------------------------------------------------------------
$getSddl = M 'TryGetTaskSddl'
$setSddl = M 'TrySetTaskSddl'
Check 'TryGetTaskSddl() exists' ($null -ne $getSddl)
Check 'TrySetTaskSddl() exists' ($null -ne $setSddl)

function SetDacl($dacl) {
  [bool]$setSddl.Invoke($null, @($ProbeTask.PSObject.BaseObject, $dacl.PSObject.BaseObject))
}
function TaskGone {
  & schtasks.exe /Query /TN $ProbeTask *> $null
  $LASTEXITCODE -ne 0
}

# The refusal that keeps a caller from bricking a task. Pure string check, so it
# runs whether or not the fixture below can be created.
if ($setSddl) {
  Check 'refuses a protected DACL (D:P)'  (-not (SetDacl "D:P(A;;FA;;;SY)(A;;GRSD;;;$sid)"))
  Check 'refuses a protected+auto DACL'   (-not (SetDacl "D:PAI(A;;FA;;;SY)(A;;GRSD;;;$sid)"))
}

# Trigger-less and disabled, so it can never fire.
$probeXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>CatFoil probe task - safe to delete</Description></RegistrationInfo>
  <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><Enabled>false</Enabled><AllowStartOnDemand>false</AllowStartOnDemand><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
  <Actions Context="Author"><Exec><Command>C:\Windows\System32\cmd.exe</Command><Arguments>/c exit</Arguments></Exec></Actions>
</Task>
"@

$service = $null; $folder = $null; $created = $false
try {
  if ($getSddl -and $setSddl) {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $folder = $service.GetFolder('\')
    # No sddl argument here — see the header.
    try {
      $null = $folder.RegisterTask($ProbeTask, $probeXml, 6, $null, $null, 3, $null)
      $created = $true
    }
    catch {
      # Denied here means a task of this name already exists and isn't writable
      # by us — rename the fixture rather than trying to force it.
      Check 'fixture task registers' $false $_.Exception.Message
    }
  }
  if ($created) {
    # The production DACL, on a real task, through the real product code.
    Check 'TrySetTaskSddl stamps the production DACL' (SetDacl $sddl)
    $back = [string]$getSddl.Invoke($null, @($ProbeTask.PSObject.BaseObject))
    Write-Host "      reads back as: $back"
    # Assert on EXPLICIT ACEs only — the ones without the "ID" (inherited) flag.
    # A default descriptor already carries inherited full control for the
    # creating user, so anything looser than this passes without a stamp.
    Check 'stamp landed: explicit SYSTEM ACE'         ($back -match '\(A;;FA;;;SY\)')
    Check 'stamp landed: explicit Administrators ACE' ($back -match '\(A;;FA;;;BA\)')
    # Windows re-renders GR+SD as the hex mask; if that ever stops being true the
    # hex branch above is dead code and self-repair rewrites on every launch.
    Check 'stamp landed: explicit user read+delete ACE' `
          (($back -match [regex]::Escape("(A;;0x130089;;;$sid)")) -or
           ($back -match [regex]::Escape("(A;;GRSD;;;$sid)")))
    if ($grants) { Check 'the read-back counts as a delete grant' (GrantsDelete $back $sid) }

    # Not a reproduction of the bug (an unelevated task deletes fine by default)
    # — it checks the stamp doesn't COST deletability, which would turn every
    # future uninstall into the orphan case.
    & schtasks.exe /Delete /TN $ProbeTask /F *> $null
    $deleted = TaskGone
    Check 'the stamped task is still deletable unelevated' $deleted
    if ($deleted) { $created = $false }
  }
}
finally {
  if ($created) {
    & schtasks.exe /Delete /TN $ProbeTask /F *> $null
    if (TaskGone) { Write-Host 'NOTE: probe task cleaned up in finally.' -ForegroundColor Yellow }
    else {
      Write-Host "WARN: could not remove '$ProbeTask'. From an ELEVATED shell:" -ForegroundColor Red
      Write-Host "        schtasks /Delete /TN `"$ProbeTask`" /F" -ForegroundColor Red
    }
  }
  if ($folder)  { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($folder) }
  if ($service) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($service) }
}

# ---------------------------------------------------------------
# 4. Self-repair for tasks that already exist
# ---------------------------------------------------------------
# Every 0.4.1 install already has a task with the old descriptor.
# RepairTaskSecurity() re-stamps it, but only from an elevated process and only
# when the task points at THIS exe — the same path-guard as UninstallCleanup.
# This probe is unelevated, so what gets exercised is the elevation guard: it
# must return quietly and leave the real task exactly as it found it.
$repair = M 'RepairTaskSecurity'
Check 'RepairTaskSecurity() exists' ($null -ne $repair)

$realTask = 'CatFoil Startup (elevated)'
$before = if ($getSddl) { [string]$getSddl.Invoke($null, @($realTask.PSObject.BaseObject)) } else { '' }
if ($repair) {
  $threw = $false
  try { $repair.Invoke($null, @()) | Out-Null } catch { $threw = $true }
  Check 'RepairTaskSecurity() is quiet when unelevated' (-not $threw)
}
if ($getSddl -and $before) {
  $after = [string]$getSddl.Invoke($null, @($realTask.PSObject.BaseObject))
  Check "the real task's descriptor is untouched" ($after -eq $before)
  # Informational, never an assertion: this flips once Steven's machine has run
  # a repaired elevated build, and the probe must pass either way.
  if ($grants) {
    $state = if (GrantsDelete $before $sid) { 'REPAIRED (user can delete it)' } else { 'not yet repaired' }
    Write-Host "      live task on this machine: $state"
  }
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
