# Hostbay

A Windows app for running game servers for you and your friends. Run `Hostbay.exe` on the PC that hosts the servers. You can then manage them from that PC or from any browser on your network.

## What it does

- **Finds your servers.** It scans every Steam library, including dedicated servers installed from Steam's Tools library. You can also add any folder, such as a Minecraft server, and Hostbay recognises the game from the files inside.
- **Edits configs.** It finds each game's config files, using known locations plus a scan of the server folder. You can edit them in a form view (INI, `.properties`, JSON, 7 Days to Die XML, and Palworld's single-line `OptionSettings`) or as plain text. Every save keeps a backup (the last 25 per file), and comments, line endings and encoding are preserved.
- **Controls servers.** Start, Stop, Restart and Force-close. Stop is graceful: it sends the game's own shutdown commands, or presses Ctrl+C. It force-closes only if the server doesn't exit within the timeout. You can also turn on auto-restart after a crash.
- **Sends commands.** Commands go through console input (Minecraft, Terraria, Zomboid, Rust), RCON (Palworld, ARK, V Rising, Conan) or Telnet (7 Days to Die). RCON passwords and ports are read from the game's own config.
- **Shows live logs.** The server's console output and its log files stream in real time, with filtering and timestamps.
- **Offers remote access with accounts.** Create accounts in App settings → Users & groups, turn on remote access, then open `http://<host-ip>:8765` from another PC or your phone. Each person signs in with their own username and password and can change their password. Each account belongs to a group. A group either has full access or a chosen set of permissions: start/stop/restart, force-close, console commands, config files, server settings, router ports, and launch settings. A group can see all servers or only selected ones. Launch settings (executable, arguments, adding and removing servers) is a separate permission because it can run programs on the host. The server checks every request against the signed-in account, so nothing loads before sign-in: the page, its script and all data need a valid session. Hiding the login page in the browser therefore gets you nothing.
- **Discord bot.** In App settings → Discord bot, paste the token of a bot you created in the Discord Developer Portal. Hostbay encrypts the token for your Windows account (DPAPI) and never shows it again. Invite the bot with the button provided. It can post a **status board** in a channel: one card per server with its status, art and join address, plus buttons that privately send someone the address so it's easy to copy. Hostbay edits the board as servers start and stop, and when your public IP changes. It also adds `/servers` and `/ip` slash commands, and shows "2 of 3 servers online" as its status. You choose which servers are shared, whether offline ones show, whether replies are private, and whether to share your public IP or a host name such as a dynamic DNS name. The bot only shares information; it can't control servers. Only full-access accounts can change these settings.
- **Hides IP addresses.** The eye button next to a join address masks IPs across the UI, which is useful when streaming. Copy buttons still copy the real address.
- **Game artwork.** Each game's Steam art appears as its sidebar badge and as a banner behind the server header. Pictures are downloaded once from Steam and cached in `%APPDATA%\Hostbay\art`. Custom servers can pick any Steam game's art in Settings → Artwork.
- **Opens router ports with PortHop.** Each server has a Router ports control next to its join address: open or close the ports friends need, with per-port status. Hostbay asks PortHop to do it (starting PortHop in the background if needed), so every forward also shows in PortHop's list and tray and is restored after a router reboot. Without PortHop, Hostbay forwards with its own UPnP. Port lists match PortHop's game list, follow a server's actual join port, and can be edited per server; an optional switch opens them while the server runs. The installer includes PortHop, which also works on its own.
- **Shows join addresses.** Each server shows the address friends join with (your public IP by default, plus the right port, read from the launch arguments or game config), with Copy buttons for local network, VPN and public addresses. IP settings in config files get a "Fill current IP" button. App settings lists all of this PC's addresses with Copy buttons.
- **Picks up running servers.** If a server was already running when Hostbay started, Hostbay detects it and can stop it and show its log files.

## Supported games

Palworld, Enshrouded, Valheim, Satisfactory, Minecraft (Java and Bedrock), V Rising, 7 Days to Die, Project Zomboid, Terraria, ARK: Survival Evolved, ARK: Survival Ascended, Sons of the Forest, Conan Exiles, Core Keeper and Rust. Any other server can be added as **Other / Custom**: you choose the executable, and Hostbay captures its console and finds config-looking files in its folder.

