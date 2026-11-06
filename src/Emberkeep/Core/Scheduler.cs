using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>
/// Runs each server's scheduled tasks: daily at a time (on chosen days) or every N minutes. Restarts and stops can warn
/// players 10, 5 and 1 minute ahead in games that have a broadcast command.
/// </summary>
public sealed class Scheduler
{
    private static readonly int[] WarnMinutes = [10, 5, 1];
    private readonly ServerManager _mgr;
    private readonly BackupService _backups;
    private readonly ServerInstaller _installer;
    private readonly HashSet<string> _warned = new();
    private readonly HashSet<string> _running = new();
    private int _started;

    public Scheduler(ServerManager mgr, BackupService backups, ServerInstaller installer)
    {
        _mgr = mgr; _backups = backups; _installer = installer;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { Tick(DateTime.Now); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        });
    }

    /// <summary>When the task runs next, or null if never (disabled, or no days chosen).</summary>
    public static DateTime? Next(ScheduledTask t, DateTime now)
    {
        if (!t.Enabled) return null;
        if (t.Kind == "interval")
        {
            var every = TimeSpan.FromMinutes(Math.Max(5, t.EveryMinutes));
            var last = t.LastRun?.ToLocalTime() ?? now;
            var next = last + every;
            return next < now ? now : next;
        }
        if (!TimeOnly.TryParse(t.Time, out var time)) return null;
        for (int d = 0; d < 8; d++)
        {
            var day = DateOnly.FromDateTime(now).AddDays(d);
            if (t.Days.Count > 0 && !t.Days.Contains((int)day.DayOfWeek)) continue;
            var at = day.ToDateTime(time);
            if (at > now || (d == 0 && Ran(t, at) == false && now - at < TimeSpan.FromMinutes(2))) return at;
        }
        return null;
    }

    private static bool Ran(ScheduledTask t, DateTime at) => t.LastRun is DateTime lr && lr.ToLocalTime() >= at.AddSeconds(-1);

    private void Tick(DateTime now)
    {
        foreach (var rt in _mgr.Runtimes.ToList())
            foreach (var t in rt.Config.Schedules.Where(t => t.Enabled).ToList())
            {
                if (t.Kind == "interval" && t.LastRun == null) { t.LastRun = DateTime.UtcNow; _mgr.Save(); continue; } // count from now
                if (Next(t, now) is not DateTime at) continue;
                var key = $"{t.Id}@{at:O}";
                // Warn players before a restart or stop.
                if (t.Warn && t.Action is "restart" or "stop" or "update" && rt.State == ServerState.Running)
                    foreach (var m in WarnMinutes)
                        if (now >= at.AddMinutes(-m) && now < at.AddMinutes(-m + 1) && _warned.Add($"{key}-{m}"))
                            Warn(rt, t, m);
                if (now >= at && !Ran(t, at) && Claim(key))
                    _ = Task.Run(async () => { try { await RunAsync(rt, t, "schedule"); } finally { lock (_running) _running.Remove(key); } });
            }
        if (_warned.Count > 500) _warned.Clear();
    }

    private bool Claim(string key) { lock (_running) return _running.Add(key); }

    private static void Warn(ServerRuntime rt, ScheduledTask t, int minutes)
    {
        var what = t.Action == "stop" ? "shutting down" : "restarting";
        var cmd = GameExtras.BroadcastCommand(rt.Config.GameId, $"Server {what} in {minutes} minute{(minutes == 1 ? "" : "s")}");
        if (cmd == null) return;
        _ = rt.SendCommandAsync(cmd).ContinueWith(_ => { }); // best effort
    }

    /// <summary>Runs a task now (also used by "Run now" in the Schedule tab).</summary>
    public async Task<string> RunAsync(ServerRuntime rt, ScheduledTask t, string who)
    {
        string result;
        var level = "info";
        try
        {
            result = await DoAsync(rt, t, who);
        }
        catch (Exception ex) { result = "Failed: " + ex.Message; level = "bad"; }
        t.LastRun = DateTime.UtcNow;
        t.LastResult = result;
        _mgr.Save();
        _mgr.Activity.Add("schedule", $"{Describe(t)}: {result}", rt.Config, who, level);
        return result;
    }

    private async Task<string> DoAsync(ServerRuntime rt, ScheduledTask t, string who)
    {
        bool running = rt.State == ServerState.Running;
        switch (t.Action)
        {
            case "restart":
                if (!running) return "Skipped: the server wasn't running.";
                rt.Actor = who;
                await rt.StopAsync();
                await WaitStopped(rt);
                rt.Actor = who;
                await _installer.StartAsync(rt);
                return "Restarted.";
            case "stop":
                if (!running) return "Skipped: the server wasn't running.";
                rt.Actor = who;
                await rt.StopAsync();
                return "Stopped.";
            case "start":
                if (rt.State != ServerState.Stopped) return "Skipped: the server was already running.";
                if (_installer.Busy(rt.Config.Id)) return "Skipped: its files were being updated.";
                rt.Actor = who;
                await _installer.StartAsync(rt);
                return "Started.";
            case "backup":
                var b = await _backups.CreateAsync(rt, "scheduled", who);
                return $"Backed up ({b.Size / (1 << 20)} MB).";
            case "command":
                if (!running) return "Skipped: the server wasn't running.";
                if (string.IsNullOrWhiteSpace(t.Command)) return "Skipped: no command set.";
                await rt.SendCommandAsync(t.Command);
                return $"Sent \"{t.Command}\".";
            case "update":
                if (rt.Config.SteamCmdAppId == null) return "Skipped: Emberkeep didn't install this server, so it can't update it.";
                if (running) { rt.Actor = who; await rt.StopAsync(); await WaitStopped(rt); }
                var job = _installer.Update(rt);
                await job.Completion.Task;
                if (job.State != InstallState.Done) return $"Update failed: {job.Error ?? job.Message}";
                if (running) { rt.Actor = who; await rt.StartAsync(); return "Updated and started again."; }
                return "Updated.";
            default:
                return "Skipped: unknown task.";
        }
    }

    private static async Task WaitStopped(ServerRuntime rt)
    {
        for (int i = 0; i < 1200 && rt.State != ServerState.Stopped; i++) await Task.Delay(500);
    }

    public static string Describe(ScheduledTask t) => t.Action switch
    {
        "restart" => "Scheduled restart",
        "stop" => "Scheduled stop",
        "start" => "Scheduled start",
        "backup" => "Scheduled backup",
        "command" => "Scheduled command",
        "update" => "Scheduled update",
        _ => "Scheduled task",
    };
}
