using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Emberkeep.Core;

/// <summary>A template describing how a type of game server is laid out and controlled.</summary>
public sealed class GameDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Two-letter badge shown in the UI.</summary>
    public required string Badge { get; init; }
    public required string Color { get; init; }
    public int[] SteamAppIds { get; init; } = [];
    /// <summary>Whether SteamCMD can download the server without a Steam login (most dedicated servers). Emberkeep only installs those.</summary>
    public bool SteamCmdAnonymous { get; init; } = true;
    /// <summary>
    /// Ports to open on the router for this game, e.g. "UDP 2456-2458, TCP 8888" (same lists Watchfire uses).
    /// Entries containing the default join port move with it when a server runs on a different port.
    /// </summary>
    public string? ForwardPorts { get; init; }
    /// <summary>The game's own Steam app ID, used for artwork (server tools have no store art).</summary>
    public int? ArtAppId { get; init; }
    /// <summary>Candidate executables relative to the install dir; the first that exists is used.</summary>
    public string[] Executables { get; init; } = [];
    /// <summary>The game's own setup program, if it has one, relative to the install dir.</summary>
    public string? Launcher { get; init; }
    public string DefaultArguments { get; init; } = "";
    public Dictionary<string, string> Environment { get; init; } = new();
    /// <summary>Relative paths; if any exists in a folder, the folder is treated as this game.</summary>
    public string[] DetectFiles { get; init; } = [];
    /// <summary>Config file patterns. Relative to the install dir, or absolute with %ENV% vars. '*' allowed per segment.</summary>
    public string[] ConfigFiles { get; init; } = [];
    public string[] LogFiles { get; init; } = [];
    public CommandChannel Commands { get; init; } = CommandChannel.None;
    public StopMethod Stop { get; init; } = StopMethod.CtrlC;
    public string[] StopCommands { get; init; } = [];
    public int DefaultRconPort { get; init; }
    public string[] QuickCommands { get; init; } = [];
    public string? Notes { get; init; }
    /// <summary>Reads RCON/Telnet connection details out of the game's own config, if it has them.</summary>
    public Func<string, RemoteConsoleSettings?>? ReadRemoteConsole { get; init; }
    /// <summary>(source, destination) relative to install dir: a defaults file that can seed a missing config.</summary>
    public (string Source, string Destination)? DefaultConfigTemplate { get; init; }
}

public static class GameCatalog
{
    private const string LocalLow = "%USERPROFILE%\\AppData\\LocalLow";

