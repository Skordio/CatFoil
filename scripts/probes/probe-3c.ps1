# Step 3c: opacity, blocked opacity, ring opacity, background shape + colour.
# Pure pixel work — no settings file is read or written.
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

Add-Type -AssemblyName System.Drawing

$asm = [Reflection.Assembly]::LoadFrom($CatFoilDll)
$rT  = $asm.GetType('CatFoil.OverlayRenderer')
$stT = $asm.GetType('CatFoil.OverlayStateSettings')
$shapeT = $asm.GetType('CatFoil.OverlayShape')

$fails = 0
function Check($name, $cond, $detail = '') {
  if ($cond) { Write-Host "PASS  $name" }
  else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:fails++ }
}
function Shape($n) { [Enum]::Parse($shapeT, $n) }

function MakeIcon($n = 256) {
  $b = New-Object System.Drawing.Bitmap($n, $n, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,90,180,240))), 10, 10, $n-20, $n-20)
  $g.Dispose(); $b
}
$icon = MakeIcon

function Render($state, $size, $text, $flash, $blocked) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  $a = New-Object object[] 7
  $a[0] = $g.PSObject.BaseObject
  $a[1] = (New-Object System.Drawing.Rectangle(0,0,$size,$size)).PSObject.BaseObject
  $a[2] = $state.PSObject.BaseObject
  $a[3] = $icon.PSObject.BaseObject
  $a[4] = $text
  $a[5] = [bool]$flash
  $a[6] = [bool]$blocked
  $rT.GetMethod('Draw').Invoke($null, $a) | Out-Null
  $g.Dispose(); $bmp
}
function Bytes($bmp) {
  $r = New-Object System.Drawing.Rectangle(0,0,$bmp.Width,$bmp.Height)
  $d = $bmp.LockBits($r, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $buf = New-Object byte[] ($d.Stride * $bmp.Height)
  [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
  $bmp.UnlockBits($d); ,$buf
}
# B,G,R,A at a pixel
function Px($bmp, $bytes, $x, $y) {
  $i = ($y * $bmp.Width * 4) + ($x * 4)
  [pscustomobject]@{ B=$bytes[$i]; G=$bytes[$i+1]; R=$bytes[$i+2]; A=$bytes[$i+3] }
}
function NewState($size) { $s = [Activator]::CreateInstance($stT); $s.Size = $size; $s }

# --- 1. Opacity scales every layer by the SAME amount -----------------------
# This is the defect the per-layer design would have had: the background box,
# the icon over it and the countdown over that must all end up at 20%, not
# 20/36/49%. Comparing whole images at two opacities proves it everywhere.
$full = NewState 64; $full.Opacity = 100
$dim  = NewState 64; $dim.Opacity  = 20
$bFull = Render $full 64 '2:05' $false $false
$bDim  = Render $dim  64 '2:05' $false $false
$fB = Bytes $bFull; $dB = Bytes $bDim
$maxDev = 0; $layered = 0
for ($i = 0; $i -lt $fB.Length; $i += 4) {
  $exp = [int][Math]::Round($fB[$i+3] * 51.0 / 255.0)   # OpacityAlpha(20) = 51
  $dev = [Math]::Abs([int]$dB[$i+3] - $exp)
  if ($dev -gt $maxDev) { $maxDev = $dev }
  if ($fB[$i+3] -gt 250) { $layered++ }
}
Check 'opacity scales all layers uniformly' ($maxDev -le 1) "max deviation=$maxDev"
Check 'the scene actually had opaque, overlapping layers' ($layered -gt 500) "opaque px=$layered"

# --- 2. Blocked opacity is held, and independent of the ring ----------------
$b1 = NewState 64; $b1.BlockedOpacityEnabled = $true; $b1.BlockedOpacity = 100
$b2 = NewState 64; $b2.BlockedOpacityEnabled = $true; $b2.BlockedOpacity = 15
$r1 = Render $b1 64 $null $true $true
$r2 = Render $b2 64 $null $true $true
$p1 = Bytes $r1; $p2 = Bytes $r2

$body1 = Px $r1 $p1 32 32     # icon centre
$body2 = Px $r2 $p2 32 32
$ring1 = Px $r1 $p1 3 32      # on the ring stroke at the left edge
$ring2 = Px $r2 $p2 3 32
Check 'blocked opacity dims the badge body' ($body2.A -lt ($body1.A / 2)) "$($body1.A) -> $($body2.A)"
Check 'ring is unaffected by blocked opacity' ([Math]::Abs([int]$ring1.A - [int]$ring2.A) -le 1) "$($ring1.A) vs $($ring2.A)"
Check 'ring stays strong while the body fades' ($ring2.A -gt ($body2.A * 2)) "ring=$($ring2.A) body=$($body2.A)"

# Disabled -> blocked is ignored entirely.
$off = NewState 64; $off.BlockedOpacityEnabled = $false; $off.BlockedOpacity = 15
$offA = Render $off 64 $null $false $false; $offAb = Bytes $offA
$offB = Render $off 64 $null $false $true;  $offBb = Bytes $offB
$same = $true
for ($i = 0; $i -lt $offAb.Length; $i++) { if ($offAb[$i] -ne $offBb[$i]) { $same = $false; break } }
Check 'blocked opacity ignored when the option is off' $same

# --- 3. Ring opacity ---------------------------------------------------------
$noRing = NewState 64; $noRing.RingOpacity = 0
$withFlash = Render $noRing 64 $null $true $false
$noFlash   = Render $noRing 64 $null $false $false
$wB = Bytes $withFlash; $nB = Bytes $noFlash
$same = $true
for ($i = 0; $i -lt $wB.Length; $i++) { if ($wB[$i] -ne $nB[$i]) { $same = $false; break } }
Check 'ring opacity 0 hides the ring entirely' $same

$halfRing = NewState 64; $halfRing.RingOpacity = 50
$hr = Render $halfRing 64 $null $true $false; $hrB = Bytes $hr
$fullRing = NewState 64; $fullRing.RingOpacity = 100
$fr = Render $fullRing 64 $null $true $false; $frB = Bytes $fr
$hp = Px $hr $hrB 3 32; $fp = Px $fr $frB 3 32
Check 'ring opacity 50 is dimmer than 100' ($hp.A -lt $fp.A) "50%=$($hp.A) 100%=$($fp.A)"

# --- 4. Shape ----------------------------------------------------------------
# The top-left corner distinguishes them: filled for a square, empty otherwise.
foreach ($case in @(
    @{ shape='Square';        corner=$true  },
    @{ shape='RoundedSquare'; corner=$false },
    @{ shape='Circle';        corner=$false },
    @{ shape='None';          corner=$false })) {
  $s = NewState 64; $s.Shape = Shape $case.shape; $s.Opacity = 100
  $bmp = Render $s 64 $null $false $false; $by = Bytes $bmp
  $c = Px $bmp $by 1 1
  $filled = $c.A -gt 200
  Check "shape $($case.shape): corner $(if ($case.corner) {'filled'} else {'clear'})" ($filled -eq $case.corner) "alpha=$($c.A)"
  $bmp.Dispose()
}

# Centre is filled for every shape except None.
foreach ($n in 'Square','RoundedSquare','Circle') {
  $s = NewState 64; $s.Shape = Shape $n; $s.Opacity = 100
  $bmp = Render $s 64 $null $false $false; $by = Bytes $bmp
  Check "shape $n fills the centre" ((Px $bmp $by 32 60).A -gt 200)
  $bmp.Dispose()
}
$sN = NewState 64; $sN.Shape = Shape 'None'; $sN.Opacity = 100
$bmpN = Render $sN 64 $null $false $false; $byN = Bytes $bmpN
Check 'shape None leaves the badge transparent behind the icon' ((Px $bmpN $byN 32 60).A -lt 20) "alpha=$((Px $bmpN $byN 32 60).A)"
$bmpN.Dispose()

# --- 5. Background colour ----------------------------------------------------
$red = NewState 64; $red.Opacity = 100; $red.BackgroundColor = '#FF0000'
$rb = Render $red 64 $null $false $false; $rbB = Bytes $rb
$rp = Px $rb $rbB 32 60
Check 'background colour is honoured' ($rp.R -gt 200 -and $rp.G -lt 40 -and $rp.B -lt 40) "R=$($rp.R) G=$($rp.G) B=$($rp.B)"

$bad = NewState 64; $bad.Opacity = 100; $bad.BackgroundColor = 'not a colour'
$bb = Render $bad 64 $null $false $false; $bbB = Bytes $bb
$bp = Px $bb $bbB 32 60
Check 'unreadable colour falls back to the default, not invisible' `
  ($bp.A -gt 200 -and $bp.R -lt 80 -and $bp.G -lt 80) "A=$($bp.A) R=$($bp.R) G=$($bp.G)"

$empty = NewState 64; $empty.Opacity = 100; $empty.BackgroundColor = ''
$eb = Render $empty 64 $null $false $false; $ebB = Bytes $eb
Check 'blank colour falls back too' ((Px $eb $ebB 32 60).A -gt 200)

# --- 6. No halo introduced by the fade blit ---------------------------------
# The icon has soft edges; the outermost ring of the bitmap must stay fully
# transparent with no background box.
$noBg = NewState 64; $noBg.Shape = Shape 'None'; $noBg.Opacity = 100
$nb = Render $noBg 64 $null $false $false; $nbB = Bytes $nb
$edgeMax = 0
for ($x = 0; $x -lt 64; $x++) {
  foreach ($y in 0, 63) { $a = (Px $nb $nbB $x $y).A; if ($a -gt $edgeMax) { $edgeMax = $a } }
}
for ($y = 0; $y -lt 64; $y++) {
  foreach ($x in 0, 63) { $a = (Px $nb $nbB $x $y).A; if ($a -gt $edgeMax) { $edgeMax = $a } }
}
Check 'no halo along the bitmap edge' ($edgeMax -eq 0) "max edge alpha=$edgeMax"

foreach ($b in $bFull,$bDim,$r1,$r2,$offA,$offB,$withFlash,$noFlash,$hr,$fr,$rb,$bb,$eb,$nb) { $b.Dispose() }
$icon.Dispose()

Write-Host ''
if ($fails -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green } else { Write-Host "$fails FAILED" -ForegroundColor Red; exit 1 }

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
