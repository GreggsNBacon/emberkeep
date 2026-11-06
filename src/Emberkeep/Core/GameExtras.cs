using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Emberkeep.Core;

/// <summary>
/// Per-game details for backups, schedules and player lists, kept apart from the main catalog:
/// where the game keeps its world/save files, the command that writes the world to disk, how to message players,
/// and the log lines that show players joining and leaving.
/// </summary>
public sealed record GameExtra(
    string[] SaveFolders,
    string[]? SaveCommands = null,
    string? Broadcast = null,         // e.g. "say {0}"
    bool BroadcastNoSpaces = false,   // Palworld: Broadcast can't contain spaces
    string? JoinPattern = null,       // regex, group 1 = player name
    string? LeavePattern = null,
    string? PlayersQuery = null);     // RCON/Telnet command answered with the player list (read quietly)

public static class GameExtras
{
    private const string LocalLow = "%USERPROFILE%\\AppData\\LocalLow";

    private static readonly Dictionary<string, GameExtra> Map = new()
    {
        ["palworld"] = new(["Pal\\Saved\\SaveGames"], ["Save"], "Broadcast {0}", BroadcastNoSpaces: true, PlayersQuery: "ShowPlayers"),
        ["minecraft-java"] = new(["world", "world_nether", "world_the_end"], ["save-all"], "say {0}",
            JoinPattern: @"\]: ([A-Za-z0-9_]{2,16}) joined the game", LeavePattern: @"\]: ([A-Za-z0-9_]{2,16}) left the game"),
        ["minecraft-bedrock"] = new(["worlds"], null, "say {0}",
            JoinPattern: @"Player connected: ([^,]+),", LeavePattern: @"Player disconnected: ([^,]+),"),
        ["valheim"] = new([LocalLow + "\\IronGate\\Valheim\\worlds_local"]),
        ["enshrouded"] = new(["savegame"]),
        ["satisfactory"] = new(["%LOCALAPPDATA%\\FactoryGame\\Saved\\SaveGames"]),
        ["vrising"] = new([LocalLow + "\\Stunlock Studios\\VRisingServer\\Saves"], null, "announce {0}"),
        ["7dtd"] = new(["%APPDATA%\\7DaysToDie\\Saves"], ["saveworld"], "say \"{0}\""),
        ["zomboid"] = new(["%USERPROFILE%\\Zomboid\\Saves", "%USERPROFILE%\\Zomboid\\db"], ["save"], "servermsg \"{0}\""),
        ["terraria"] = new(["%USERPROFILE%\\Documents\\My Games\\Terraria\\Worlds"], ["save"], "say {0}",
            JoinPattern: @"^(.+) has joined\.$", LeavePattern: @"^(.+) has left\.$"),
        ["ark-se"] = new(["ShooterGame\\Saved\\SavedArks"], ["SaveWorld"], "Broadcast {0}"),
        ["ark-sa"] = new(["ShooterGame\\Saved\\SavedArks"], ["SaveWorld"], "ServerChat {0}"),
        ["sotf"] = new([LocalLow + "\\Endnight\\SonsOfTheForestDS"]),
        ["conan"] = new(["ConanSandbox\\Saved"]),
        ["corekeeper"] = new([LocalLow + "\\Pugstorm\\Core Keeper\\DedicatedServer"]),
        ["rust"] = new(["server"], ["server.save"], "say {0}"),
    };

    private static readonly GameExtra None = new([]);

    public static GameExtra For(string gameId) => Map.TryGetValue(gameId, out var e) ? e : None;

    /// <summary>The command that shows <paramref name="message"/> to players, or null when the game has none.</summary>
    public static string? BroadcastCommand(string gameId, string message)
    {
        var e = For(gameId);
        if (e.Broadcast == null) return null;
        if (e.BroadcastNoSpaces) message = message.Replace(' ', '_');
        return string.Format(e.Broadcast, message.Replace("\"", "'"));
    }
}

/// <summary>Who is on a server: from join/leave lines in its log, or a quiet RCON query (Palworld).</summary>
public sealed class PlayerTracker
{
    private readonly object _lock = new();
    private readonly SortedSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Regex? _join, _leave;

    /// <summary>False for games Emberkeep can't count players for.</summary>
    public bool Supported { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    public void Configure(GameExtra e)
    {
        lock (_lock)
        {
            _join = e.JoinPattern != null ? new Regex(e.JoinPattern, RegexOptions.Compiled) : null;
            _leave = e.LeavePattern != null ? new Regex(e.LeavePattern, RegexOptions.Compiled) : null;
            Supported = _join != null || e.PlayersQuery != null;
        }
    }

    /// <summary>Raised when a query (not a log line) refreshed the list.</summary>
    public event Action? Updated;

    public string[] Names { get { lock (_lock) return [.. _names]; } }
    public int Count { get { lock (_lock) return _names.Count; } }

    public void Reset() { lock (_lock) { _names.Clear(); UpdatedAt = null; } }

    /// <summary>Reads a server log line; returns true when the player list changed.</summary>
    public bool OnLine(string text)
    {
        lock (_lock)
        {
            if (_join?.Match(text) is { Success: true } j) { UpdatedAt = DateTime.UtcNow; return _names.Add(j.Groups[1].Value.Trim()); }
            if (_leave?.Match(text) is { Success: true } l) { UpdatedAt = DateTime.UtcNow; return _names.Remove(l.Groups[1].Value.Trim()); }
            return false;
        }
    }

    /// <summary>Palworld's ShowPlayers: a CSV header "name,playeruid,steamid", then one line per player.</summary>
    public void SetFromPalworld(string reply)
    {
        lock (_lock)
        {
            _names.Clear();
            foreach (var line in reply.Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0 || line.StartsWith("name,", StringComparison.OrdinalIgnoreCase)) continue;
                var name = line.Split(',')[0].Trim();
                if (name.Length > 0) _names.Add(name);
            }
            UpdatedAt = DateTime.UtcNow;
        }
        try { Updated?.Invoke(); } catch { }
    }
}
