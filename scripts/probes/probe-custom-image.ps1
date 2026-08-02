# Covers the custom-image import path: a file that cannot be drawn must be
# rejected loudly and leave nothing behind, a good one must round-trip through
# OverlayIcon.Load, and a multi-frame .ico must arrive at its largest frame.
#
# Run with STA pwsh 7 (Windows PowerShell 5.1 cannot load the net8.0 assembly):
#   pwsh -sta -File scripts\probes\probe-custom-image.ps1
#
# Touches %APPDATA%\CatFoil\icons only, never settings.json, and removes every
# file it creates there.

$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$dll = Join-Path $repo 'bin\Debug\net8.0-windows\CatFoil.dll'
if (-not (Test-Path $dll)) { throw "Build first: $dll not found" }
$asm = [Reflection.Assembly]::LoadFrom($dll)

$iconStore = $asm.GetType('CatFoil.IconStore', $true)
$overlayIcon = $asm.GetType('CatFoil.OverlayIcon', $true)
$stateType = $asm.GetType('CatFoil.OverlayAppearance', $true)
$sourceEnum = $asm.GetType('CatFoil.OverlayIconSource', $true)
$importMethod = $iconStore.GetMethod('Import', [Reflection.BindingFlags]'Public,Static')
$fullPathMethod = $iconStore.GetMethod('FullPath', [Reflection.BindingFlags]'Public,Static')
$loadMethod = $overlayIcon.GetMethod('Load', [Reflection.BindingFlags]'Public,Static')

$iconsDir = Join-Path $env:APPDATA 'CatFoil\icons'
$before = @{}
if (Test-Path $iconsDir) { Get-ChildItem $iconsDir -File | ForEach-Object { $before[$_.FullName] = $true } }

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("catfoil-probe-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scratch | Out-Null

$pass = 0
$fail = 0
function Check($label, $ok, $detail) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $label" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $label -- $detail" -ForegroundColor Red }
}

# Reflection hands back a PSObject unless every reference-type argument is
# unwrapped, and reflected exceptions arrive wrapped in TargetInvocationException.
function Invoke-Import($path, $id) {
    $argv = [object[]]@($path.PSObject.BaseObject, $id.PSObject.BaseObject)
    try { return @{ Ok = $true; Value = $importMethod.Invoke($null, $argv) } }
    catch { return @{ Ok = $false; Error = $_.Exception.InnerException } }
}

function Stored-Files { if (Test-Path $iconsDir) { @(Get-ChildItem $iconsDir -File | Where-Object { -not $before.ContainsKey($_.FullName) }) } else { @() } }

