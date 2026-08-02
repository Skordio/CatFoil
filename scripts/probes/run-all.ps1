# Runs every probe and reports a one-line result each.
#
#   pwsh -NoProfile -STA -File scripts\probes\run-all.ps1
#
# STA matters: these construct WinForms windows, and Windows PowerShell 5.1
# can't load the .NET 8 assembly at all — it fails by silently handing back a
# null form, which reads as a pass.
param([switch]$Verbose)
$ErrorActionPreference = 'Stop'

$probes = @(
  'probe-overlay-model'   # settings model + every migration
  'probe-render'          # badge output vs the captured baseline
  'probe-3c'              # opacity, shape, colour, blocked/ring opacity
  'probe-3e'              # built-in icon gallery
  'probe-sounds'          # sound settings, migration, MCI player, Sounds page
  'probe-multi-overlay'   # several live badges at once
  'probe-settings'        # settings shell renders every page
  'probe-behavior'        # immediate-apply plumbing
  'probe-scroll'          # pages scroll when squeezed
  'probe-3a'              # overlay list + sub-page navigation
  'probe-3d'              # overlay editor sub-page
  'probe-review-fixes'    # regression cover for the code-review findings
  'probe-topmost-reassert' # badge climbs back above a later-raised topmost window
  'probe-hook-resync'     # stale modifier/chord state cleared by the watchdog
  'probe-stats-page'      # Statistics page in the shell (replaced StatsForm)
  'probe-task-sddl'       # logon task carries a descriptor the user can delete
  'probe-unlock-cue'      # unlock-gesture keys don't fire the blocked-key cue
  'probe-no-chord'        # chord option gone from the UI; engine dormant, data kept
  'probe-restore-ui'      # elevation relaunch re-opens the windows that were up
)

$failed = @()
foreach ($p in $probes) {
    $path = Join-Path $PSScriptRoot "$p.ps1"
    if (-not (Test-Path $path)) { Write-Host "SKIP  $p (missing)" -ForegroundColor Yellow; continue }

    $output = & pwsh -NoProfile -STA -File $path 2>&1
    $bad = @($output | Select-String -Pattern '^FAIL')
    if ($LASTEXITCODE -eq 0 -and $bad.Count -eq 0) {
        Write-Host "OK    $p" -ForegroundColor Green
    }
    else {
        Write-Host "FAIL  $p" -ForegroundColor Red
        $bad | ForEach-Object { Write-Host "        $_" -ForegroundColor Red }
        $failed += $p
    }
    if ($Verbose) { $output | ForEach-Object { Write-Host "      $_" } }
}

Write-Host ''
if ($failed.Count -eq 0) { Write-Host 'ALL PROBES PASS' -ForegroundColor Green }
else { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
