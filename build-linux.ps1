# Builds Emberkeep for Linux (x64): a self-contained program plus install scripts, packed as
#   dist\Emberkeep-<version>-linux-x64.tar.gz
# Same version numbering as build.ps1. Needs Node (for the tarball, which keeps Unix permissions).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$major = '0.3'
$count = 0
try { $count = [int](git rev-list --count HEAD 2>$null) } catch { }
$minute = [int]((Get-Date) - (Get-Date).Date).TotalMinutes
$version = "$major.$count"
$fileVersion = "$version.$minute"
Write-Host "Version $fileVersion (Linux)"

$out = 'publish-linux'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish src\Emberkeep.Linux\Emberkeep.Linux.csproj -c Release -r linux-x64 -o $out "-p:Version=$version" "-p:FileVersion=$fileVersion" "-p:AssemblyVersion=$fileVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Shell scripts must have Unix line endings to run.
foreach ($f in 'install.sh', 'uninstall.sh', 'emberkeep.service', 'README.txt') {
  $text = [IO.File]::ReadAllText((Join-Path 'linux' $f)) -replace "`r`n", "`n"
  [IO.File]::WriteAllText((Join-Path $out $f), $text, (New-Object Text.UTF8Encoding $false))
}

New-Item -ItemType Directory -Force dist | Out-Null
Get-ChildItem dist\*-linux-x64.tar.gz -ErrorAction SilentlyContinue | Remove-Item
$node = if ($env:EMBERKEEP_NODE) { $env:EMBERKEEP_NODE } else { 'node' }
$tar = "dist\Emberkeep-$version-linux-x64.tar.gz"
& $node linux\make-tar.js $tar "emberkeep-$version" "$out\emberkeep:755" "$out\install.sh:755" "$out\uninstall.sh:755" "$out\emberkeep.service" "$out\README.txt"
if ($LASTEXITCODE -ne 0) { throw "making the tarball failed" }
Write-Host "Linux package: $(Resolve-Path $tar)"
