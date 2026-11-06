# Renders Emberkeep's and Watchfire's artwork from source\emberkeep.svg and source\watchfire.svg (Windows, Microsoft Edge):
#   - app icons (.ico with 16-256 px, each size rasterised at its own size so small ones stay crisp):
#       ..\src\Emberkeep\Assets\emberkeep.ico and <Watchfire>\assets\watchfire.ico
#   - emberkeep-logo.png / watchfire-logo.png (1024, transparent), the Discord avatar and banner
#   - favicons: copies of the SVGs into each app's web UI
#   powershell -ExecutionPolicy Bypass -File branding\build-icons.ps1 [-WatchfireDir ..\..\Watchfire]
param([string]$WatchfireDir = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$here = $PSScriptRoot
$src = Join-Path $here 'source'
$repo = Split-Path $here -Parent
if (-not $WatchfireDir) {
  foreach ($d in '..\Watchfire', '..\PortHop') { $p = Join-Path $repo $d; if (Test-Path (Join-Path $p 'server.js')) { $WatchfireDir = $p; break } }
}
$render = Join-Path $src 'render.ps1'
$sizes = 256, 128, 64, 48, 40, 32, 24, 20, 16
$tmp = Join-Path $env:TEMP 'emberkeep-icons'; New-Item -ItemType Directory -Force $tmp | Out-Null

function Write-Ico([string]$logo, [string]$out) {
  $strip = Join-Path $tmp "$logo-strip.png"
  $width = ($sizes | Measure-Object -Sum).Sum + 8 * $sizes.Count
  & powershell -NoProfile -ExecutionPolicy Bypass -File $render -Html (Join-Path $src 'icon-strip.html') -Query "?logo=$logo" -Out $strip -Width $width -Height 256 | Out-Null
  $img = [System.Drawing.Bitmap]::FromFile($strip)
  $pngs = @(); $x = 0
  foreach ($s in $sizes) {
    $bmp = $img.Clone((New-Object System.Drawing.Rectangle $x, 0, $s, $s), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $pngs += , $ms.ToArray(); $x += $s + 8
  }
  $img.Dispose()
  $o = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $o
  $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i] % 256
    $w.Write([byte]$n); $w.Write([byte]$n); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
  }
  foreach ($p in $pngs) { $w.Write($p) }
  $w.Flush(); [System.IO.File]::WriteAllBytes($out, $o.ToArray())
  "Wrote $out"
}

function Render([string]$html, [string]$out, [int]$w = 1024, [int]$h = 1024, [string]$q = '') {
  $extra = if ($q) { @('-Query', $q) } else { @() }
  & powershell -NoProfile -ExecutionPolicy Bypass -File $render -Html (Join-Path $src $html) -Out $out -Width $w -Height $h @extra | Out-Null
  if (-not (Test-Path $out)) { throw "Rendering $html failed." }
  "Wrote $out"
}

Write-Ico 'emberkeep' (Join-Path $repo 'src\Emberkeep\Assets\emberkeep.ico')
Copy-Item (Join-Path $src 'emberkeep.svg') (Join-Path $repo 'src\Emberkeep\wwwroot\favicon.svg') -Force
Render 'logo.html' (Join-Path $here 'emberkeep-logo.png') -q '?logo=emberkeep'
Render 'logo.html' (Join-Path $here 'watchfire-logo.png') -q '?logo=watchfire'
Render 'logo-avatar.html' (Join-Path $here 'emberkeep-discord-avatar.png')
Render 'banner.html' (Join-Path $here 'emberkeep-discord-banner.png') 1360 480
if ($WatchfireDir) {
  Write-Ico 'watchfire' (Join-Path $WatchfireDir 'assets\watchfire.ico')
  Copy-Item (Join-Path $src 'watchfire.svg') (Join-Path $WatchfireDir 'assets\watchfire.svg') -Force
} else { Write-Warning 'Watchfire source folder not found: skipped its icon.' }
