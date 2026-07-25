# Step 3e: the built-in icon gallery.
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
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$gT  = $asm.GetType('CatFoil.IconGallery')
$stT = $asm.GetType('CatFoil.OverlayStateSettings')
$oiT = $asm.GetType('CatFoil.OverlayIcon')
$srcT = $asm.GetType('CatFoil.OverlayIconSource')
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Render($id, $color, $size) {
  $a = New-Object object[] 3
  $a[0] = $id; $a[1] = $color.PSObject.BaseObject; $a[2] = [int]$size
  $gT.GetMethod('Render').Invoke($null, $a)
}
# Fraction of non-transparent pixels, and the ink bounding box.
function InkStats($bmp) {
  $r = New-Object System.Drawing.Rectangle(0,0,$bmp.Width,$bmp.Height)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $buf = New-Object byte[] ($d.Stride * $bmp.Height)
  [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($d)
  $n = 0; $minX = $bmp.Width; $maxX = -1; $minY = $bmp.Height; $maxY = -1
  for ($y = 0; $y -lt $bmp.Height; $y++) {
    for ($x = 0; $x -lt $bmp.Width; $x++) {
      $i = $y * $d.Stride + $x * 4
      if ($buf[$i+3] -gt 32) {
        $n++
        if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
        if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
      }
    }
  }
  [pscustomobject]@{ Count=$n; MinX=$minX; MaxX=$maxX; MinY=$minY; MaxY=$maxY
                     W=($maxX-$minX+1); H=($maxY-$minY+1) }
}

$available = $gT.GetProperty('IsAvailable').GetValue($null)
Check 'a symbol font is available on this machine' $available
$icons = $gT.GetField('Icons', $flags).GetValue($null)
Check 'gallery has a curated set' ($icons.Count -ge 8) "count=$($icons.Count)"

# --- every curated glyph renders real, well-centred ink ----------------------
$white = [System.Drawing.Color]::White
$ids = @()
foreach ($e in $icons) {
  $ids += $e.Id
  $bmp = Render $e.Id $white 256
  if ($null -eq $bmp) { Check "render $($e.Id)" $false 'returned null'; continue }
  $s = InkStats $bmp
  $frac = $s.Count / (256.0 * 256.0)
  # Real glyph, not an empty box or a .notdef rectangle.
  $ok = ($frac -gt 0.02) -and ($frac -lt 0.75)
  # Fitted to ~76% of the box on its larger axis, and centred.
  $bigAxis = [Math]::Max($s.W, $s.H)
  $fitted = ($bigAxis -gt 180) -and ($bigAxis -le 200)
  $cx = ($s.MinX + $s.MaxX) / 2.0; $cy = ($s.MinY + $s.MaxY) / 2.0
  $centred = ([Math]::Abs($cx - 128) -le 3) -and ([Math]::Abs($cy - 128) -le 3)
  Check "glyph $($e.Id): real ink, fitted and centred" ($ok -and $fitted -and $centred) `
    ("ink={0:P1} box={1}x{2} centre=({3:N0},{4:N0})" -f $frac, $s.W, $s.H, $cx, $cy)
  $bmp.Dispose()
}
Check 'ids are unique' ((($ids | Sort-Object -Unique).Count) -eq $ids.Count)

# --- glyphs are visually distinct from one another --------------------------
$sigs = @{}
foreach ($e in $icons) {
  $bmp = Render $e.Id $white 64
  $s = InkStats $bmp
  $sigs[$e.Id] = "$($s.Count)"
  $bmp.Dispose()
}
$distinct = ($sigs.Values | Sort-Object -Unique).Count
Check 'glyphs are distinct from one another' ($distinct -eq $icons.Count) "$distinct distinct of $($icons.Count)"

# --- tinting -----------------------------------------------------------------
$red = Render 'lock' ([System.Drawing.Color]::FromArgb(255,220,30,30)) 128
$r = New-Object System.Drawing.Rectangle(0,0,128,128)
$d = $red.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$buf = New-Object byte[] ($d.Stride * 128)
[Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
$red.UnlockBits($d)
$found = $false
for ($i = 0; $i -lt $buf.Length; $i += 4) {
  if ($buf[$i+3] -gt 250) { $found = ($buf[$i+2] -gt 200 -and $buf[$i+1] -lt 60 -and $buf[$i] -lt 60); break }
}
Check 'tint colours the glyph' $found
$red.Dispose()

# --- caching hands out copies, never the cached instance --------------------
$a1 = Render 'lock' $white 64
$a2 = Render 'lock' $white 64
Check 'render returns a fresh bitmap each time' (-not [object]::ReferenceEquals($a1, $a2))
$a1.Dispose()
# Disposing one copy must not damage the next.
$a3 = Render 'lock' $white 64
$st = InkStats $a3
Check 'cache survives a caller disposing its copy' ($st.Count -gt 0) "ink px=$($st.Count)"
$a2.Dispose(); $a3.Dispose()

# --- unknown ids and sizes ---------------------------------------------------
Check 'unknown id renders nothing' ($null -eq (Render 'no-such-icon' $white 64))
Check 'null id renders nothing' ($null -eq (Render $null $white 64))
Check 'zero size renders nothing' ($null -eq (Render 'lock' $white 0))

# --- OverlayIcon routes gallery icons and falls back safely -----------------
$fallback = New-Object System.Drawing.Bitmap 8, 8
function Load($state) {
  $a = New-Object object[] 2
  $a[0] = $state.PSObject.BaseObject; $a[1] = $fallback.PSObject.BaseObject
  $oiT.GetMethod('Load').Invoke($null, $a)
}
$g1 = [Activator]::CreateInstance($stT)
$g1.IconSource = [Enum]::Parse($srcT, 'Gallery'); $g1.GalleryIconId = 'keyboard'
$got = Load $g1
Check 'OverlayIcon renders a gallery icon' (-not [object]::ReferenceEquals($got, $fallback))
Check 'gallery icon is rendered at badge size' ($got.Width -eq 256) $got.Width
$got.Dispose()

$g2 = [Activator]::CreateInstance($stT)
$g2.IconSource = [Enum]::Parse($srcT, 'Gallery'); $g2.GalleryIconId = 'bogus'
Check 'unknown gallery id falls back to the default icon' ([object]::ReferenceEquals((Load $g2), $fallback))

$g3 = [Activator]::CreateInstance($stT)
$g3.IconSource = [Enum]::Parse($srcT, 'Gallery')   # no id recorded yet
$got3 = Load $g3
Check 'gallery with no id chosen still renders' (-not [object]::ReferenceEquals($got3, $fallback))
$got3.Dispose()

$g4 = [Activator]::CreateInstance($stT)
Check 'default source uses the bundled cat' ([object]::ReferenceEquals((Load $g4), $fallback))

$g5 = [Activator]::CreateInstance($stT)
$g5.IconSource = [Enum]::Parse($srcT, 'Custom')    # no file set
Check 'custom source with no file falls back' ([object]::ReferenceEquals((Load $g5), $fallback))
$fallback.Dispose()

Write-Host ''
if ($fails -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green } else { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }
