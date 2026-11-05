using System;
using System.Collections.Generic;

namespace Hostbay.Core;

/// <summary>How console commands reach a running server.</summary>
public enum CommandChannel { None, Stdin, Rcon, Telnet }

/// <summary>How Hostbay asks a server to shut down before falling back to killing it.</summary>
public enum StopMethod { CtrlC, Commands, Kill }

public enum ServerState { Stopped, Starting, Running, Stopping }

public sealed class RemoteConsoleSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string Password { get; set; } = "";
}

/// <summary>A server the user has added to Hostbay. Persisted to settings.json.</summary>
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
    public Dictionary<string, string> Environment { get; set; } = new();
    public bool AutoRestart { get; set; }
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
    /// <summary>Port players connect to. Null means detect it from the launch arguments or game config.</summary>
    public int? JoinPort { get; set; }
    /// <summary>Steam app ID whose artwork to show. Null means use the game's own.</summary>
    public int? ArtAppId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public bool RemoteAccess { get; set; }
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public bool MinimizeToTray { get; set; } = true;
    public List<ServerInstance> Servers { get; set; } = new();
}

public sealed record LogLine(long Seq, DateTime Time, string Source, string Text);
