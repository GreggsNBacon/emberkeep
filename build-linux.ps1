# Builds Emberkeep for Linux (x64), from Windows:
#   dist\emberkeep_<version>_amd64.deb            Ubuntu, Debian, Mint… (sudo apt install ./emberkeep_<version>_amd64.deb)
#   dist\Emberkeep-<version>-linux-x64.tar.gz     any Linux with systemd (sudo ./install.sh); also what self-update uses
# Same version numbering as build.ps1. Needs Node (linux\pkg.js writes the packages with Unix permissions).
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
dotnet publish src/Emberkeep.Linux/Emberkeep.Linux.csproj -c Release -r linux-x64 -o $out "-p:Version=$version" "-p:FileVersion=$fileVersion" "-p:AssemblyVersion=$fileVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$root = (Resolve-Path .).Path
$bin = "$root/$out/emberkeep"
$L = "$root/linux"
$icon = "$root/branding/source/emberkeep.svg"
$spec = [ordered]@{
  package = 'emberkeep'; version = $version; arch = 'amd64'
  control = [ordered]@{
    Maintainer = 'GreggsNBacon <22661097+GreggsNBacon@users.noreply.github.com>'
    Section = 'games'; Priority = 'optional'
    Homepage = 'https://github.com/GreggsNBacon/emberkeep'
    # Self-contained .NET: the C library, OpenSSL (HTTPS) and zlib. No ICU (invariant globalization).
    Depends = 'libc6 (>= 2.27), libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3t64 | libssl3 | libssl1.1, ca-certificates, adduser | passwd'
    Recommends = 'lib32gcc-s1'
    Suggests = 'default-jre-headless, xvfb, chromium | google-chrome-stable'
    Description = "Game server manager with a web interface`nRuns your game servers as a service: start and stop, console, config files,`nbackups, schedules, installing servers with SteamCMD, router ports (UPnP)`nand a Discord bot. Open it from the apps menu, or from a browser."
  }
  files = @(
    @{ src = $bin; dest = '/opt/emberkeep/emberkeep'; mode = '755' }
    @{ link = '/usr/bin/emberkeep'; target = '/opt/emberkeep/emberkeep' }
    @{ src = "$L/emberkeep.service"; dest = '/usr/lib/systemd/system/emberkeep.service' }
    @{ src = "$L/emberkeep.desktop"; dest = '/usr/share/applications/emberkeep.desktop' }
    @{ src = $icon; dest = '/usr/share/icons/hicolor/scalable/apps/emberkeep.svg' }
    @{ src = "$L/README.txt"; dest = '/usr/share/doc/emberkeep/README.txt' }
  )
  scripts = [ordered]@{ postinst = "$L/deb/postinst"; prerm = "$L/deb/prerm"; postrm = "$L/deb/postrm" }
  tarball = @{
    folder = "emberkeep-$version"
    files = @(
      @{ src = $bin; mode = '755' }
      @{ src = "$L/install.sh"; mode = '755' }
      @{ src = "$L/uninstall.sh"; mode = '755' }
      @{ src = "$L/emberkeep.service" }
      @{ src = "$L/emberkeep.desktop" }
      @{ src = $icon }
      @{ src = "$L/README.txt" }
    )
  }
}
$specFile = "$root/$out/package.json"
[IO.File]::WriteAllText($specFile, ($spec | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))

New-Item -ItemType Directory -Force dist | Out-Null
Get-ChildItem dist/*-linux-x64.tar.gz, dist/emberkeep_*_amd64.deb -ErrorAction SilentlyContinue | Remove-Item
$node = if ($env:EMBERKEEP_NODE) { $env:EMBERKEEP_NODE } else { 'node' }
& $node linux/pkg.js tar $specFile "dist/Emberkeep-$version-linux-x64.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "making the tarball failed" }
& $node linux/pkg.js deb $specFile "dist/emberkeep_${version}_amd64.deb"
if ($LASTEXITCODE -ne 0) { throw "making the .deb failed" }
Get-ChildItem dist/*-linux-x64.tar.gz, dist/emberkeep_*_amd64.deb | ForEach-Object { Write-Host "Linux package: $($_.FullName)" }
