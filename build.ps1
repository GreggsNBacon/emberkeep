# Builds Hostbay.exe, bundles PortHop, and builds the installer.
#   .\build.ps1            -> publish\Hostbay.exe, publish\porthop\PortHop.exe and dist\Hostbay-Setup.msi
#
# PortHop: built from ..\PortHop with Node (node on PATH, or $env:HOSTBAY_NODE pointing at node.exe).
# Without Node, the existing ..\PortHop\dist\PortHop.exe is bundled as-is.
#
# Versions: 0.3.<commit count>, with the minute of the day as the 4th part of the file version so
# every build is distinguishable in Apps. Each installer also gets a fresh ProductCode, so running
# a newer build always replaces the installed one.
param(
  [string]$PortHopDir = (Join-Path $PSScriptRoot '..\PortHop')
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$major = '0.3'
$count = 0
try { $count = [int](git rev-list --count HEAD 2>$null) } catch { }
$minute = [int]((Get-Date) - (Get-Date).Date).TotalMinutes
$version = "$major.$count"
$fileVersion = "$version.$minute"
Write-Host "Version $fileVersion"

Write-Host 'Publishing Hostbay.exe...'
dotnet publish src\Hostbay\Hostbay.csproj -c Release -o publish "-p:Version=$version" "-p:FileVersion=$fileVersion" "-p:AssemblyVersion=$fileVersion"
if ($LASTEXITCODE) { throw 'Publishing failed.' }

Write-Host 'Preparing PortHop...'
$phOut = Join-Path $PSScriptRoot 'publish\porthop'
New-Item -ItemType Directory -Force $phOut | Out-Null
$node = $env:HOSTBAY_NODE
if (-not $node) { $node = (Get-Command node -ErrorAction SilentlyContinue).Source }
if ($node -and (Test-Path (Join-Path $PortHopDir 'build.js'))) {
  # PortHop's build stamps its version and icon with the resedit package: install it the first time.
  if ((Test-Path (Join-Path $PortHopDir 'package.json')) -and -not (Test-Path (Join-Path $PortHopDir 'node_modules\resedit'))) {
    $npm = Join-Path (Split-Path $node) 'npm.cmd'
    if (-not (Test-Path $npm)) { $npm = 'npm' }
    Push-Location $PortHopDir
    try { & $npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install for PortHop failed.' } } finally { Pop-Location }
  }
  & $node (Join-Path $PortHopDir 'build.js') --out $phOut
  if ($LASTEXITCODE) { throw 'Building PortHop failed.' }
} elseif (Test-Path (Join-Path $PortHopDir 'dist\PortHop.exe')) {
  Write-Host 'Node not found: bundling the existing PortHop build.'
  Copy-Item (Join-Path $PortHopDir 'dist\PortHop.exe') (Join-Path $phOut 'PortHop.exe') -Force
} else {
  throw "PortHop not found in $PortHopDir. Pass -PortHopDir, or set HOSTBAY_NODE to build it."
}

Write-Host 'Building the installer...'
# A fresh ProductCode per installer, and a full rebuild: WiX's incremental build would otherwise
# reuse the previous output and its ProductCode, turning upgrades into failed "repairs".
$productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
dotnet build installer\Hostbay.Installer.wixproj -c Release -t:Rebuild -o dist "-p:ProductCode=$productCode" "-p:PortHopExe=$(Join-Path $phOut 'PortHop.exe')"
if ($LASTEXITCODE) { throw 'Building the installer failed.' }

Get-ChildItem dist\*.msi | ForEach-Object { Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB), version $fileVersion" }
