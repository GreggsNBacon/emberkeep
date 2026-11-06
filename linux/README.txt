Emberkeep for Linux
===================

Emberkeep runs your game servers and gives you a web page to control them: start and stop, console,
config files, backups, schedules, installing servers with SteamCMD, router ports and a Discord bot.
On Linux it runs as a service; you use it from a browser.

Install (systemd; Ubuntu, Debian, Fedora and most others):

  sudo ./install.sh

That creates an "emberkeep" user, puts the program in /opt/emberkeep, starts the emberkeep service
and prints a link that signs you in as the host. Emberkeep only listens on 127.0.0.1 until you turn
on remote access, so from another computer forward the port over SSH and open the link there:

  ssh -L 8765:127.0.0.1:8765 you@your-server

Then, in App settings: create accounts (Users & groups) and turn on Allow remote access. People then
sign in at http://<your server>:8765. Allow that port in your firewall (ufw, firewalld) if you have one.

  emberkeep-link                         the host's sign-in link (changes each time Emberkeep starts)
  sudo systemctl status emberkeep        is it running?
  sudo journalctl -u emberkeep -f        its output
  sudo systemctl restart emberkeep       restart it (servers are stopped properly first)
  sudo ./uninstall.sh [--purge]          remove it (--purge also deletes settings and game servers)

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
Updates: App settings → Updates installs a new version in place and restarts the service.
