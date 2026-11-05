# Regenerates the installer bitmaps (WiX expects 493x312 and 493x58, 24-bit BMP).
param([string]$Dir = $PSScriptRoot)
Add-Type -AssemblyName System.Drawing
function Logo($g, [single]$x, [single]$y, [single]$s) {
  $r = [single]($s * 0.22)
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, $r*2, $r*2, 180, 90); $p.AddArc($x+$s-$r*2, $y, $r*2, $r*2, 270, 90)
  $p.AddArc($x+$s-$r*2, $y+$s-$r*2, $r*2, $r*2, 0, 90); $p.AddArc($x, $y+$s-$r*2, $r*2, $r*2, 90, 90); $p.CloseFigure()
  $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF $x,$y), (New-Object System.Drawing.PointF ($x+$s),($y+$s)), ([System.Drawing.Color]::FromArgb(124,92,255)), ([System.Drawing.Color]::FromArgb(34,211,238))
  $g.FillPath($b, $p)
  $w = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245,255,255,255))
  $d = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(52,211,153))
  $bh = $s*0.13; $gap = $s*0.07; $yy = $y + ($s - (3*$bh + 2*$gap))/2
  for ($i = 0; $i -lt 3; $i++) {
    $g.FillRectangle($w, $x+$s*0.2, $yy, $s*0.6, $bh)
    $g.FillEllipse($d, $x+$s*0.8-$bh*0.85, $yy+$bh*0.27, $bh*0.46, $bh*0.46)
    $yy += $bh + $gap
  }
}
# Welcome / finish page: night-harbour panel on the left (164px), white where the text goes.
$bmp = New-Object System.Drawing.Bitmap 493, 312, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF 0,312), ([System.Drawing.Color]::FromArgb(22,29,42)), ([System.Drawing.Color]::FromArgb(16,21,31))
$g.FillRectangle($bg, 0, 0, 164, 312)
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(124,92,255))), 0, 0, 164, 4)
Logo $g 46 92 72
$f = New-Object System.Drawing.Font 'Segoe UI Semibold', 15
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString('Hostbay', $f, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(223,230,240))), (New-Object System.Drawing.RectangleF 0, 176, 164, 30), $fmt)
$f2 = New-Object System.Drawing.Font 'Segoe UI', 8.5
$g.DrawString('Game server manager', $f2, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(141,154,176))), (New-Object System.Drawing.RectangleF 0, 204, 164, 20), $fmt)
$g.Dispose(); $bmp.Save((Join-Path $Dir 'Dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# Banner across the top of inner pages: white for the title text, logo at the right.
$bmp = New-Object System.Drawing.Bitmap 493, 58, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::White)
Logo $g 441 11 36
$g.Dispose(); $bmp.Save((Join-Path $Dir 'Banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
"art written"
