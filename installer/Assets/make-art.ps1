# Regenerates the installer bitmaps (WiX expects 493x312 and 493x58, 24-bit BMP) with the Emberkeep flame logo
# (the same shape as src\Emberkeep\wwwroot\favicon.svg) in the Flame theme's colours.
#   powershell -ExecutionPolicy Bypass -File installer\Assets\make-art.ps1
param([string]$Dir = $PSScriptRoot)
Add-Type -AssemblyName System.Drawing

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function P([single]$x, [single]$y) { New-Object System.Drawing.PointF $x, $y }

# The logo on a 64x64 grid, drawn at ($x,$y) with size $s. $cut is the colour behind it: the server-rack slots are cut out
# of the flame, so they show the background.
function Logo($g, [single]$x, [single]$y, [single]$s, $cut) {
  $k = $s / 64
  function Q([single]$px, [single]$py) { P ($x + $px * $k) ($y + $py * $k) }
  $flame = New-Object System.Drawing.Drawing2D.GraphicsPath
  # M32 2 C39 12 53 21 53 39 C53 52 44 62 32 62 C20 62 11 52 11 39 C11 30 16 24 20 17 C21.5 24 24.5 27.5 27.5 28.5 C25.5 20 26.5 10 32 2 Z
  $flame.AddBezier((Q 32 2), (Q 39 12), (Q 53 21), (Q 53 39))
  $flame.AddBezier((Q 53 39), (Q 53 52), (Q 44 62), (Q 32 62))
  $flame.AddBezier((Q 32 62), (Q 20 62), (Q 11 52), (Q 11 39))
  $flame.AddBezier((Q 11 39), (Q 11 30), (Q 16 24), (Q 20 17))
  $flame.AddBezier((Q 20 17), (Q 21.5 24), (Q 24.5 27.5), (Q 27.5 28.5))
  $flame.AddBezier((Q 27.5 28.5), (Q 25.5 20), (Q 26.5 10), (Q 32 2))
  $flame.CloseFigure()
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (Q 0 0), (Q 0 64.5), (C '#ffb347'), (C '#d32f3f')
  $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
  $blend.Colors = @((C '#ffb347'), (C '#ff6a2b'), (C '#d32f3f')); $blend.Positions = @([single]0, [single]0.55, [single]1)
  $grad.InterpolationColors = $blend
  $g.FillPath($grad, $flame)
  $slot = New-Object System.Drawing.SolidBrush $cut
  foreach ($ry in 31, 40, 49) {
    $r = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rx = $x + 21 * $k; $top = $y + $ry * $k; $w = 22 * $k; $h = 7 * $k; $d = 4 * $k
    $r.AddArc($rx, $top, $d, $d, 180, 90); $r.AddArc($rx + $w - $d, $top, $d, $d, 270, 90)
    $r.AddArc($rx + $w - $d, $top + $h - $d, $d, $d, 0, 90); $r.AddArc($rx, $top + $h - $d, $d, $d, 90, 90); $r.CloseFigure()
    $g.FillPath($slot, $r)
    $g.FillEllipse($grad, $x + (25 - 1.4) * $k, $y + ($ry + 3.5 - 1.4) * $k, 2.8 * $k, 2.8 * $k) # the rack's status light
  }
}

# Welcome / finish pages: a dark Flame panel on the left (164px) with the logo, white where the installer's text goes.
$bmp = New-Object System.Drawing.Bitmap 493, 312, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 0), (P 0 312), (C '#221d20'), (C '#131012')
$g.FillRectangle($bg, 0, 0, 164, 312)
$accent = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 0), (P 164 0), (C '#ffb23a'), (C '#ef5a24')
$g.FillRectangle($accent, 0, 0, 164, 4)
# A soft glow behind the flame
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath; $glow.AddEllipse(22, 66, 120, 120)
$gb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$gb.CenterColor = [System.Drawing.Color]::FromArgb(70, 239, 90, 36); $gb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 239, 90, 36))
$g.FillPath($gb, $glow)
# Logo over the glow: the slots show the panel colour at that height.
Logo $g 42 86 80 (C '#2a1c19')
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString('Emberkeep', (New-Object System.Drawing.Font 'Segoe UI Semibold', 15), (New-Object System.Drawing.SolidBrush (C '#f3ebe6')), (New-Object System.Drawing.RectangleF 0, 182, 164, 30), $fmt)
$g.DrawString('Game server manager', (New-Object System.Drawing.Font 'Segoe UI', 8.5), (New-Object System.Drawing.SolidBrush (C '#b3a59e')), (New-Object System.Drawing.RectangleF 0, 210, 164, 20), $fmt)
$g.Dispose(); $bmp.Save((Join-Path $Dir 'Dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# Banner across the top of the inner pages: white behind the page title, the logo at the right.
$bmp = New-Object System.Drawing.Bitmap 493, 58, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::White)
Logo $g 437 7 44 ([System.Drawing.Color]::White)
$g.Dispose(); $bmp.Save((Join-Path $Dir 'Banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
"art written"
