# Contact sheet of candidate gallery glyphs, rendered from Segoe MDL2 Assets
# (the Windows 10 font, so anything here also exists in Windows 11's Fluent).
# Rendered exactly the way IconGallery will: GraphicsPath.AddString + FillPath,
# centred on the glyph's INK bounds rather than its line box.
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

$font = 'Segoe MDL2 Assets'
$cell = 110
$cols = 8
$codes = @(
  0xE72E, 0xE785, 0xE765, 0xE7BA, 0xE783, 0xE711, 0xE8FB, 0xE734,
  0xE735, 0xEB52, 0xE77B, 0xE713, 0xE80F, 0xE945, 0xE72C, 0xE890,
  0xE706, 0xE8C8, 0xE7C1, 0xE909, 0xE753, 0xE946, 0xE8BB, 0xE72B,
  0xE81C, 0xE7EE, 0xE90A, 0xE7F4, 0xE8D7, 0xEA80, 0xE72F, 0xE81D,
  0xE930, 0xE95E, 0xE9D9, 0xE897, 0xE7C3, 0xE8EC, 0xE91B, 0xE9CE,
  0xE001, 0xE00B, 0xE0A5, 0xE1CF, 0xE13D, 0xE181, 0xE188, 0xE72D
)

$rows = [Math]::Ceiling($codes.Count / $cols)
$bmp = New-Object System.Drawing.Bitmap ($cols * $cell), ($rows * $cell)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::White)

$fam = New-Object System.Drawing.FontFamily($font)
$label = New-Object System.Drawing.Font('Segoe UI', 7.5)
$ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 25, 25, 30))
$grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 120, 120, 120))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 220, 220, 220))

$empty = @()
for ($i = 0; $i -lt $codes.Count; $i++) {
  $cx = ($i % $cols) * $cell
  $cy = [Math]::Floor($i / $cols) * $cell
  $g.DrawRectangle($pen, $cx, $cy, $cell - 1, $cell - 1)

  $ch = [char]::ConvertFromUtf32($codes[$i])
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $sf = [System.Drawing.StringFormat]::GenericTypographic
  $path.AddString($ch, $fam, 0, 64.0, (New-Object System.Drawing.PointF 0, 0), $sf)

  $b = $path.GetBounds()
  if ($b.Width -lt 0.5 -or $b.Height -lt 0.5) {
    $script:empty += ('{0:X4}' -f $codes[$i])
    $g.DrawString(('{0:X4} EMPTY' -f $codes[$i]), $label, $grey, $cx + 4, $cy + $cell - 14)
    $path.Dispose(); continue
  }

  # Scale the ink to fit a 72px box and centre it in the cell.
  $target = 72.0
  $scale = [Math]::Min($target / $b.Width, $target / $b.Height)
  $m = New-Object System.Drawing.Drawing2D.Matrix
  $m.Translate($cx + ($cell - $b.Width * $scale) / 2 - $b.X * $scale,
               $cy + 6 + ($target - $b.Height * $scale) / 2 - $b.Y * $scale)
  $m.Scale($scale, $scale)
  $path.Transform($m)
  $g.FillPath($ink, $path)
  $g.DrawString(('{0:X4}' -f $codes[$i]), $label, $grey, $cx + 4, $cy + $cell - 14)
  $path.Dispose(); $m.Dispose()
}

$out = (Join-Path $ProbeShots 'glyph-sheet.png')
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $fam.Dispose()

Write-Host "sheet: $out"
if ($empty.Count) { Write-Host "empty/missing: $($empty -join ', ')" -ForegroundColor Yellow }
else { Write-Host 'every candidate produced ink' -ForegroundColor Green }
