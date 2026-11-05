# Builds the single-file exe and the installer.
#   .\build.ps1            -> publish\Hostbay.exe and dist\Hostbay-Setup.msi
#
# Versions: 0.2.<commit count>, with the minute of the day as the 4th part of the file version so
# every build is distinguishable in Apps. Each installer also gets a fresh ProductCode, so running
# a newer build always replaces the installed one.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$major = '0.2'
$count = 0
try { $count = [int](git rev-list --count HEAD 2>$null) } catch { }
$minute = [int]((Get-Date) - (Get-Date).Date).TotalMinutes
$version = "$major.$count"
$fileVersion = "$version.$minute"
Write-Host "Version $fileVersion"

Write-Host 'Publishing Hostbay.exe...'
dotnet publish src\Hostbay\Hostbay.csproj -c Release -o publish "-p:Version=$version" "-p:FileVersion=$fileVersion" "-p:AssemblyVersion=$fileVersion"
if ($LASTEXITCODE) { throw 'Publishing failed.' }

Write-Host 'Building the installer...'
# A fresh ProductCode per installer, and a full rebuild: WiX's incremental build would otherwise
# reuse the previous output and its ProductCode, turning upgrades into failed "repairs".
$productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
dotnet build installer\Hostbay.Installer.wixproj -c Release -t:Rebuild -o dist "-p:ProductCode=$productCode"
if ($LASTEXITCODE) { throw 'Building the installer failed.' }

Get-ChildItem dist\*.msi | ForEach-Object { Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB), version $fileVersion" }