try {
    Write-Host "`nA chosen file that cannot be drawn is rejected" -ForegroundColor Cyan

    # Exactly Steven's case: a OneDrive placeholder whose contents were never
    # downloaded copies successfully as zero bytes.
    $empty = Join-Path $scratch 'empty.png'
    New-Item -ItemType File -Path $empty | Out-Null
    $r = Invoke-Import $empty 'probeid'
    Check 'an empty (0-byte) source throws' (-not $r.Ok) 'Import returned normally'
    if (-not $r.Ok) {
        Check 'it throws UnusableImageException' ($r.Error.GetType().Name -eq 'UnusableImageException') $r.Error.GetType().Name
        Check 'the message names the size and OneDrive' ($r.Error.Message -match '0 bytes' -and $r.Error.Message -match 'OneDrive') $r.Error.Message
    }
    Check 'no file is left in icons\' ((Stored-Files).Count -eq 0) "left $((Stored-Files) -join ', ')"

    # A .webp or .svg picked through the dialog's "All files" filter: the
    # extension is coerced to .png, so only the content can reveal it.
    $bogus = Join-Path $scratch 'notreally.png'
    [IO.File]::WriteAllText($bogus, 'this is not a picture')
    $r = Invoke-Import $bogus 'probeid'
    Check 'an undecodable source throws' (-not $r.Ok) 'Import returned normally'
    if (-not $r.Ok) {
        Check 'it throws UnusableImageException' ($r.Error.GetType().Name -eq 'UnusableImageException') $r.Error.GetType().Name
        Check 'the message says which formats work' ($r.Error.Message -match 'PNG') $r.Error.Message
    }
    Check 'no file is left in icons\ (undecodable)' ((Stored-Files).Count -eq 0) "left $((Stored-Files) -join ', ')"

    Write-Host "`nA real picture still imports and renders" -ForegroundColor Cyan

    # 8x8 opaque red PNG, so the probe never depends on GDI+ to *create* a file.
    $png = Join-Path $scratch 'good.png'
    [IO.File]::WriteAllBytes($png, [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAFklEQVR4nGP8z4AATAxIYFThIFEIAEbGAROv52ldAAAAAElFTkSuQmCC'))
    $r = Invoke-Import $png 'probeid'
    Check 'a valid PNG imports' $r.Ok $(if (-not $r.Ok) { $r.Error.Message })

    $fallback = New-Object System.Drawing.Bitmap 4, 4
    if ($r.Ok) {
        $relative = $r.Value
        $full = $fullPathMethod.Invoke($null, [object[]]@($relative.PSObject.BaseObject))
        Check 'the stored file has content' ((Get-Item $full).Length -gt 0) 'stored file is empty'

        $state = [Activator]::CreateInstance($stateType)
        $stateType.GetProperty('IconSource').SetValue($state, [Enum]::Parse($sourceEnum, 'Custom'))
        $stateType.GetProperty('CustomIconFile').SetValue($state, $relative)
        $loaded = $loadMethod.Invoke($null, [object[]]@($state.PSObject.BaseObject, $fallback.PSObject.BaseObject))
        Check 'OverlayIcon.Load does not fall back' (-not [object]::ReferenceEquals($loaded, $fallback)) 'returned the fallback icon'
        Check 'the loaded bitmap is the source image' ($loaded.Width -eq 8 -and $loaded.Height -eq 8) "$($loaded.Width)x$($loaded.Height)"
        $loaded.Dispose()
    }

    Write-Host "`nA multi-frame .ico arrives at its largest frame" -ForegroundColor Cyan

    $ico = Join-Path $repo 'assets\cat.ico'
    $r = Invoke-Import $ico 'probeid'
    Check 'the .ico imports' $r.Ok $(if (-not $r.Ok) { $r.Error.Message })
    if ($r.Ok) {
        $state = [Activator]::CreateInstance($stateType)
        $stateType.GetProperty('IconSource').SetValue($state, [Enum]::Parse($sourceEnum, 'Custom'))
        $stateType.GetProperty('CustomIconFile').SetValue($state, $r.Value)
        $loaded = $loadMethod.Invoke($null, [object[]]@($state.PSObject.BaseObject, $fallback.PSObject.BaseObject))

        # Measured against BOTH decoders rather than against the one the code
        # happens to use, which would assert only that the code equals itself.
        # For assets\cat.ico these are 256 and 48: Icon() cannot read the
        # PNG-compressed 256 px frame, so picking it blind would be a downgrade.
        $b = [System.Drawing.Bitmap]::new($ico); $viaGdi = $b.Width; $b.Dispose()
        $i = [System.Drawing.Icon]::new($ico, 256, 256); $viaIcon = $i.Width; $i.Dispose()
        $best = [Math]::Max($viaGdi, $viaIcon)
        Check 'the .ico decodes to its largest frame' ($loaded.Width -eq $best) `
            "got $($loaded.Width); Bitmap() gives $viaGdi, Icon() gives $viaIcon"
        $loaded.Dispose()
    }
    $fallback.Dispose()
}
finally {
    foreach ($f in Stored-Files) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n$pass passed, $fail failed`n" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
exit $(if ($fail) { 1 } else { 0 })
