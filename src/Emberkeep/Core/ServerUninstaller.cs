using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>What uninstalling a server would do, shown before anyone confirms it.</summary>
/// <param name="Method">"steam": Steam uninstalls it (a game in a Steam library). "delete": Emberkeep deletes the folder.
/// "remove": the folder is already gone, so only the list entry goes.</param>
/// <param name="Problem">Why it can't be uninstalled right now (null when it can).</param>
public sealed record UninstallInfo(string Method, string Dir, long? Bytes, bool SizeIsPartial, int? SteamAppId, string? SteamName,
    string? Problem, bool WaitingForSteam);

/// <summary>
/// Uninstalls servers: removes them from Emberkeep and from the disk.
/// <list type="bullet">
/// <item>A server in a Steam library (…\steamapps\common\…, installed by Steam itself) is uninstalled by Steam, which asks
/// for confirmation on the host PC; once its files are gone, Emberkeep drops it from the list.</item>
/// <item>Anything else (installed with SteamCMD, which keeps everything inside the folder, or added by hand) is deleted by
/// Emberkeep, after strict checks that the folder holds only this server.</item>
/// </list>
/// Emberkeep's backups of the server are kept either way.
/// </summary>
public sealed class ServerUninstaller
{
    private readonly ServerManager _mgr;
    private readonly Func<string, bool> _busy;
    private readonly Func<string> _backupRoot;
    private readonly ConcurrentDictionary<string, DateTime> _waitingForSteam = new();

    /// <param name="busy">True while the server's files are being installed, updated or backed up.</param>
    public ServerUninstaller(ServerManager mgr, Func<string, bool> busy, Func<string> backupRoot)
    {
        _mgr = mgr; _busy = busy; _backupRoot = backupRoot;
    }