Game definitions live in `src/Hostbay/Core/GameCatalog.cs`. Adding a game means adding one entry there.

## Build

Requires the .NET 10 SDK. Everything else, including the WiX toolset for the installer, comes from NuGet automatically.

```powershell
.\build.ps1
```

This produces:

- `publish\Hostbay.exe`: the app as a single self-contained file that doesn't need a separate .NET install. You can run it as-is.
- `dist\Hostbay-Setup.msi`: the installer to give to friends.

The window uses the Microsoft Edge WebView2 Runtime, which is built into Windows 11.

## Installer

`Hostbay-Setup.msi` installs Hostbay for the current Windows user, into `%LOCALAPPDATA%\Programs\Hostbay`, so no admin prompt appears. A "Choose what to install" page has a tickbox for Hostbay and one for PortHop (installed to `%LOCALAPPDATA%ProgramsPortHop`), so you can install either on its own or both. On an upgrade, the ticks start out matching what's already installed. Hostbay gets Start menu and desktop shortcuts, PortHop a Start menu shortcut, and the last page offers to launch whichever app you installed. Uninstall it from Settings > Apps. Uninstalling keeps your server list and settings in `%APPDATA%\Hostbay`.

To release a new version, bump `<Version>` in `src\Hostbay\Hostbay.csproj` and run `.\build.ps1`. The installer takes its version from the exe and upgrades older installs in place.

Silent install, for example for scripting: `msiexec /i Hostbay-Setup.msi /qn` (installs both; add `ADDLOCAL=Hostbay` or `ADDLOCAL=PortHop` for just one, and `INSTALLDESKTOPSHORTCUT=0` to skip the desktop shortcut).

The installer's source is in `installer/`. `Package.wxs` defines the package. The artwork is generated by `installer\Assets\make-art.ps1`.

## Where things are stored

- Settings and the server list: `%APPDATA%\Hostbay\settings.json`
- Config backups: `%APPDATA%\Hostbay\backups\`
- Set the `HOSTBAY_DATA_DIR` environment variable to use a different folder, for example when testing.

## How it's built

- **`Core/`**: the game catalog, the Steam library scanner (VDF/ACF parser), config discovery, process control (Ctrl+C delivery, process-tree stats and kill), the RCON and Telnet clients, and log tailing.
- **`Web/`**: an embedded Kestrel server providing the REST API and live log/status streams (Server-Sent Events). It also handles sign-in: the desktop window uses a per-launch token, and other browsers sign in with an account (PBKDF2-hashed passwords, lockout after repeated failures). Sign-in uses a session cookie, permissions are enforced per request on the server, and changes require an `X-Hostbay` header so other websites can't make them.
- **`Desktop/`**: the WinForms window hosting WebView2, the tray icon, single-instance handling, and a graceful stop of all servers on exit.
- **`wwwroot/`**: the UI, in plain HTML, CSS and JavaScript embedded in the exe, with no build step.
- **`tests/FakeServer`**: a stand-in game server (console output, a log file, console input, Ctrl+C handling, an RCON server) used to exercise Hostbay without installing real games.
- **`tests/FakeDiscord`**: a stand-in for Discord's REST API and gateway, used to test the bot without a real token (`HOSTBAY_DISCORD_API=http://127.0.0.1:9555/api/v10`).

## PortHop

PortHop (`..\PortHop`) is a separate app for forwarding game ports. Hostbay finds a running copy through `%LOCALAPPDATA%\PortHop\instance.json` and uses its local API (`/api/hello`, `/api/forwarded`, `/api/forward`, `/api/remove`). Forwards Hostbay makes are named `Hostbay: <server name>` in PortHop. `build.ps1` builds PortHop from source when Node is available (on PATH, or `$env:HOSTBAY_NODE`) and bundles it into the installer, which puts it in `%LOCALAPPDATA%\Programs\PortHop`.

To go back to the version before PortHop: the git tag `v0.2-pre-porthop` (branch `hostbay-v1`), or the saved installer `releases\Hostbay-0.2.4.7-Setup.msi`.

## Not in this version

- Installing or updating servers through SteamCMD.
- Satisfactory's HTTPS admin API and Rust's WebRCON, so those games use console input or have no commands.
- Scheduled restarts and backups of world saves.
