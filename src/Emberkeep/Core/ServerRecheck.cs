using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Emberkeep.Core;

/// <summary>
/// "Check files again": looks at a server's folder the way adding it does, for servers added before their files were all
/// there (still downloading, say). Recognises the game if it was added as Other / Custom, and finds the server program and
/// the game's launcher if they were missing. The name and everything else the user set are kept.
/// </summary>
public static class ServerRecheck
{
    /// <summary>Whether the program the server runs is there. A bare name such as "java" is found through PATH, so it counts.</summary>
    public static bool ExecutableFound(ServerRuntime rt)
    {
        var exe = rt.ResolveExecutable();
        if (string.IsNullOrWhiteSpace(exe)) return false;
        return !Path.IsPathRooted(exe) || File.Exists(exe);
    }

    /// <summary>The server's settings after a fresh look at its folder, and what changed (empty when nothing did).</summary>
    public static (ServerInstance Updated, List<string> Changes) Run(ServerRuntime rt)
    {
        var cur = rt.Config;
        var dir = cur.InstallDir;
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"The server folder can't be found: {dir}");

        var next = JsonSerializer.Deserialize<ServerInstance>(JsonSerializer.Serialize(cur, ServerManager.Json), ServerManager.Json)!;
        var changes = new List<string>();
        var oldGame = GameCatalog.Get(cur.GameId);
        var game = oldGame.Id == "generic" ? GameCatalog.DetectInFolder(dir).FirstOrDefault() ?? oldGame : oldGame;
        var fresh = GameCatalog.CreateInstance(game, dir, cur.Name, cur.SteamAppId);

        if (game.Id != oldGame.Id)
        {
            // Now recognised: take the game's launch setup, as if it had been added like this.
            next.GameId = game.Id;
            next.Executable = fresh.Executable;
            next.Arguments = fresh.Arguments;
            next.Environment = fresh.Environment;
            next.CommandChannel = fresh.CommandChannel;
            next.StopMethod = fresh.StopMethod;
            next.StopCommands = fresh.StopCommands;
            next.Rcon.Port = fresh.Rcon.Port;
            next.SteamAppId ??= fresh.SteamAppId;
            next.Launcher = fresh.Launcher;
            if (cur.Name == oldGame.Name) next.Name = game.Name; // still the default name: name it after the game
            changes.Add($"Recognised the game: {game.Name}.");
            if (!string.IsNullOrEmpty(fresh.Executable)) changes.Add($"Server program: {fresh.Executable}.");
            return (next, changes);
        }

        if (!ExecutableFound(rt) && !string.IsNullOrEmpty(fresh.Executable)
            && File.Exists(Path.Combine(dir, fresh.Executable))
            && !fresh.Executable.Equals(cur.Executable, StringComparison.OrdinalIgnoreCase))
        {
            next.Executable = fresh.Executable;
            if (game.Id == "minecraft-java") next.Arguments = fresh.Arguments; // the launch line names the jar it found
            changes.Add($"Found the server program: {fresh.Executable}.");
        }
        if (string.IsNullOrEmpty(cur.Launcher) && !string.IsNullOrEmpty(fresh.Launcher))
        {
            next.Launcher = fresh.Launcher;
            changes.Add($"Found the game's launcher: {fresh.Launcher}.");
        }
        return (next, changes);
    }
}
