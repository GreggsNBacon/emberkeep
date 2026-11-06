using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

public enum InstallState { Queued, Preparing, Downloading, Finishing, Done, Failed, Cancelled }

/// <summary>One SteamCMD download: a new server, or an update of one Emberkeep installed.</summary>
public sealed class InstallJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public required string Kind { get; init; } // "install" or "update"
    public required int AppId { get; init; }
    public required string Title { get; init; }
    public required string Dir { get; init; }
    public string? GameId { get; init; }
    public string? Name { get; init; }
    public string? ServerId { get; set; }
    public InstallState State { get; set; } = InstallState.Queued;
    public double Percent { get; set; }
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public string Message { get; set; } = "Waiting…";
    public string? Error { get; set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
    internal bool CreatedDir { get; set; }
    public bool Finished => State is InstallState.Done or InstallState.Failed or InstallState.Cancelled;
}

/// <summary>
/// Installs dedicated servers with SteamCMD and adds them to Emberkeep, and updates servers it installed. One download runs at
/// a time (SteamCMD can't run twice at once); the rest wait in line.
/// </summary>
public sealed class ServerInstaller
{
    private readonly ServerManager _mgr;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly List<InstallJob> _jobs = new();

    public ServerInstaller(ServerManager mgr) => _mgr = mgr;

    public IReadOnlyList<InstallJob> Jobs { get { lock (_jobs) return _jobs.ToList(); } }

    /// <summary>Whether an install or update is working on this server's folder, so it mustn't be started.</summary>
    public bool Busy(string serverId) { lock (_jobs) return _jobs.Any(j => !j.Finished && j.ServerId == serverId); }