    public static readonly IReadOnlyList<GameDefinition> All = new List<GameDefinition>
    {
        new()
        {
            Id = "palworld", Name = "Palworld", Badge = "PW", Color = "#3fb6ff",
            ForwardPorts = "UDP 8211",
            ArtAppId = 1623730,
            SteamAppIds = [2394010],
            Executables = ["PalServer.exe"],
            DefaultArguments = "-port=8211 -players=32 -useperfthreads -NoAsyncLoadingThread -UseMultithreadForDS",
            DetectFiles = ["PalServer.exe"],
            ConfigFiles =
            [
                "Pal\\Saved\\Config\\WindowsServer\\PalWorldSettings.ini",
                "Pal\\Saved\\Config\\WindowsServer\\Engine.ini",
                "Pal\\Saved\\Config\\WindowsServer\\GameUserSettings.ini",
                "DefaultPalWorldSettings.ini",
            ],
            LogFiles = ["Pal\\Saved\\Logs\\Pal.log"],
            Commands = CommandChannel.Rcon, Stop = StopMethod.Commands,
            StopCommands = ["Save", "DoExit"], DefaultRconPort = 25575,
            QuickCommands = ["ShowPlayers", "Save", "Info", "Broadcast Hello_everyone"],
            DefaultConfigTemplate = ("DefaultPalWorldSettings.ini", "Pal\\Saved\\Config\\WindowsServer\\PalWorldSettings.ini"),
            Notes = "Commands use RCON: set RCONEnabled=True and an AdminPassword in PalWorldSettings.ini. Broadcast messages can't contain spaces, so use underscores.",
            ReadRemoteConsole = dir =>
            {
                var opts = ConfigReaders.PalworldOptions(Path.Combine(dir, "Pal\\Saved\\Config\\WindowsServer\\PalWorldSettings.ini"));
                if (opts == null) return null;
                if (opts.TryGetValue("RCONEnabled", out var en) && !en.Equals("True", StringComparison.OrdinalIgnoreCase)) return null;
                return new RemoteConsoleSettings
                {
                    Port = int.TryParse(opts.GetValueOrDefault("RCONPort"), out var p) ? p : 25575,
                    Password = opts.GetValueOrDefault("AdminPassword") ?? "",
                };
            },
        },
        new()
        {
            Id = "enshrouded", Name = "Enshrouded", Badge = "EN", Color = "#c792ea",
            ForwardPorts = "UDP 15636-15637",
            ArtAppId = 1203620,
            SteamAppIds = [2278520],
            Executables = ["enshrouded_server.exe"],
            DetectFiles = ["enshrouded_server.exe"],
            ConfigFiles = ["enshrouded_server.json"],
            LogFiles = ["logs\\enshrouded_server.log"],
            Stop = StopMethod.CtrlC,
            Notes = "Enshrouded has no remote console. Emberkeep stops it with Ctrl+C so the world saves cleanly. The config file is created the first time the server runs.",
        },
        new()
        {
            Id = "valheim", Name = "Valheim", Badge = "VH", Color = "#f2b84b",
            ForwardPorts = "UDP 2456-2458",
            ArtAppId = 892970,
            SteamAppIds = [896660],
            Executables = ["valheim_server.exe"],
            DefaultArguments = "-nographics -batchmode -name \"My Valheim Server\" -port 2456 -world \"Dedicated\" -password \"changeme\" -crossplay",
            Environment = new() { ["SteamAppId"] = "892970" },
            DetectFiles = ["valheim_server.exe"],
            ConfigFiles =
            [
                LocalLow + "\\IronGate\\Valheim\\adminlist.txt",
                LocalLow + "\\IronGate\\Valheim\\bannedlist.txt",
                LocalLow + "\\IronGate\\Valheim\\permittedlist.txt",
                "BepInEx\\config\\*.cfg",
            ],
            Stop = StopMethod.CtrlC,
            Notes = "Valheim's server name, world and password are launch arguments, so edit them under Settings → Launch. The password must be at least 5 characters and can't appear in the server name.",
        },
        new()
        {
            Id = "satisfactory", Name = "Satisfactory", Badge = "SF", Color = "#f58b3c",
            ForwardPorts = "TCP/UDP 7777, TCP 8888",
            ArtAppId = 526870,
            SteamAppIds = [1690800],
            Executables = ["FactoryServer.exe"],
            DefaultArguments = "-log -unattended",
            DetectFiles = ["FactoryServer.exe"],
            ConfigFiles =
            [
                "FactoryGame\\Saved\\Config\\WindowsServer\\Game.ini",
                "FactoryGame\\Saved\\Config\\WindowsServer\\Engine.ini",
                "FactoryGame\\Saved\\Config\\WindowsServer\\GameUserSettings.ini",
                "FactoryGame\\Saved\\Config\\WindowsServer\\ServerSettings.ini",
            ],
            LogFiles = ["FactoryGame\\Saved\\Logs\\FactoryGame.log"],
            Stop = StopMethod.CtrlC,
            Notes = "Satisfactory is administered from the in-game Server Manager. Its config files appear here after the first launch.",
        },
        new()
        {
            Id = "minecraft-java", Name = "Minecraft (Java)", Badge = "MC", Color = "#62c462",
            ForwardPorts = "TCP 25565",
            Executables = ["run.bat"],
            DefaultArguments = "-Xms1G -Xmx4G -jar server.jar nogui",
            DetectFiles = ["server.properties", "eula.txt", "user_jvm_args.txt"],
            ConfigFiles =
            [
                "server.properties", "eula.txt", "ops.json", "whitelist.json", "banned-players.json", "banned-ips.json",
                "user_jvm_args.txt", "bukkit.yml", "spigot.yml", "paper.yml", "purpur.yml",
                "config\\paper-global.yml", "config\\paper-world-defaults.yml",
            ],
            Commands = CommandChannel.Stdin, Stop = StopMethod.Commands, StopCommands = ["stop"],
            QuickCommands = ["list", "save-all", "say Hello everyone", "weather clear", "time set day", "whitelist list"],
            Notes = "Requires Java. Set eula=true in eula.txt before the first start.",
        },
        new()
        {
            Id = "minecraft-bedrock", Name = "Minecraft (Bedrock)", Badge = "MB", Color = "#4fae5b",
            ForwardPorts = "UDP 19132-19133",
            Executables = ["bedrock_server.exe"],
            DetectFiles = ["bedrock_server.exe"],
            ConfigFiles = ["server.properties", "allowlist.json", "permissions.json"],
            Commands = CommandChannel.Stdin, Stop = StopMethod.Commands, StopCommands = ["stop"],
            QuickCommands = ["list", "say Hello everyone", "time set day", "allowlist list"],
        },
        new()
        {
            Id = "vrising", Name = "V Rising", Badge = "VR", Color = "#e0424b",
            ForwardPorts = "UDP 9876-9877",
            ArtAppId = 1604030,
            SteamAppIds = [1829350],
            Executables = ["VRisingServer.exe"],
            DefaultArguments = "-persistentDataPath .\\save-data -serverName \"My V Rising Server\" -saveName world1 -logFile .\\logs\\VRisingServer.log",
            DetectFiles = ["VRisingServer.exe"],
            ConfigFiles =
            [
                "save-data\\Settings\\ServerHostSettings.json", "save-data\\Settings\\ServerGameSettings.json",
                "VRisingServer_Data\\StreamingAssets\\Settings\\ServerHostSettings.json",
                "VRisingServer_Data\\StreamingAssets\\Settings\\ServerGameSettings.json",
                "save-data\\Settings\\adminlist.txt", "save-data\\Settings\\banlist.txt",
            ],
            LogFiles = ["logs\\VRisingServer.log"],
            Commands = CommandChannel.Rcon, Stop = StopMethod.CtrlC, DefaultRconPort = 25575,
            QuickCommands = ["announce Hello everyone", "announcerestart 5"],
            Notes = "Turn on RCON in ServerHostSettings.json (Rcon.Enabled, Port, Password) to send commands. Settings in save-data override the defaults in VRisingServer_Data.",
            ReadRemoteConsole = dir =>
            {
                foreach (var rel in new[] { "save-data\\Settings\\ServerHostSettings.json", "VRisingServer_Data\\StreamingAssets\\Settings\\ServerHostSettings.json" })
                {
                    var r = ConfigReaders.JsonRcon(Path.Combine(dir, rel), "Rcon");
                    if (r != null) return r;
                }
                return null;
            },
        },
        new()
        {
            Id = "7dtd", Name = "7 Days to Die", Badge = "7D", Color = "#b5523b",
            ForwardPorts = "TCP 26900, UDP 26900-26903",
            ArtAppId = 251570,
            SteamAppIds = [294420],
            Executables = ["7DaysToDieServer.exe"],
            DefaultArguments = "-quit -batchmode -nographics -configfile=serverconfig.xml -dedicated -logfile output_log__emberkeep.txt",
            DetectFiles = ["7DaysToDieServer.exe"],
            ConfigFiles = ["serverconfig.xml", "%APPDATA%\\7DaysToDie\\Saves\\serveradmin.xml"],
            LogFiles = ["output_log__emberkeep.txt", "output_log__hostbay.txt"], // servers added before the rename log to the old name
            Commands = CommandChannel.Telnet, Stop = StopMethod.Commands, StopCommands = ["saveworld", "shutdown"], DefaultRconPort = 8081,
            QuickCommands = ["listplayers", "saveworld", "say \"Hello everyone\"", "gettime"],
            Notes = "Commands go over Telnet. Set TelnetEnabled, TelnetPort and TelnetPassword in serverconfig.xml.",
            ReadRemoteConsole = dir =>
            {
                var p = ConfigReaders.XmlProperties(Path.Combine(dir, "serverconfig.xml"));
                if (p == null) return null;
                if (p.TryGetValue("TelnetEnabled", out var en) && !en.Equals("true", StringComparison.OrdinalIgnoreCase)) return null;
                return new RemoteConsoleSettings
                {
                    Port = int.TryParse(p.GetValueOrDefault("TelnetPort"), out var port) ? port : 8081,
                    Password = p.GetValueOrDefault("TelnetPassword") ?? "",
                };
            },
        },
        new()
        {
            Id = "zomboid", Name = "Project Zomboid", Badge = "PZ", Color = "#9bbf3a",
            ForwardPorts = "UDP 16261-16262",
            ArtAppId = 108600,
            SteamAppIds = [380870],
            Executables = ["StartServer64_nosteam.bat", "StartServer64.bat"],
            DetectFiles = ["StartServer64.bat"],
            ConfigFiles =
            [
                "%USERPROFILE%\\Zomboid\\Server\\*.ini",
                "%USERPROFILE%\\Zomboid\\Server\\*_SandboxVars.lua",
                "%USERPROFILE%\\Zomboid\\Server\\*_spawnregions.lua",
                "ProjectZomboid64.json",
            ],
            LogFiles = [],
            Commands = CommandChannel.Stdin, Stop = StopMethod.Commands, StopCommands = ["save", "quit"],
            QuickCommands = ["players", "save", "servermsg \"Hello everyone\"", "showoptions"],
            Notes = "The first start asks for an admin password in the console. Type it into the command box below. Server configs live in %USERPROFILE%\\Zomboid\\Server.",
        },
        new()
        {
            Id = "terraria", Name = "Terraria", Badge = "TR", Color = "#5ea84a",
            ForwardPorts = "TCP 7777",
            ArtAppId = 105600,
            SteamAppIds = [105600],
            SteamCmdAnonymous = false, // the server comes with the game itself
            Executables = ["TerrariaServer.exe"],
            DefaultArguments = "-config serverconfig.txt",
            DetectFiles = ["TerrariaServer.exe"],
            ConfigFiles = ["serverconfig.txt", "tModLoader\\serverconfig.txt"],
            Commands = CommandChannel.Stdin, Stop = StopMethod.Commands, StopCommands = ["exit"],
            QuickCommands = ["playing", "save", "say Hello everyone", "time", "dawn"],
            Notes = "Point serverconfig.txt at a world file (world=...), or the server waits at its interactive menu. You can also answer the menu from the command box.",
        },
        new()
        {
            Id = "ark-se", Name = "ARK: Survival Evolved", Badge = "AE", Color = "#4fb0a8",
            ForwardPorts = "UDP 7777-7778, UDP 27015",
            ArtAppId = 346110,
            SteamAppIds = [376030],
            Executables = ["ShooterGame\\Binaries\\Win64\\ShooterGameServer.exe"],
            DefaultArguments = "TheIsland?listen?SessionName=MyArkServer?RCONEnabled=True?RCONPort=27020 -server -log",
            DetectFiles = ["ShooterGame\\Binaries\\Win64\\ShooterGameServer.exe"],
            ConfigFiles = ["ShooterGame\\Saved\\Config\\WindowsServer\\GameUserSettings.ini", "ShooterGame\\Saved\\Config\\WindowsServer\\Game.ini"],
            LogFiles = ["ShooterGame\\Saved\\Logs\\ShooterGame.log"],
            Commands = CommandChannel.Rcon, Stop = StopMethod.Commands, StopCommands = ["SaveWorld", "DoExit"], DefaultRconPort = 27020,
            QuickCommands = ["ListPlayers", "SaveWorld", "Broadcast Hello everyone", "GetChat"],
            ReadRemoteConsole = dir => ConfigReaders.IniRcon(Path.Combine(dir, "ShooterGame\\Saved\\Config\\WindowsServer\\GameUserSettings.ini"), "RCONPort", "ServerAdminPassword", 27020),
        },
        new()
        {
            Id = "ark-sa", Name = "ARK: Survival Ascended", Badge = "AA", Color = "#38a3c9",
            ForwardPorts = "UDP 7777, UDP 27015",
            ArtAppId = 2399830,
            SteamAppIds = [2430930],
            Executables = ["ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe"],
            DefaultArguments = "TheIsland_WP?listen?SessionName=MyArkServer?RCONEnabled=True?RCONPort=27020 -log",
            DetectFiles = ["ShooterGame\\Binaries\\Win64\\ArkAscendedServer.exe"],
            ConfigFiles = ["ShooterGame\\Saved\\Config\\WindowsServer\\GameUserSettings.ini", "ShooterGame\\Saved\\Config\\WindowsServer\\Game.ini"],
            LogFiles = ["ShooterGame\\Saved\\Logs\\ShooterGame.log"],
            Commands = CommandChannel.Rcon, Stop = StopMethod.Commands, StopCommands = ["SaveWorld", "DoExit"], DefaultRconPort = 27020,
            QuickCommands = ["ListPlayers", "SaveWorld", "ServerChat Hello everyone"],
            ReadRemoteConsole = dir => ConfigReaders.IniRcon(Path.Combine(dir, "ShooterGame\\Saved\\Config\\WindowsServer\\GameUserSettings.ini"), "RCONPort", "ServerAdminPassword", 27020),
        },
        new()
        {
            Id = "sotf", Name = "Sons of the Forest", Badge = "SO", Color = "#7fa36b",
            ForwardPorts = "UDP 8766, UDP 27016, UDP 9700",
            ArtAppId = 1326470,
            SteamAppIds = [2465200],
            Executables = ["SonsOfTheForestDS.exe"],
            DetectFiles = ["SonsOfTheForestDS.exe"],
            ConfigFiles = [LocalLow + "\\Endnight\\SonsOfTheForestDS\\dedicatedserver.cfg", LocalLow + "\\Endnight\\SonsOfTheForestDS\\ownerswhitelist.txt"],
            Stop = StopMethod.CtrlC,
        },
        new()
        {
            Id = "conan", Name = "Conan Exiles", Badge = "CE", Color = "#d39b4a",
            ForwardPorts = "UDP 7777-7778, UDP 27015",
            ArtAppId = 440900,
            SteamAppIds = [443030],
            Executables = ["ConanSandboxServer.exe"],
            DefaultArguments = "-log",
            DetectFiles = ["ConanSandboxServer.exe"],
            ConfigFiles =
            [
                "ConanSandbox\\Saved\\Config\\WindowsServer\\ServerSettings.ini",
                "ConanSandbox\\Saved\\Config\\WindowsServer\\Engine.ini",
                "ConanSandbox\\Saved\\Config\\WindowsServer\\Game.ini",
            ],
            LogFiles = ["ConanSandbox\\Saved\\Logs\\ConanSandbox.log"],
            Commands = CommandChannel.Rcon, Stop = StopMethod.CtrlC, DefaultRconPort = 25575,
            QuickCommands = ["listplayers"],
            ReadRemoteConsole = dir => ConfigReaders.IniRcon(Path.Combine(dir, "ConanSandbox\\Saved\\Config\\WindowsServer\\Game.ini"), "RconPort", "RconPassword", 25575),
        },
        new()
        {
            Id = "corekeeper", Name = "Core Keeper", Badge = "CK", Color = "#e7c45a",
            ArtAppId = 1621690,
            SteamAppIds = [1963720],
            Executables = ["CoreKeeperServer.exe"],
            DefaultArguments = "-batchmode -logfile CoreKeeperServerLog.txt",
            DetectFiles = ["CoreKeeperServer.exe"],
            ConfigFiles = [LocalLow + "\\Pugstorm\\Core Keeper\\DedicatedServer\\ServerConfig.json"],
            LogFiles = ["CoreKeeperServerLog.txt"],
            Stop = StopMethod.CtrlC,
            Notes = "The Game ID players use to join is written to GameID.txt in the install folder.",
        },
        new()
        {
            Id = "rust", Name = "Rust", Badge = "RS", Color = "#cd412b",
            ForwardPorts = "UDP 28015",
            ArtAppId = 252490,
            SteamAppIds = [258550],
            Executables = ["RustDedicated.exe"],
            DefaultArguments = "-batchmode +server.port 28015 +server.identity \"main\" +server.hostname \"My Rust Server\" +server.maxplayers 50 +server.worldsize 3000",
            DetectFiles = ["RustDedicated.exe"],
            ConfigFiles = ["server\\*\\cfg\\server.cfg", "server\\*\\cfg\\users.cfg", "server\\*\\cfg\\bans.cfg", "oxide\\config\\*.json"],
            Commands = CommandChannel.Stdin, Stop = StopMethod.Commands, StopCommands = ["server.save", "quit"],
            QuickCommands = ["status", "server.save", "say Hello everyone"],
        },
        new()
        {
            // The server takes its whole setup on the command line as compressed JSON, built by the game's Server Launcher.
            // "Learn from launcher" captures it once; Emberkeep then keeps it in EmberkeepConfig\*.json and re-encodes it on start.
            Id = "ace", Name = "Assetto Corsa EVO", Badge = "AC", Color = "#d6332f",
            ForwardPorts = "TCP/UDP 9700",
            ArtAppId = 3058630,
            SteamAppIds = [4564210],
            SteamCmdAnonymous = false, // Steam only sends it to accounts that own the game
            Executables = ["AssettoCorsaEVOServer.exe"],
            Launcher = "ServerLauncher.exe",
            DetectFiles = ["AssettoCorsaEVOServer.exe"],
            ConfigFiles = ["EmberkeepConfig\\serverconfig.json", "EmberkeepConfig\\seasondefinition.json"],
            Commands = CommandChannel.None, Stop = StopMethod.CtrlC,
            Notes = "Set the server up once in Assetto Corsa EVO's Server Launcher, then use Learn from launcher (Settings → Launch). After that Emberkeep runs it directly, and you can edit serverconfig.json and seasondefinition.json in Config files.",
        },
        new()
        {
            Id = "generic", Name = "Other / Custom", Badge = "··", Color = "#8a94a6",
            Commands = CommandChannel.Stdin, Stop = StopMethod.CtrlC,
            Notes = "A custom server. Emberkeep runs the executable you choose, captures its console output, and finds config-looking files in its folder.",
        },
    };

