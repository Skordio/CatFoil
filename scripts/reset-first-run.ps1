# Resets CatFoil's first-run state so the welcome screen shows again on next launch.
#   .\scripts\reset-first-run.ps1         # clear just the WelcomeShown flag
#   .\scripts\reset-first-run.ps1 -All    # delete settings.json entirely (true first run:
#                                         # also wipes hotkey, overlay position, license, etc.)
param(
    [switch]$All
)

$settingsPath = Join-Path $env:APPDATA 'CatFoil\settings.json'

# A running CatFoil holds settings in memory and would write the old values
# right back over ours the next time it saves.
if (Get-Process CatFoil -ErrorAction SilentlyContinue) {
    Write-Warning 'CatFoil is running — exit it first (tray icon > Exit), then re-run this script.'
    exit 1
}

if (-not (Test-Path $settingsPath)) {
    Write-Host "No settings file at $settingsPath — the app is already in first-run state."
    exit 0
}

if ($All) {
    Remove-Item $settingsPath -Force
    Write-Host 'Deleted settings.json — next launch is a completely fresh first run.'
}
else {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    # -Force also covers settings files saved before the flag existed.
    $json | Add-Member -NotePropertyName WelcomeShown -NotePropertyValue $false -Force
    $json | ConvertTo-Json -Depth 5 | Set-Content $settingsPath
    Write-Host 'WelcomeShown reset — the welcome screen will show on next launch (other settings kept).'
}
