# Builds the single-file exe and the installer.
#   .\build.ps1            -> publish\Hostbay.exe and dist\Hostbay-Setup.msi
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host 'Publishing Hostbay.exe...'
dotnet publish src\Hostbay\Hostbay.csproj -c Release -o publish
if ($LASTEXITCODE) { throw 'Publishing failed.' }

Write-Host 'Building the installer...'
dotnet build installer\Hostbay.Installer.wixproj -c Release -o dist
if ($LASTEXITCODE) { throw 'Building the installer failed.' }

Get-ChildItem dist\*.msi | ForEach-Object { Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
