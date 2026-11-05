# Hostbay

A Windows app for running game servers for you and your friends. Run `Hostbay.exe` on the PC that hosts the servers. You can then manage them from that PC or from any browser on your network.

## What it does

- **Finds your servers.** It scans every Steam library, including dedicated servers installed from Steam's Tools library. You can also add any folder, such as a Minecraft server, and Hostbay recognises the game from the files inside.
- **Edits configs.** It finds each game's config files, using known locations plus a scan of the server folder. You can edit them in a form view (INI, `.properties`, JSON, 7 Days to Die XML, and Palworld's single-line `OptionSettings`) or as plain text. Every save keeps a backup (the last 25 per file), and comments, line endings and encoding are preserved.
- **Controls servers.** Start, Stop, Restart and Force-close. Stop is graceful: it sends the game's own shutdown commands, or presses Ctrl+C. It force-closes only if the server doesn't exit within the timeout. You can also turn on auto-restart after a crash.
- **Sends commands.** Commands go through console input (Minecraft, Terraria, Zomboid, Rust), RCON (Palworld, ARK, V Rising, Conan) or Telnet (7 Days to Die). RCON passwords and ports are read from the game's own config.
- **Shows live logs.** The server's console output and its log files stream in real time, with filtering and timestamps.
- **Offers remote access.** Set a password, turn on remote access, then open `http://<host-ip>:8765` from another PC or your phone.
- **Picks up running servers.** If a server was already running when Hostbay started, Hostbay detects it and can stop it and show its log files.

## Supported games

Palworld, Enshrouded, Valheim, Satisfactory, Minecraft (Java and Bedrock), V Rising, 7 Days to Die, Project Zomboid, Terraria, ARK: Survival Evolved, ARK: Survival Ascended, Sons of the Forest, Conan Exiles, Core Keeper and Rust. Any other server can be added as **Other / Custom**: you choose the executable, and Hostbay captures its console and finds config-looking files in its folder.

Game definitions live in `src/Hostbay/Core/GameCatalog.cs`. Adding a game means adding one entry there.

## Build

Requires the .NET 10 SDK.

```powershell
dotnet publish src\Hostbay\Hostbay.csproj -c Release -o publish
```

This produces `publish\Hostbay.exe`, a single self-contained file that doesn't need a separate .NET install. The window uses the Microsoft Edge WebView2 Runtime, which is built into Windows 11.

## Where things are stored

- Settings and the server list: `%APPDATA%\Hostbay\settings.json`
- Config backups: `%APPDATA%\Hostbay\backups\`
- Set the `HOSTBAY_DATA_DIR` environment variable to use a different folder, for example when testing.

## How it's built

- **`Core/`**: the game catalog, the Steam library scanner (VDF/ACF parser), config discovery, process control (Ctrl+C delivery, process-tree stats and kill), the RCON and Telnet clients, and log tailing.
- **`Web/`**: an embedded Kestrel server providing the REST API and live log/status streams (Server-Sent Events). It also handles sign-in: the desktop window uses a per-launch token, and other browsers use the password. Sign-in uses a session cookie, and changes require an `X-Hostbay` header so other websites can't make them.
- **`Desktop/`**: the WinForms window hosting WebView2, the tray icon, single-instance handling, and a graceful stop of all servers on exit.
- **`wwwroot/`**: the UI, in plain HTML, CSS and JavaScript embedded in the exe, with no build step.
- **`tests/FakeServer`**: a stand-in game server (console output, a log file, console input, Ctrl+C handling, an RCON server) used to exercise Hostbay without installing real games.

## Not in this version

- Installing or updating servers through SteamCMD.
- Satisfactory's HTTPS admin API and Rust's WebRCON, so those games use console input or have no commands.
- Scheduled restarts and backups of world saves.