    /// <summary>The folder a new server goes into by default: the chosen install folder, then the game's name.</summary>
    public string SuggestedDir(string title)
    {
        var root = string.IsNullOrWhiteSpace(_mgr.Settings.InstallRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Game Servers")
            : _mgr.Settings.InstallRoot;
        var safe = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c).ToArray()).Trim();
        var dir = Path.Combine(root, safe.Length > 0 ? safe : "Server");
        for (int n = 2; Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); n++) dir = Path.Combine(root, $"{safe} {n}");
        return dir;
    }

    public InstallJob Install(int appId, string title, string dir, string? gameId, string? name)
    {
        if (appId <= 0) throw new InvalidOperationException("Pick a game, or enter its Steam app ID.");
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) throw new InvalidOperationException("Choose a full folder path, like D:\\Game Servers\\Palworld.");
        dir = Path.GetFullPath(dir.Trim());
        if (dir.Contains($"{Path.DirectorySeparatorChar}steamapps{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That folder is inside a Steam library, which Steam itself manages. Pick a folder outside it.");
        lock (_jobs)
            if (_jobs.Any(j => !j.Finished && j.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Something is already being installed into that folder.");
        if (_mgr.Runtimes.Any(r => Path.GetFullPath(r.Config.InstallDir).TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A server in Emberkeep already uses that folder. Use Update server in its menu instead.");
        var root = Path.GetPathRoot(dir);
        if (root == null || !Directory.Exists(root)) throw new InvalidOperationException($"The drive {root} isn't available.");

        var job = new InstallJob { Kind = "install", AppId = appId, Title = title, Dir = dir, GameId = gameId, Name = name };
        // Remembered as the place for the next server too.
        _mgr.Settings.InstallRoot = Path.GetDirectoryName(dir) ?? _mgr.Settings.InstallRoot;
        _mgr.Save();
        Enqueue(job);
        return job;
    }

    public InstallJob Update(ServerRuntime rt)
    {
        var appId = rt.Config.SteamCmdAppId ?? throw new InvalidOperationException("Emberkeep didn't install this server, so it can't update it. Update it the way it was installed (for example through Steam).");
        if (rt.State != ServerState.Stopped) throw new InvalidOperationException("Stop the server before updating it.");
        if (Busy(rt.Config.Id)) throw new InvalidOperationException("This server is already being updated.");
        var job = new InstallJob { Kind = "update", AppId = appId, Title = rt.Config.Name, Dir = rt.Config.InstallDir, ServerId = rt.Config.Id };
        Enqueue(job);
        return job;
    }

    public bool Cancel(string id)
    {
        InstallJob? job; lock (_jobs) job = _jobs.FirstOrDefault(j => j.Id == id);
        if (job == null || job.Finished) return false;
        job.Cts.Cancel();
        return true;
    }

    /// <summary>Removes finished jobs from the list.</summary>
    public void ClearFinished() { lock (_jobs) _jobs.RemoveAll(j => j.Finished); }

    private void Enqueue(InstallJob job)
    {
        lock (_jobs) { _jobs.Add(job); if (_jobs.Count > 30) _jobs.RemoveAll(j => j.Finished && j != job && _jobs.Count > 30); }
        _ = Task.Run(() => RunAsync(job));
    }

    private async Task RunAsync(InstallJob job)
    {
        var ct = job.Cts.Token;
        var log = job.ServerId != null ? _mgr.Get(job.ServerId)?.Logs : null;
        try
        {
            await _one.WaitAsync(ct);
            try
            {
                job.State = InstallState.Preparing;
                job.Message = SteamCmd.IsInstalled(_mgr.Settings) ? "Starting SteamCMD…" : "Setting up SteamCMD (only the first time)…";
                await SteamCmd.EnsureAsync(_mgr.Settings, m => job.Message = m, ct);
                if (!Directory.Exists(job.Dir)) { Directory.CreateDirectory(job.Dir); job.CreatedDir = true; }
                job.Message = "Connecting to Steam…";
                log?.Add("system", $"Updating the server files with SteamCMD (app {job.AppId})…");
                // Progress: SteamCMD holds its output lines back while it runs when the output is captured, but it writes the
                // same "Update state … progress: 45.12 (… / …)" lines to logs\console_log.txt as it goes, so follow that file.
                Action<string> onLine = line =>
                {
                    if (SteamCmd.ParseProgress(line) is { } p)
                    {
                        job.State = InstallState.Downloading;
                        job.Percent = p.Percent; job.BytesDone = p.Done; job.BytesTotal = p.Total;
                        job.Message = p.Phase switch
                        {
                            var x when x.Contains("download", StringComparison.OrdinalIgnoreCase) => "Downloading",
                            var x when x.Contains("verif", StringComparison.OrdinalIgnoreCase) || x.Contains("validat", StringComparison.OrdinalIgnoreCase) => "Checking files",
                            var x when x.Contains("prealloc", StringComparison.OrdinalIgnoreCase) => "Making room on the disk",
                            var x when x.Contains("commit", StringComparison.OrdinalIgnoreCase) => "Finishing",
                            var x when x.Contains("unknown", StringComparison.OrdinalIgnoreCase) || x.Contains("reconfig", StringComparison.OrdinalIgnoreCase) => "Getting ready",
                            _ => char.ToUpper(p.Phase[0]) + p.Phase[1..],
                        };
                    }
                    else if (line.Contains("Update state", StringComparison.OrdinalIgnoreCase) && line.Contains("reconfiguring", StringComparison.OrdinalIgnoreCase))
                        job.Message = "Getting ready";
                    else if (line.StartsWith("Logging in", StringComparison.OrdinalIgnoreCase) || line.Contains("Waiting for user info", StringComparison.OrdinalIgnoreCase))
                        job.Message = "Connecting to Steam…";
                };
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var consoleLog = Path.Combine(Path.GetDirectoryName(SteamCmd.ExePath(_mgr.Settings))!, "logs", "console_log.txt");
                long from = File.Exists(consoleLog) ? new FileInfo(consoleLog).Length : 0;
                _ = Task.Run(async () =>
                {
                    var acf = Path.Combine(job.Dir, "steamapps", $"appmanifest_{job.AppId}.acf");
                    while (!watch.IsCancellationRequested)
                    {
                        try
                        {
                            if (File.Exists(consoleLog))
                            {
                                using var fs = new FileStream(consoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                if (fs.Length < from) from = 0; // the log was rotated
                                fs.Seek(from, SeekOrigin.Begin);
                                using var r = new StreamReader(fs);
                                var text = await r.ReadToEndAsync();
                                from = fs.Length;
                                foreach (var l in text.Split('\n')) if (l.Length > 0) onLine(l);
                            }
                            // Fallback before the first progress line: the total size from the app manifest.
                            if (job.BytesTotal == 0 && File.Exists(acf) && SteamCmd.ParseManifestProgress(File.ReadAllText(acf)) is { } m)
                                job.BytesTotal = m.Total;
                        }
                        catch { } // files being rewritten as we read them
                        try { await Task.Delay(1000, watch.Token); } catch { }
                    }
                });
                var (code, output) = await SteamCmd.RunAsync(_mgr.Settings, $"+app_update {job.AppId} validate", onLine, ct, beforeLogin: $"+force_install_dir \"{job.Dir}\"");
                watch.Cancel();

                bool ok = output.Contains($"Success! App '{job.AppId}'", StringComparison.OrdinalIgnoreCase)
                    || (code == 0 && !output.Contains("ERROR!", StringComparison.OrdinalIgnoreCase));
                if (!ok) throw new InvalidOperationException(SteamCmd.FailureReason(output, code));
                // SteamCMD reports success even when Steam sent nothing (an app that needs an account that owns it).
                if (job.Kind == "install" && !HasServerFiles(job.Dir))
                    throw new InvalidOperationException("Steam didn't send any files: this server needs a Steam account that owns the game. Install it through Steam, then add its folder.");

                job.State = InstallState.Finishing; job.Percent = 100; job.Message = "Setting the server up…";
                if (job.Kind == "install") AddServer(job);
                else
                {
                    log?.Add("system", "The server files are up to date.");
                    if (job.ServerId != null && _mgr.Get(job.ServerId) is { } rt && rt.State == ServerState.Stopped)
                    {
                        var (next, changes) = ServerRecheck.Run(rt); // a new version may have a new program name
                        if (changes.Count > 0) { _mgr.Update(rt.Config.Id, next); foreach (var c in changes) log?.Add("system", c); }
                    }
                }
                job.State = InstallState.Done;
                job.Message = job.Kind == "install" ? "Installed and added." : "Up to date.";
            }
            finally { _one.Release(); }
        }
        catch (OperationCanceledException)
        {
            job.State = InstallState.Cancelled; job.Message = "Cancelled.";
            log?.Add("system", "The update was cancelled.");
            if (job.Kind == "install" && job.CreatedDir) TryDelete(job.Dir); // leave nothing half-installed behind
        }
        catch (Exception ex)
        {
            job.State = InstallState.Failed; job.Error = ex.Message; job.Message = "Failed.";
            log?.Add("system", "Updating failed: " + ex.Message);
            if (job.Kind == "install" && job.CreatedDir) TryDelete(job.Dir);
        }
        finally { job.FinishedAt = DateTime.UtcNow; }
    }

    private void AddServer(InstallJob job)
    {
        var game = (job.GameId != null ? GameCatalog.Get(job.GameId) : null)
            ?? GameCatalog.All.FirstOrDefault(g => g.SteamAppIds.Contains(job.AppId));
        if (game == null || game.Id == "generic") game = GameCatalog.DetectInFolder(job.Dir).FirstOrDefault() ?? GameCatalog.Get("generic");
        var name = string.IsNullOrWhiteSpace(job.Name) ? (game.Id == "generic" ? job.Title : game.Name) : job.Name.Trim();
        var inst = GameCatalog.CreateInstance(game, job.Dir, name, null);
        inst.SteamCmdAppId = job.AppId;
        var rt = _mgr.Add(inst);
        job.ServerId = inst.Id;
        rt.Logs.Add("system", $"Installed with SteamCMD (app {job.AppId}) into {job.Dir}.");
        _ = ArtCache.AutoDetectAsync(_mgr, inst.Id);
    }

    /// <summary>Anything besides Steam's own bookkeeping (the steamapps folder and installscript.vdf).</summary>
    private static bool HasServerFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).Any(p =>
                !Path.GetFileName(p).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(p).Equals("installscript.vdf", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(500); } // SteamCMD may still be letting go of files
        }
    }
}
