using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emberkeep.Core;

/// <summary>
/// The Linux edition's view of each game: its Linux server program and environment, and paths in Linux form
/// (Unreal's Config/WindowsServer is Config/LinuxServer; Unity's AppData\LocalLow is ~/.config/unity3d).
/// Games with no Linux server are marked Windows-only.
/// </summary>
public static partial class GameCatalog
{
    private sealed record LinuxServer(string[] Programs, Dictionary<string, string>? Env = null, string[]? ConfigFiles = null, string? Note = null);

    private static readonly Dictionary<string, LinuxServer> Linux = new()
    {
        ["palworld"] = new(["PalServer.sh"]),
        ["valheim"] = new(["valheim_server.x86_64"], new() { ["LD_LIBRARY_PATH"] = "./linux64" }),
        ["satisfactory"] = new(["FactoryServer.sh"]),
        ["minecraft-java"] = new(["run.sh"]),
        ["minecraft-bedrock"] = new(["bedrock_server"], new() { ["LD_LIBRARY_PATH"] = "." }),
        ["7dtd"] = new(["7DaysToDieServer.x86_64"], new() { ["LD_LIBRARY_PATH"] = "." },
            ["serverconfig.xml", "~/.local/share/7DaysToDie/Saves/serveradmin.xml"]),
        ["zomboid"] = new(["start-server.sh"]),
        ["terraria"] = new(["TerrariaServer.bin.x86_64", "TerrariaServer"]),
        ["ark-se"] = new(["ShooterGame/Binaries/Linux/ShooterGameServer"]),
        ["corekeeper"] = new(["_launch.sh"], Note: "On Linux, Core Keeper's server needs Xvfb (for example: sudo apt install xvfb)."),
        ["rust"] = new(["RustDedicated"], new() { ["LD_LIBRARY_PATH"] = "RustDedicated_Data/Plugins/x86_64" }),
        ["generic"] = new([]),
    };

    /// <summary>A catalog path (Windows style) as this system has it.</summary>
    public static string OsPath(string path)
    {
        if (Platform.IsWindows) return path;
        path = path.Replace("%USERPROFILE%\\AppData\\LocalLow", "~/.config/unity3d", StringComparison.OrdinalIgnoreCase)
                   .Replace("\\Config\\WindowsServer\\", "\\Config\\LinuxServer\\", StringComparison.OrdinalIgnoreCase)
                   .Replace("/Config/WindowsServer/", "/Config/LinuxServer/", StringComparison.OrdinalIgnoreCase);
        return Platform.Path(path);
    }

    private static GameDefinition ForLinux(GameDefinition g)
    {
        if (!Linux.TryGetValue(g.Id, out var l))
            return g with
            {
                WindowsOnly = true,
                Launcher = null,
                Notes = $"{g.Name} has no Linux server, so the Linux edition of Emberkeep can't run it. Run it with Emberkeep on Windows.",
            };
        var env = new Dictionary<string, string>(g.Environment);
        foreach (var (k, v) in l.Env ?? new()) env[k] = v;
        return g with
        {
            Executables = l.Programs,
            DetectFiles = g.Id is "minecraft-java" or "generic" ? g.DetectFiles : l.Programs,
            Launcher = null, // setup programs are Windows apps
            Environment = env,
            DefaultArguments = g.DefaultArguments.Replace(".\\", "./"),
            ConfigFiles = (l.ConfigFiles ?? g.ConfigFiles).Select(OsPath).ToArray(),
            LogFiles = g.LogFiles.Select(OsPath).ToArray(),
            DefaultConfigTemplate = g.DefaultConfigTemplate is { } t ? (OsPath(t.Source), OsPath(t.Destination)) : null,
            Notes = l.Note is { } n ? (g.Notes is { } gn ? gn + " " + n : n) : g.Notes,
        };
    }

    /// <summary>
    /// Programs in a folder that could be the server: .exe files on Windows; on Linux, executable files and scripts
    /// (.sh, .x86_64), since Linux programs usually have no extension.
    /// </summary>
    public static List<string> ProgramsIn(string dir)
    {
        if (Platform.IsWindows) return Directory.EnumerateFiles(dir, "*.exe").ToList();
        return Directory.EnumerateFiles(dir).Where(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".sh" or ".x86_64") return true;
            if (ext is ".so" or ".txt" or ".json" or ".cfg" or ".ini" or ".log" or ".dll" or ".exe" or ".jar") return false;
            try { return (File.GetUnixFileMode(f) & UnixFileMode.UserExecute) != 0; } catch { return false; }
        }).ToList();
    }
}
