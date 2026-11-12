Emberkeep for Linux
===================

Emberkeep runs your game servers and gives you a web page to control them: start and stop, console,
config files, backups, schedules, installing servers with SteamCMD, router ports and a Discord bot.
On Linux it runs as a service in the background. On a desktop it opens in its own window from the
apps menu; on a server without a desktop, you use the same interface from a browser on another computer.

Install on Ubuntu, Debian, Mint and others based on them: the .deb package from the release page,

  sudo apt install ./emberkeep_<version>_amd64.deb

or, on any Linux with systemd (Fedora, Arch, openSUSE…), this tarball:

  sudo ./install.sh

Either way, Emberkeep runs as the "emberkeep" service (its own user, starting with the machine).
On a computer with a desktop, open Emberkeep from the apps menu: it opens in its own window, signed
in as the host. Whoever installed it is added to the "emberkeep" group, which is what allows that
(from their next login; until then it asks for the password). Add others with:
  sudo usermod -aG emberkeep <name>

Emberkeep only listens on 127.0.0.1 until you turn on remote access, so on a server without a
desktop, forward the port over SSH and open the link from "sudo emberkeep link" on your computer:

  ssh -L 8765:127.0.0.1:8765 you@your-server

Then, in App settings: create accounts (Users & groups) and turn on Allow remote access. People then
sign in at http://<your server>:8765. Allow that port in your firewall (ufw, firewalld) if you have one.

  emberkeep open                         open Emberkeep in its own window (desktop)
  sudo emberkeep link                    the host's sign-in link (changes each time Emberkeep starts)
  sudo systemctl status emberkeep        is it running?
  sudo journalctl -u emberkeep -f        its output
  sudo systemctl restart emberkeep       restart it (servers are stopped properly first)
  sudo apt remove emberkeep              remove the .deb (settings and game servers stay)
  sudo ./uninstall.sh [--purge]          remove a tarball install (--purge also deletes settings and game servers)

Everything Emberkeep keeps is in /var/lib/emberkeep (the emberkeep user's home):
settings in .config/Emberkeep, new game servers in "Game Servers", SteamCMD and backups in .local/share/Emberkeep.

Installing servers with SteamCMD needs its 32-bit libraries:
  Debian/Ubuntu: sudo dpkg --add-architecture i386 && sudo apt update && sudo apt install lib32gcc-s1
  Fedora:        sudo dnf install glibc.i686 libstdc++.i686
Minecraft (Java) needs Java, for example: sudo apt install openjdk-21-jre-headless

Games: Palworld, Valheim, Satisfactory, Minecraft (Java and Bedrock), 7 Days to Die, Project Zomboid,
Terraria, ARK: Survival Evolved, Core Keeper (needs xvfb) and Rust have Linux servers, plus any other
server you add as "Other / Custom". Enshrouded, V Rising, ARK: Survival Ascended, Sons of the Forest,
Conan Exiles and Assetto Corsa EVO only have Windows servers, so they're marked Windows only.

Without systemd (or to try it): run ./emberkeep as a normal user and open the link it prints.
Updates: App settings → Updates. A tarball install updates itself in place and restarts; with the
.deb, download the new .deb and install it the same way.

Watchfire (a separate app for opening router ports, with a desktop window) has its own Linux
downloads on its release page.