    public static GameDefinition Get(string id) => All.FirstOrDefault(g => g.Id == id) ?? All.First(g => g.Id == "generic");

    public static GameDefinition? BySteamAppId(int appId) => All.FirstOrDefault(g => g.SteamAppIds.Contains(appId));

    /// <summary>Returns games whose marker files exist in <paramref name="dir"/>, best match first.</summary>
    public static List<GameDefinition> DetectInFolder(string dir)
    {
        var hits = new List<(GameDefinition Game, int Score)>();
        foreach (var g in All)
        {
            int score = g.DetectFiles.Count(f => File.Exists(Path.Combine(dir, f)));
            if (score > 0) hits.Add((g, score));
        }
        // A folder with server.properties AND bedrock_server.exe is Bedrock, not Java.
        if (hits.Any(h => h.Game.Id == "minecraft-bedrock")) hits.RemoveAll(h => h.Game.Id == "minecraft-java");
        if (hits.Any(h => h.Game.Id == "minecraft-java") && !Directory.EnumerateFiles(dir, "*.jar").Any() && !File.Exists(Path.Combine(dir, "run.bat")))
            hits.RemoveAll(h => h.Game.Id == "minecraft-java" && !File.Exists(Path.Combine(dir, "server.properties")));
        return hits.OrderByDescending(h => h.Score).Select(h => h.Game).ToList();
    }