    private static StringComparison Cmp => Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); } catch { return p; } }
    private static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), Cmp);
    /// <summary>child is inside parent (not the same folder).</summary>
    private static bool Inside(string child, string parent) => Norm(child).StartsWith(Norm(parent) + Path.DirectorySeparatorChar, Cmp);

    public UninstallInfo Info(ServerRuntime rt, bool measure = true)
    {
        var dir = rt.Config.InstallDir;
        var steam = SteamLibraryApp(dir);
        bool waiting = _waitingForSteam.ContainsKey(rt.Config.Id);
        if (!Directory.Exists(dir)) return new UninstallInfo("remove", dir, null, false, null, null, Problem(rt, dir, checkFolder: false), waiting);

        long? bytes = null; bool part = false;
        if (measure) bytes = Size(dir, out part);
        if (steam is { } s)
        {
            string? problem = Problem(rt, dir, checkFolder: false)
                ?? (Platform.IsWindows ? null : $"Steam installed this server. Uninstall {s.Name} in Steam, then remove it from Emberkeep.");
            return new UninstallInfo("steam", dir, bytes, part, s.AppId, s.Name, problem, waiting);
        }
        return new UninstallInfo("delete", dir, bytes, part, null, null, Problem(rt, dir, checkFolder: true), waiting);
    }

    /// <summary>Why this server can't be uninstalled now, or null.</summary>
    private string? Problem(ServerRuntime rt, string dir, bool checkFolder)
    {
        if (rt.State != ServerState.Stopped) return "Stop the server first.";
        if (_busy(rt.Config.Id)) return "Its files are being installed, updated or backed up. Wait for that to finish.";
        if (_waitingForSteam.ContainsKey(rt.Config.Id)) return "Steam is uninstalling it. Confirm in Steam on the host PC.";
        if (!checkFolder) return null;

        if (!Path.IsPathFullyQualified(dir)) return "The server's folder isn't a full path, so Emberkeep won't delete it.";
        var full = Norm(dir);
        if (Path.GetPathRoot(full) is { } root && Same(full, root)) return "The server's folder is a whole drive. Emberkeep won't delete that.";
        try
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return "The server's folder is a link to another folder. Delete it yourself if you're sure.";
        }
        catch { }

        // Folders that must never go, nor anything that contains them.
        foreach (var keep in ProtectedFolders())
            if (Same(full, keep) || Inside(keep, full))
                return $"The server's folder is (or contains) {keep}, which Emberkeep won't delete. Delete the server's files yourself.";

        // Other servers in the same folder or below it.
        var others = _mgr.Runtimes.Where(r => r.Config.Id != rt.Config.Id && !string.IsNullOrWhiteSpace(r.Config.InstallDir)
            && (Same(r.Config.InstallDir, full) || Inside(r.Config.InstallDir, full))).Select(r => r.Config.Name).ToList();
        if (others.Count > 0) return $"This folder also holds {string.Join(", ", others)}. Uninstalling would delete {(others.Count == 1 ? "it" : "them")} too.";
        return null;
    }

    private IEnumerable<string> ProtectedFolders()
    {
        var list = new List<string?>
        {
            ServerManager.DataDir, _backupRoot(), SteamCmd.OwnDir, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        if (Platform.IsWindows)
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        else
            list.AddRange(["/home", "/root", "/usr", "/etc", "/opt", "/var", "/var/lib", "/srv", "/tmp", "/boot", "/bin", "/lib"]);
        // Steam itself and its library folders (the games in them belong to Steam).
        if (SteamScanner.FindSteamRoot() is { } steamRoot)
        {
            list.Add(steamRoot);
            foreach (var lib in SteamScanner.FindLibraries(steamRoot))
            {
                list.Add(lib);
                list.Add(Path.Combine(lib, "steamapps"));
                list.Add(Path.Combine(lib, "steamapps", "common"));
            }
        }
        return list.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Norm(p!)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A game folder Steam installed: …\steamapps\common\&lt;folder&gt; with that library's appmanifest naming it.</summary>
    public static (int AppId, string Name, string Manifest)? SteamLibraryApp(string dir)
    {
        try
        {
            var full = Norm(dir);
            var common = Path.GetDirectoryName(full);
            var steamapps = common == null ? null : Path.GetDirectoryName(common);
            if (common == null || steamapps == null) return null;
            if (!Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(steamapps).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return null;
            var folder = Path.GetFileName(full);
            foreach (var mf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                VdfNode? app;
                try { app = Vdf.Parse(File.ReadAllText(mf)).Child("AppState"); } catch { continue; }
                if (app == null || !string.Equals(app.Value("installdir"), folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(app.Value("appid"), out var id)) continue;
                return (id, app.Value("name") ?? folder, mf);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Total size of the folder's files, giving up after a few seconds on huge folders (partial = true then).</summary>
    private static long Size(string dir, out bool partial)
    {
        partial = false;
        long total = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                total += f.Length;
                if (sw.ElapsedMilliseconds > 8000) { partial = true; break; }
            }
        }
        catch { partial = true; }
        return total;
    }

    /// <summary>
    /// Steam: asks Steam to uninstall the game (Steam shows its own confirmation on the host PC) and watches for the files
    /// to go, then removes the server from Emberkeep. Folder: deletes it and removes the server. Throws when it can't.
    /// </summary>
    public async Task<string> UninstallAsync(ServerRuntime rt, string? who)
    {
        var info = Info(rt, measure: false);
        if (info.Problem != null) throw new InvalidOperationException(info.Problem);
        var cfg = rt.Config;
        switch (info.Method)
        {
            case "remove":
                _mgr.Remove(cfg.Id);
                _mgr.Activity.Add("settings", "Uninstalled (its folder was already gone).", cfg, who);
                return "removed";

            case "steam":
                if (SteamLibraryApp(info.Dir) is not { } app) throw new InvalidOperationException("Steam's record of this game is gone. Check files again, then try once more.");
                Process.Start(new ProcessStartInfo($"steam://uninstall/{app.AppId}") { UseShellExecute = true });
                _waitingForSteam[cfg.Id] = DateTime.UtcNow;
                _mgr.Activity.Add("settings", $"Asked Steam to uninstall {app.Name}.", cfg, who);
                _ = Task.Run(() => WatchSteamAsync(cfg.Id, app.Manifest, info.Dir, who));
                return "steam";

            default:
                await Task.Run(() => DeleteFolder(info.Dir));
                _mgr.Remove(cfg.Id);
                _mgr.Activity.Add("settings", $"Uninstalled: deleted {info.Dir}.", cfg, who, "warn");
                return "deleted";
        }
    }

    private static void DeleteFolder(string dir)
    {
        // Read-only files (common in game folders) would stop Directory.Delete.
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            try { if (f.IsReadOnly) f.IsReadOnly = false; } catch { }
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Some files couldn't be deleted ({ex.Message}). Close anything using the server's folder and try again; what's left is in {dir}.");
        }
    }

    /// <summary>Waits up to 30 minutes for Steam to remove the game, then drops the server from the list.</summary>
    private async Task WatchSteamAsync(string id, string manifest, string dir, string? who)
    {
        try
        {
            for (var until = DateTime.UtcNow.AddMinutes(30); DateTime.UtcNow < until;)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_mgr.Get(id) is not { } rt) return; // removed some other way
                bool gone = !File.Exists(manifest) && (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any());
                if (!gone) continue;
                _waitingForSteam.TryRemove(id, out _);
                try { _mgr.Remove(id); _mgr.Activity.Add("settings", "Uninstalled by Steam.", rt.Config, who); }
                catch (InvalidOperationException) { } // started again in the meantime: leave it
                return;
            }
        }
        finally { _waitingForSteam.TryRemove(id, out _); }
    }
}
