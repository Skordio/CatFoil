# Launches CatFoil with the keyboard-hook diagnostic log enabled.
# This is the only supported way to turn the log on: CATFOIL_HOOK_LOG is set
# for just this process, so every other launch of CatFoil runs with logging off.
#
# NOTE: while enabled, the log records every key event the hook sees —
# including keys typed while unlocked — so use it for short diagnostic
# sessions, not day-to-day running.
#
#   .\run-with-hook-log.ps1

$logPath = Join-Path $env:APPDATA 'CatFoil\hook-diagnostic.log'

# The single-instance mutex means a second launch just signals the running
# instance and exits — logging nothing.
if (Get-Process CatFoil -ErrorAction SilentlyContinue) {
    Write-Warning 'CatFoil is already running — exit it first (tray icon > Exit), then re-run this script.'
    exit 1
}

$env:CATFOIL_HOOK_LOG = '1'
Write-Host "Hook diagnostic log enabled for this run: $logPath"
Write-Host 'Starting CatFoil (exit via the tray menu to end the session)...'

dotnet run

Write-Host "CatFoil exited. Diagnostic log: $logPath"