    /// <summary>Builds a ready-to-run server instance from a game template and a folder.</summary>
    public static ServerInstance CreateInstance(GameDefinition g, string installDir, string? name = null, int? steamAppId = null)
    {
        var inst = new ServerInstance
        {
            Name = name ?? g.Name,
            GameId = g.Id,
            InstallDir = installDir,
            Arguments = g.DefaultArguments,
            Environment = new Dictionary<string, string>(g.Environment),
            CommandChannel = g.Commands,
            StopMethod = g.Stop,
            StopCommands = g.StopCommands.ToList(),
            SteamAppId = steamAppId ?? g.SteamAppIds.FirstOrDefault() switch { 0 => null, var x => x },
        };
        inst.Rcon.Port = g.DefaultRconPort;

        string? exe = g.Executables.FirstOrDefault(e => File.Exists(Path.Combine(installDir, e))) ?? g.Executables.FirstOrDefault();
        if (g.Id == "minecraft-java")
        {
            var (mExe, mArgs) = MinecraftJava.DefaultLaunch(installDir);
            exe = mExe; inst.Arguments = mArgs;
        }
        var exes = Directory.Exists(installDir) ? Directory.EnumerateFiles(installDir, "*.exe").ToList() : new List<string>();
        if (string.IsNullOrEmpty(exe) && g.Id == "generic")
        {
            // Prefer console programs (servers) over windowed ones (launchers, configurators), then names with "server".
            exe = exes.OrderByDescending(p => Native.IsGuiExe(p) == false)
                      .ThenByDescending(p => Path.GetFileName(p).Contains("server", StringComparison.OrdinalIgnoreCase))
                      .Select(Path.GetFileName).FirstOrDefault();
        }
        inst.Executable = exe ?? "";
        inst.Launcher = g.Launcher is { } l && File.Exists(Path.Combine(installDir, l)) ? l : FindLauncher(exes, inst.Executable) ?? "";
        return inst;
    }

