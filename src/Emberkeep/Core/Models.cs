using System;
using System.Collections.Generic;

namespace Emberkeep.Core;

/// <summary>How console commands reach a running server.</summary>
public enum CommandChannel { None, Stdin, Rcon, Telnet }

/// <summary>How Emberkeep asks a server to shut down before falling back to killing it.</summary>
public enum StopMethod { CtrlC, Commands, Kill }

public enum ServerState { Stopped, Starting, Running, Stopping }

public sealed class RemoteConsoleSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Password { get; set; } = "";
}

/// <summary>A server the user has added to Emberkeep. Persisted to settings.json.</summary>
public sealed class ServerInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "New server";
    public string GameId { get; set; } = "generic";
    public string InstallDir { get; set; } = "";
    /// <summary>Executable path. Relative paths resolve against <see cref="InstallDir"/>; bare names (java) resolve via PATH.</summary>
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Optional; defaults to the install dir.</summary>
    public string WorkingDirectory { get; set; } = "";
    /// <summary>The game's own setup program (e.g. a "Server Launcher"), relative to the install dir. Used by "Learn from launcher".</summary>
    public string Launcher { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new();
    public bool AutoRestart { get; set; }
    /// <summary>Start this server when Emberkeep starts (e.g. after Windows starts, with Start with Windows on).</summary>
    public bool StartWithEmberkeep { get; set; }
    public CommandChannel CommandChannel { get; set; } = CommandChannel.None;
    public StopMethod StopMethod { get; set; } = StopMethod.CtrlC;
    /// <summary>Commands sent (in order) when <see cref="StopMethod"/> is Commands, e.g. "save-all", "stop".</summary>
    public List<string> StopCommands { get; set; } = new();
    public int StopTimeoutSeconds { get; set; } = 60;
    public RemoteConsoleSettings Rcon { get; set; } = new();
    /// <summary>When true, RCON/Telnet port + password are re-read from the game's config file before each use.</summary>
    public bool RconFromConfig { get; set; } = true;
    /// <summary>Extra log files to tail (relative to install dir or absolute).</summary>
    public List<string> ExtraLogFiles { get; set; } = new();
    public int? SteamAppId { get; set; }
    /// <summary>Set when Emberkeep installed the server with SteamCMD: the app it downloads, so Update server can fetch new versions.</summary>
    public int? SteamCmdAppId { get; set; }
    /// <summary>Port players connect to. Null means detect it from the launch arguments or game config.</summary>
    public int? JoinPort { get; set; }
    /// <summary>Ports to open on the router, e.g. "UDP 2456-2458, TCP 8888". Empty means use the game's list.</summary>
    public string? ForwardPorts { get; set; }
    /// <summary>Open the router ports when the server starts and close them when it stops.</summary>
    public bool ForwardWhileRunning { get; set; }
    /// <summary>Steam app ID whose artwork to show. Null means use the game's own.</summary>
    public int? ArtAppId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public bool RemoteAccess { get; set; }
    /// <summary>Also open Emberkeep's port on the router (UPnP) so it can be reached from the internet.</summary>
    public bool InternetAccess { get; set; }
    /// <summary>Whether the remote password is long enough (10+ characters) to allow internet access.</summary>
    public bool LongPassword { get; set; }
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool MinimizeToTray { get; set; } = true;
    public List<UserAccount> Users { get; set; } = new();
    public List<UserGroup> Groups { get; set; } = new();
    public List<ServerInstance> Servers { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    /// <summary>Set once Steam's "Dedicated Server" suffixes have been taken off existing server names.</summary>
    public bool NamesTidied { get; set; }
    /// <summary>Look for a newer Emberkeep on GitHub (on start and every 12 hours).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Restart this PC every day at <see cref="DailyRestartTime"/>, stopping the servers first.</summary>
    public bool DailyRestart { get; set; }
    /// <summary>Local time of day, "HH:mm".</summary>
    public string DailyRestartTime { get; set; } = "05:00";
    /// <summary>steamcmd.exe to use instead of Emberkeep's own copy. Empty: Emberkeep sets SteamCMD up itself.</summary>
    public string SteamCmdPath { get; set; } = "";
    /// <summary>Folder new servers are installed into (each in its own subfolder). Empty: %USERPROFILE%Game Servers.</summary>
    public string InstallRoot { get; set; } = "";
}

public sealed record LogLine(long Seq, DateTime Time, string Source, string Text);
