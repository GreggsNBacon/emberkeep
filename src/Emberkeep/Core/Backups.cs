using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Emberkeep.Core;

public sealed record BackupInfo(string Name, DateTime Created, long Size, string Reason, int Items);

/// <summary>One folder or file in a backup, and where it goes back to.</summary>
public sealed record BackupItem(int Index, string Configured, string Resolved, bool IsDir);

public sealed record BackupManifest(string Server, string GameId, DateTime Created, string Reason, List<BackupItem> Items);

/// <summary>
/// Backups of a server's world/save folders and config files, as zip files in the backup folder (one subfolder per server).
/// Restoring first takes a safety backup of what's there now. Old backups beyond the server's limit are deleted.
/// </summary>
public sealed class BackupService
{
    private readonly ServerManager _mgr;
    private readonly ConcurrentDictionary<string, bool> _busy = new();

    public BackupService(ServerManager mgr)
    {
        _mgr = mgr;
        mgr.ServerExited += (rt, requested) =>
        {
            if (rt.Config.BackupOnStop) _ = Task.Run(async () =>
            {
                try { await CreateAsync(rt, requested ? "server stopped" : "server crashed", "Emberkeep"); } catch { }
            });
        };
    }

    public string Root => AppEnv.Get("BACKUP_DIR") is { Length: > 0 } d ? d
        : !string.IsNullOrWhiteSpace(_mgr.Settings.BackupRoot) ? _mgr.Settings.BackupRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emberkeep", "Backups");