    private static readonly Regex LauncherName = new(@"launcher|config|configurator|manager|setup|admin|tool", RegexOptions.IgnoreCase);

    /// <summary>A windowed program next to the server whose name suggests it sets the server up, e.g. "ServerLauncher.exe".</summary>
    public static string? FindLauncher(IEnumerable<string> exePaths, string serverExe) =>
        exePaths.Where(p => !Path.GetFileName(p).Equals(serverExe, StringComparison.OrdinalIgnoreCase))
            .Where(p => !Regex.IsMatch(Path.GetFileName(p), @"crash|report|unins|redist|vc_?redist|dxsetup|UnityCrash|BsSndRpt|EasyAntiCheat|BEService", RegexOptions.IgnoreCase))
            .Where(p => LauncherName.IsMatch(Path.GetFileNameWithoutExtension(p)) && Native.IsGuiExe(p) == true)
            .Select(Path.GetFileName).FirstOrDefault();
}

/// <summary>Small read-only parsers used to pull RCON details out of game configs.</summary>
public static class ConfigReaders
{
    public static Dictionary<string, string>? PalworldOptions(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        var m = Regex.Match(text, @"OptionSettings=\((.*)\)\s*$", RegexOptions.Multiline);
        if (!m.Success) return null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitTopLevel(m.Groups[1].Value))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            result[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
        }
        return result;
    }

    /// <summary>Splits on commas that aren't inside quotes or parentheses.</summary>
    public static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0; bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')') depth--;
            else if (!quoted && depth == 0 && c == ',') { parts.Add(s[start..i]); start = i + 1; }
        }
        if (start < s.Length) parts.Add(s[start..]);
        return parts;
    }

    public static Dictionary<string, string> IniValues(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return d;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    public static RemoteConsoleSettings? IniRcon(string path, string portKey, string passwordKey, int defaultPort)
    {
        var v = IniValues(path);
        if (v.Count == 0) return null;
        return new RemoteConsoleSettings
        {
            Port = int.TryParse(v.GetValueOrDefault(portKey), out var p) ? p : defaultPort,
            Password = v.GetValueOrDefault(passwordKey) ?? "",
        };
    }

    public static RemoteConsoleSettings? JsonRcon(string path, string objectName)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty(objectName, out var r)) return null;
            if (r.TryGetProperty("Enabled", out var en) && en.ValueKind == JsonValueKind.False) return null;
            return new RemoteConsoleSettings
            {
                Port = r.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pi) ? pi : 25575,
                Password = r.TryGetProperty("Password", out var pw) ? pw.GetString() ?? "" : "",
            };
        }
        catch { return null; }
    }

    public static Dictionary<string, string>? XmlProperties(string path)
    {
        if (!File.Exists(path)) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(File.ReadAllText(path), "<property\\s+name=\"([^\"]+)\"\\s+value=\"([^\"]*)\""))
            d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }
}

