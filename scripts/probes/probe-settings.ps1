# Renders the new SettingsForm and screenshots every page.
# Constructs the form only — no keyboard hook, no tray, no lock. Uses a fresh
# in-memory Settings so nothing touches %APPDATA%\CatFoil.
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
# elsewhere. A probe that edits a setting and then pumps for longer than the
# 500 ms debounce is enough to trigger it, which is easy to do by accident and
# silently destroys live configuration. Snapshot the file and put it back
# however this script exits, so the rule is enforced rather than remembered.
$LiveSettings   = Join-Path (Join-Path $env:APPDATA 'CatFoil') 'settings.json'
$SettingsBackup = if (Test-Path $LiveSettings) { [IO.File]::ReadAllBytes($LiveSettings) } else { $null }
try {


Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# PrintWindow captures the window's own pixels, so an occluding window (or the
# probe never getting foreground) can't contaminate the shot.
Add-Type -Name 'U32' -Namespace 'Probe' -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
'@

function Capture-Window {
    param($handle, $w, $h)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try { [Probe.U32]::PrintWindow($handle, $hdc, 2) | Out-Null }   # 2 = PW_RENDERFULLCONTENT
    finally { $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}

$outDir = $ProbeShots
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$settingsType = $asm.GetType('CatFoil.Settings')
$formType     = $asm.GetType('CatFoil.SettingsForm')
if (-not $formType) { throw 'SettingsForm type not found — probe would false-pass.' }

$settings = [Activator]::CreateInstance($settingsType)
$form     = [Activator]::CreateInstance($formType, @($settings))
if (-not $form) { throw 'SettingsForm instance was null.' }

$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(80, 60)
$form.Show()
$form.Activate()

$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance
$nav = $formType.GetField('_nav', $flags).GetValue($form)
Write-Host "Nav items: $($nav.Items.Count) -> $($nav.Items -join ', ')"

function Pump { param($ms = 400)
  $end = (Get-Date).AddMilliseconds($ms)
  while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
}

Pump 800

for ($i = 0; $i -lt $nav.Items.Count; $i++) {
    $nav.SelectedIndex = $i
    Pump 600
    $form.Refresh()
    Pump 200
    $name = ($nav.Items[$i] -replace '[^A-Za-z0-9]', '')
    $bmp  = Capture-Window $form.Handle $form.Width $form.Height
    $path = Join-Path $outDir ("{0}-{1}.png" -f $i, $name)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "captured $path"
}

$form.Close()
$form.Dispose()
Write-Host 'OK'

}
finally {
    # Restore byte-for-byte, including the case where there was no file at all.
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
