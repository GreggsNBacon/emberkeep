# Builds Emberkeep.exe, bundles Watchfire, and builds the installer.
#   .\build.ps1            -> publish\Emberkeep.exe, publish\watchfire\Watchfire.exe and dist\Emberkeep-Setup.msi
#
# Watchfire: built from ..\Watchfire with Node (node on PATH, or $env:EMBERKEEP_NODE pointing at node.exe).
# Without Node, the existing ..\Watchfire\dist\Watchfire.exe is bundled as-is.
#
# Versions: 0.3.<commit count>, with the minute of the day as the 4th part of the file version so
# every build is distinguishable in Apps. Each installer also gets a fresh ProductCode, so running
# a newer build always replaces the installed one.
param(
  [string]$WatchfireDir = (Join-Path $PSScriptRoot '..\Watchfire')
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# Watchfire's source folder may still carry its old name (PortHop) next to this one.
if (-not (Test-Path $WatchfireDir) -and (Test-Path (Join-Path $PSScriptRoot '..\PortHop'))) { $WatchfireDir = Join-Path $PSScriptRoot '..\PortHop' }

$major = '0.3'
$count = 0
try { $count = [int](git rev-list --count HEAD 2>$null) } catch { }
$minute = [int]((Get-Date) - (Get-Date).Date).TotalMinutes
$version = "$major.$count"
$fileVersion = "$version.$minute"
Write-Host "Version $fileVersion"

Write-Host 'Publishing Emberkeep.exe...'
dotnet publish src\Emberkeep\Emberkeep.csproj -c Release -o publish "-p:Version=$version" "-p:FileVersion=$fileVersion" "-p:AssemblyVersion=$fileVersion"
if ($LASTEXITCODE) { throw 'Publishing failed.' }

Write-Host 'Preparing Watchfire...'
$phOut = Join-Path $PSScriptRoot 'publish\watchfire'
New-Item -ItemType Directory -Force $phOut | Out-Null
$node = $env:EMBERKEEP_NODE
if (-not $node) { $node = (Get-Command node -ErrorAction SilentlyContinue).Source }
if ($node -and (Test-Path (Join-Path $WatchfireDir 'build.js'))) {
  # Watchfire's build stamps its version and icon with the resedit package: install it the first time.
  if ((Test-Path (Join-Path $WatchfireDir 'package.json')) -and -not (Test-Path (Join-Path $WatchfireDir 'node_modules\resedit'))) {
    $npm = Join-Path (Split-Path $node) 'npm.cmd'
    if (-not (Test-Path $npm)) { $npm = 'npm' }
    Push-Location $WatchfireDir
    try { & $npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install for Watchfire failed.' } } finally { Pop-Location }
  }
  & $node (Join-Path $WatchfireDir 'build.js') --out $phOut
  if ($LASTEXITCODE) { throw 'Building Watchfire failed.' }
} elseif (Test-Path (Join-Path $WatchfireDir 'dist\Watchfire.exe')) {
  Write-Host 'Node not found: bundling the existing Watchfire build.'
  Copy-Item (Join-Path $WatchfireDir 'dist\Watchfire.exe') (Join-Path $phOut 'Watchfire.exe') -Force
} else {
  throw "Watchfire not found in $WatchfireDir. Pass -WatchfireDir, or set EMBERKEEP_NODE to build it."
}

Write-Host 'Building the installer...'
# A fresh ProductCode per installer, and a full rebuild: WiX's incremental build would otherwise
# reuse the previous output and its ProductCode, turning upgrades into failed "repairs".
$productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
dotnet build installer\Emberkeep.Installer.wixproj -c Release -t:Rebuild -o dist "-p:ProductCode=$productCode" "-p:WatchfireExe=$(Join-Path $phOut 'Watchfire.exe')"
if ($LASTEXITCODE) { throw 'Building the installer failed.' }

Get-ChildItem dist\*.msi | ForEach-Object { Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB), version $fileVersion" }