public static class MinecraftJava
{
    /// <summary>Works out how to launch a Java server: modern Forge/NeoForge use run.bat, everything else is java -jar.</summary>
    public static (string Exe, string Args) DefaultLaunch(string dir)
    {
        if (File.Exists(Path.Combine(dir, "run.bat")) && File.Exists(Path.Combine(dir, "user_jvm_args.txt")))
            return ("run.bat", "nogui");

        string jar = "server.jar";
        if (Directory.Exists(dir))
        {
            var jars = Directory.EnumerateFiles(dir, "*.jar").Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
            string[] prefs = ["server", "paper", "purpur", "spigot", "fabric", "forge", "neoforge", "minecraft"];
            jar = jars.OrderBy(j =>
            {
                int i = Array.FindIndex(prefs, p => j.Contains(p, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? 99 : i;
            }).FirstOrDefault() ?? "server.jar";
        }
        string q = jar.Contains(' ') ? $"\"{jar}\"" : jar;
        return (FindJava() ?? "java", $"-Xms1G -Xmx4G -jar {q} nogui");
    }

    public static string? FindJava()
    {
        var home = System.Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "bin", "java.exe"))) return Path.Combine(home, "bin", "java.exe");
        foreach (var p in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(p.Trim(), "java.exe"))) return "java"; } catch { }
        }
        var pf = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" })
        {
            var root = Path.Combine(pf, vendor);
            if (!Directory.Exists(root)) continue;
            var best = Directory.EnumerateDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "bin", "java.exe")))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (best != null) return Path.Combine(best, "bin", "java.exe");
        }
        return null;
    }
}
