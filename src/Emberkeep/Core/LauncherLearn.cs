using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>What "Learn from launcher" has seen so far, for the UI.</summary>
public sealed record LearnStatus(string State, string Message, string? Executable, string? Arguments, List<LearnedFile> Files, string? Launcher);

/// <summary>A config the launcher passed encoded on the command line, which Emberkeep will keep as a file.</summary>
public sealed record LearnedFile(string Path, string Flag, string Encoding, int Bytes, List<string> Keys);

/// <summary>
/// "Learn from launcher": opens a game's own setup program, waits for it to start the server, and reads the exact
/// program and command line it used. Emberkeep can then run the server itself with those settings, without the launcher.
/// Works for any game; settings passed as encoded JSON (Assetto Corsa EVO) become editable files.
/// </summary>
public sealed class LauncherLearn
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);
    // Helpers a launcher might start that are never the server itself.
    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "WerFault.exe", "explorer.exe", "msedgewebview2.exe",
        "dotnet.exe", "CefSharp.BrowserSubprocess.exe", "steam.exe", "steamwebhelper.exe", "UnityCrashHandler64.exe", "CrashReportClient.exe",
    };

    private readonly ServerManager _mgr;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public LauncherLearn(ServerManager mgr) => _mgr = mgr;

    private sealed class Session
    {
        public required string ServerId;
        public required string LauncherPath;
        public int? LauncherPid;
        public required HashSet<int> Before;
        public readonly CancellationTokenSource Cts = new();
        public volatile string State = "waiting";
        public volatile string Message = "";
        public string? ExePath, Args;
        public int? ServerPid;
    }

    public static string? LauncherPath(ServerInstance s)
    {
        if (string.IsNullOrWhiteSpace(s.Launcher)) return null;
        var p = Path.IsPathRooted(s.Launcher) ? s.Launcher : Path.Combine(s.InstallDir, s.Launcher);
        return File.Exists(p) ? Path.GetFullPath(p) : null;
    }

    public LearnStatus Status(string serverId)
    {
        var rt = _mgr.Get(serverId);
        var launcher = rt == null ? null : rt.Config.Launcher;
        if (!_sessions.TryGetValue(serverId, out var s)) return new("idle", "", null, null, new(), launcher);
        var files = new List<LearnedFile>();
        string? shownArgs = null;
        if (s.Args != null)
        {
            shownArgs = ArgBlobs.Summarize(s.Args);
            var (_, extracted) = ArgBlobs.Extract(s.Args);
            var blobs = ArgBlobs.Find(s.Args);
            for (int i = 0; i < extracted.Count && i < blobs.Count; i++)
                files.Add(new LearnedFile(extracted[i].RelPath, blobs[i].Flag ?? "", blobs[i].Encoding, extracted[i].Json.Length, TopKeys(blobs[i].Json)));
        }
        string? exe = s.ExePath == null || rt == null ? s.ExePath : Relative(rt.Config.InstallDir, s.ExePath);
        return new(s.State, s.Message, exe, shownArgs, files, launcher);
    }

    private static List<string> TopKeys(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind == JsonValueKind.Object ? d.RootElement.EnumerateObject().Select(p => p.Name).Take(12).ToList() : new();
        }
        catch { return new(); }
    }

    /// <summary>Opens the launcher and starts watching for the server it launches.</summary>
    public void Start(string serverId)
    {
        var rt = _mgr.Get(serverId) ?? throw new KeyNotFoundException();
        if (rt.State != ServerState.Stopped) throw new InvalidOperationException("Stop the server first. The launcher will start it, and Emberkeep will watch how.");
        var path = LauncherPath(rt.Config) ?? throw new InvalidOperationException("Choose the game's launcher program first (Settings → Launch → Launcher).");
        Cancel(serverId);

        var session = new Session { ServerId = serverId, LauncherPath = path, Before = Native.ParentMap().Keys.ToHashSet() };
        session.Message = $"Waiting for {Path.GetFileNameWithoutExtension(path)} to start the server…";
        try
        {
            // The server the launcher starts inherits Ctrl+C handling from it, and it from us: make sure Ctrl+C isn't
            // ignored, or Emberkeep could never stop that server gracefully.
            lock (Native.ConsoleLock)
            {
                Native.AllowCtrlCInChildren();
                using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
                session.LauncherPid = p?.Id;
            }
        }
        catch (Exception ex) { throw new InvalidOperationException($"Couldn't open the launcher: {ex.Message}"); }
        _sessions[serverId] = session;
        _ = Task.Run(() => WatchAsync(session, rt));
    }

    public void Cancel(string serverId)
    {
        if (_sessions.TryRemove(serverId, out var s)) s.Cts.Cancel();
    }

    private async Task WatchAsync(Session s, ServerRuntime rt)
    {
        var started = DateTime.UtcNow;
        DateTime? launcherGoneAt = null;
        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                var map = Native.ParentMap();
                var candidates = new List<int>();
                if (s.LauncherPid is int lp)
                {
                    if (map.ContainsKey(lp)) candidates.AddRange(Native.ProcessTree(lp, map).Where(p => p != lp));
                    else launcherGoneAt ??= DateTime.UtcNow;
                }
                // Launchers that hand off through the shell lose the parent link: also accept new processes from the server's folder.
                candidates.AddRange(map.Keys.Where(p => !s.Before.Contains(p) && !candidates.Contains(p)));

                foreach (var pid in candidates)
                {
                    var image = Native.ImagePath(pid);
                    if (image == null || image.Equals(s.LauncherPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(image);
                    if (Ignore.Contains(name)) continue;
                    bool fromLauncher = s.LauncherPid is int l2 && Native.ProcessTree(l2, map).Contains(pid);
                    bool inFolder = IsUnder(image, rt.Config.InstallDir) || IsUnder(image, Path.GetDirectoryName(s.LauncherPath)!);
                    if (!fromLauncher && !inFolder) continue;
                    // Windowed children are usually the launcher's own helpers; servers are console programs (or say "server").
                    if (Native.IsGuiExe(image) == true && !name.Contains("server", StringComparison.OrdinalIgnoreCase)) continue;

                    var cmd = Native.CommandLine(pid);
                    if (cmd == null) continue;
                    s.ExePath = image;
                    s.Args = ArgBlobs.SplitProgram(cmd).Args;
                    s.ServerPid = pid;
                    int blobs = ArgBlobs.Find(s.Args).Count;
                    s.Message = $"The launcher started {name}." + (blobs > 0 ? $" Its settings were passed in {blobs} encoded block{(blobs == 1 ? "" : "s")}, which Emberkeep will keep as editable files." : "");
                    s.State = "captured";
                    // Show the server as running in Emberkeep so it can be stopped from here.
                    if (rt.State == ServerState.Stopped) { try { rt.Adopt(pid); } catch { } }
                    return;
                }

                if (launcherGoneAt is DateTime g && DateTime.UtcNow - g > TimeSpan.FromSeconds(5))
                {
                    s.State = "closed"; s.Message = "The launcher closed without starting a server. Open it again and use its Start or Run button.";
                    return;
                }
                if (DateTime.UtcNow - started > GiveUpAfter)
                {
                    s.State = "timeout"; s.Message = "Stopped waiting: the launcher didn't start a server within 30 minutes.";
                    return;
                }
                await Task.Delay(400, s.Cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { s.State = "error"; s.Message = ex.Message; }
    }

    /// <summary>Uses what was captured as the server's launch settings, writing any encoded settings to files.</summary>
    public void Apply(string serverId)
    {
        var rt = _mgr.Get(serverId) ?? throw new KeyNotFoundException();
        if (!_sessions.TryGetValue(serverId, out var s) || s.State != "captured" || s.ExePath == null || s.Args == null)
            throw new InvalidOperationException("Nothing has been learned yet. Start the server from the launcher first.");

        var (args, files) = ArgBlobs.Extract(s.Args);
        foreach (var (rel, json) in files)
        {
            var full = Path.Combine(rt.Config.InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full) && File.ReadAllText(full) != json)
            {
                // Keep what was there in Emberkeep's backups, like any other config edit.
                var backups = Path.Combine(ServerManager.BackupDir, serverId);
                Directory.CreateDirectory(backups);
                File.Copy(full, Path.Combine(backups, $"{Path.GetFileName(full)}.{DateTime.Now:yyyyMMdd-HHmmss}.bak"), true);
            }
            File.WriteAllText(full, json);
        }

        var next = JsonSerializer.Deserialize<ServerInstance>(JsonSerializer.Serialize(rt.Config, ServerManager.Json), ServerManager.Json)!;
        next.Executable = Relative(next.InstallDir, s.ExePath);
        next.Arguments = args;
        var exeDir = Path.GetDirectoryName(s.ExePath)!;
        next.WorkingDirectory = PathsEqual(exeDir, next.InstallDir) ? "" : exeDir;
        _mgr.Update(serverId, next);
        _sessions.TryRemove(serverId, out _);
    }

    private static bool IsUnder(string path, string dir) =>
        dir.Length > 0 && Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Relative(string baseDir, string path) => IsUnder(path, baseDir) ? Path.GetRelativePath(baseDir, path) : path;
}