    public string DirFor(ServerInstance s)
    {
        var safe = new string(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return Path.Combine(Root, $"{safe}-{s.Id}");
    }

    public bool Busy(string serverId) => _busy.ContainsKey(serverId);

    /// <summary>What a backup of this server contains: its save folders (the user's list, or the game's suggestion) and config files.</summary>
    public static List<BackupItem> Items(ServerInstance s)
    {
        var list = new List<BackupItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var raw in s.BackupFolders ?? GameExtras.For(s.GameId).SaveFolders.ToList())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
            var full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(s.InstallDir, expanded));
            if (!seen.Add(full)) continue;
            list.Add(new BackupItem(i++, raw.Trim(), full, !File.Exists(full)));
        }
        foreach (var c in ConfigDiscovery.Discover(s).Where(c => c.Known))
            if (seen.Add(Path.GetFullPath(c.Path))) list.Add(new BackupItem(i++, c.DisplayPath, Path.GetFullPath(c.Path), false));
        return list;
    }

    public List<BackupInfo> List(ServerInstance s)
    {
        var dir = DirFor(s);
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.zip").Select(f =>
        {
            var fi = new FileInfo(f);
            var m = ReadManifest(f);
            return new BackupInfo(fi.Name, m?.Created ?? fi.CreationTimeUtc, fi.Length, m?.Reason ?? "", m?.Items.Count ?? 0);
        }).OrderByDescending(b => b.Created).ToList();
    }

    private static BackupManifest? ReadManifest(string zip)
    {
        try
        {
            using var z = ZipFile.OpenRead(zip);
            var e = z.GetEntry("emberkeep-backup.json");
            if (e == null) return null;
            using var st = e.Open();
            return JsonSerializer.Deserialize<BackupManifest>(st, ServerManager.Json);
        }
        catch { return null; }
    }

    /// <summary>Backs the server up. A running server is first asked to save its world (games that have a save command).</summary>
    public async Task<BackupInfo> CreateAsync(ServerRuntime rt, string reason, string who)
    {
        var s = rt.Config;
        if (!_busy.TryAdd(s.Id, true)) throw new InvalidOperationException("A backup of this server is already being made.");
        try
        {
            var items = Items(s).Where(it => it.IsDir ? Directory.Exists(it.Resolved) : File.Exists(it.Resolved)).ToList();
            if (items.Count == 0)
                throw new InvalidOperationException("There's nothing to back up yet: none of the backup folders exist. Start the server once, or choose its save folders in Backups.");

            if (rt.State == ServerState.Running && GameExtras.For(s.GameId).SaveCommands is { Length: > 0 } saves)
            {
                try
                {
                    foreach (var c in saves) await rt.SendCommandAsync(c);
                    await Task.Delay(5000); // give the game time to write the world
                }
                catch { } // no console (e.g. RCON not set up): back up what's on disk
            }

            var dir = DirFor(s);
            Directory.CreateDirectory(dir);
            var created = DateTime.Now;
            var slug = new string(reason.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            var name = $"{created:yyyy-MM-dd HH.mm.ss} {slug}.zip";
            var tmp = Path.Combine(dir, name + ".part");
            int skipped = 0;
            await Task.Run(() =>
            {
                using (var fs = File.Create(tmp))
                using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var it in items)
                    {
                        var files = it.IsDir ? Directory.EnumerateFiles(it.Resolved, "*", SearchOption.AllDirectories) : [it.Resolved];
                        foreach (var f in files)
                        {
                            var rel = it.IsDir ? Path.GetRelativePath(it.Resolved, f) : Path.GetFileName(f);
                            try
                            {
                                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                var e = z.CreateEntry($"items/{it.Index}/{rel.Replace('\\', '/')}", CompressionLevel.Fastest);
                                e.LastWriteTime = File.GetLastWriteTime(f);
                                using var dst = e.Open();
                                src.CopyTo(dst);
                            }
                            catch { skipped++; }
                        }
                    }
                    var m = z.CreateEntry("emberkeep-backup.json");
                    using var ms = m.Open();
                    JsonSerializer.Serialize(ms, new BackupManifest(s.Name, s.GameId, created.ToUniversalTime(), reason, items), ServerManager.Json);
                }
                File.Move(tmp, Path.Combine(dir, name));
            });
            Prune(s);
            var info = List(s).First(b => b.Name == name);
            var size = info.Size >= 1 << 20 ? $"{info.Size / (1 << 20)} MB" : $"{Math.Max(1, info.Size / 1024)} KB";
            _mgr.Activity.Add("backup", $"Backed up ({reason}, {size}){(skipped > 0 ? $"; {skipped} files in use were skipped" : "")}.", s, who, skipped > 0 ? "warn" : "info");
            rt.Logs.Add("system", $"Backup saved: {name}");
            return info;
        }
        catch (Exception ex)
        {
            _mgr.Activity.Add("backup", "Backup failed: " + ex.Message, s, who, "bad");
            try { foreach (var p in Directory.EnumerateFiles(DirFor(s), "*.part")) File.Delete(p); } catch { }
            throw;
        }
        finally { _busy.TryRemove(s.Id, out _); }
    }

    /// <summary>Puts a backup back. The server must be stopped; what's there now is backed up first.</summary>
    public async Task RestoreAsync(ServerRuntime rt, string name, string who)
    {
        var s = rt.Config;
        if (rt.State != ServerState.Stopped) throw new InvalidOperationException("Stop the server before restoring a backup.");
        var zip = PathOf(s, name);
        var m = ReadManifest(zip) ?? throw new InvalidOperationException("That file isn't an Emberkeep backup.");
        try { await CreateAsync(rt, "before restore", who); }
        catch (InvalidOperationException) { } // nothing there to keep
        if (!_busy.TryAdd(s.Id, true)) throw new InvalidOperationException("A backup of this server is being made. Try again in a moment.");
        try
        {
            await Task.Run(() =>
            {
                using var z = ZipFile.OpenRead(zip);
                foreach (var it in m.Items)
                {
                    var prefix = $"items/{it.Index}/";
                    if (it.IsDir)
                    {
                        // The folder goes back exactly as it was: remove what's there now (it's in the safety backup).
                        if (Directory.Exists(it.Resolved)) Directory.Delete(it.Resolved, recursive: true);
                        Directory.CreateDirectory(it.Resolved);
                    }
                    foreach (var e in z.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Name.Length > 0))
                    {
                        var rel = e.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                        var target = it.IsDir ? Path.GetFullPath(Path.Combine(it.Resolved, rel)) : it.Resolved;
                        if (it.IsDir && !target.StartsWith(it.Resolved.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue; // no "..\" escapes
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target, overwrite: true);
                    }
                }
            });
            _mgr.Activity.Add("restore", $"Restored the backup from {m.Created.ToLocalTime():d MMM yyyy HH:mm} ({m.Reason}).", s, who, "good");
            rt.Logs.Add("system", $"Restored backup {name}.");
        }
        catch (Exception ex)
        {
            _mgr.Activity.Add("restore", "Restore failed: " + ex.Message + " The files from before the restore are in the newest backup.", s, who, "bad");
            throw;
        }
        finally { _busy.TryRemove(s.Id, out _); }
    }

    public void Delete(ServerInstance s, string name) => File.Delete(PathOf(s, name));

    /// <summary>The backup's full path; only plain file names inside the server's backup folder are accepted.</summary>
    public string PathOf(ServerInstance s, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That backup doesn't exist.");
        var p = Path.Combine(DirFor(s), name);
        return File.Exists(p) ? p : throw new InvalidOperationException("That backup doesn't exist.");
    }

    private void Prune(ServerInstance s)
    {
        var keep = Math.Clamp(s.BackupKeep, 1, 500);
        foreach (var old in List(s).Skip(keep))
            try { File.Delete(Path.Combine(DirFor(s), old.Name)); } catch { }
    }
}
