using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>A restart of this PC that Emberkeep is counting down to.</summary>
public sealed record PendingRestart(DateTime At, string Reason, bool Stopping);

/// <summary>
/// Restarts this PC: "Restart PC now" in App settings, or every day at a set time. Emberkeep first stops every running server
/// the normal way (so worlds are saved), then asks Windows to restart. With Start with Windows on, Emberkeep comes back after
/// the user signs in and starts the servers set to Start with Emberkeep.
/// </summary>
public sealed class PcRestart
{
    /// <summary>How long "Restart PC now" waits, so it can still be cancelled.</summary>
    public static readonly TimeSpan NowDelay = TimeSpan.FromSeconds(60);
    /// <summary>How early the daily restart is announced (and can be cancelled for that day).</summary>
    public static readonly TimeSpan DailyWarning = TimeSpan.FromMinutes(5);

    // EMBERKEEP_RESTART_COMMAND replaces shutdown.exe in tests, so a test never restarts the PC.
    private static readonly string Command = AppEnv.Get("RESTART_COMMAND") ?? "shutdown.exe";

    private readonly ServerManager _mgr;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private DateOnly? _skippedDay;   // the daily restart was cancelled for this day
    private DateOnly? _lastDailyDay; // the daily restart already ran (or was announced) on this day
    private int _started;

    public PcRestart(ServerManager mgr) => _mgr = mgr;

    public PendingRestart? Pending { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Starts watching the clock for the daily restart (once per run).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        // A restart time that has already passed today doesn't fire just because Emberkeep (re)started after it.
        if (DailyTime() is { } t && DateTime.Now.TimeOfDay > t.ToTimeSpan()) _lastDailyDay = DateOnly.FromDateTime(DateTime.Now);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { CheckDaily(); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        });
    }

    private TimeOnly? DailyTime() =>
        _mgr.Settings.DailyRestart && TimeOnly.TryParse(_mgr.Settings.DailyRestartTime, out var t) ? t : null;

    /// <summary>The next daily restart, or null when it's off.</summary>
    public DateTime? NextDaily()
    {
        if (DailyTime() is not { } t) return null;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var day = _lastDailyDay == today || _skippedDay == today || DateTime.Now.TimeOfDay > t.ToTimeSpan() ? today.AddDays(1) : today;
        return day.ToDateTime(t);
    }

    private void CheckDaily()
    {
        if (DailyTime() is not { } t || Pending != null) return;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (_lastDailyDay == today || _skippedDay == today) return;
        var at = today.ToDateTime(t);
        if (now < at - DailyWarning || now > at + TimeSpan.FromMinutes(1)) return;
        _lastDailyDay = today;
        Schedule(at > now ? at - now : TimeSpan.Zero, "the daily restart");
    }

    /// <summary>"Restart PC now": counts down a minute, then stops the servers and restarts.</summary>
    public PendingRestart RestartSoon() => Schedule(NowDelay, "Restart PC now");

    private PendingRestart Schedule(TimeSpan delay, string reason)
    {
        lock (_lock)
        {
            if (Pending != null) return Pending;
            var cts = new CancellationTokenSource();
            _cts = cts;
            Pending = new PendingRestart(DateTime.Now + delay, reason, false);
            LastError = null;
            Log($"This PC will restart at {Pending.At:HH:mm} ({reason}). The server will be stopped first.");
            _ = Task.Run(() => RunAsync(delay, cts.Token));
            return Pending;
        }
    }

    /// <summary>Calls off the countdown. Once the servers are being stopped it's too late (they stay stopped).</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (Pending == null || Pending.Stopping) return false;
            if (Pending.Reason == "the daily restart") _skippedDay = DateOnly.FromDateTime(Pending.At);
            _cts?.Cancel();
            Pending = null;
            Log("The PC restart was cancelled.");
            return true;
        }
    }

    private async Task RunAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { return; }

        lock (_lock)
        {
            if (ct.IsCancellationRequested || Pending == null) return;
            Pending = Pending with { Stopping = true };
        }
        try
        {
            await _mgr.StopAllAsync(); // the normal stop: save commands or Ctrl+C, then force-close after the server's timeout
            var psi = new ProcessStartInfo(Command, "/r /t 10 /d p:0:0 /c \"Emberkeep is restarting this PC.\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException($"Windows refused the restart (shutdown.exe exit code {p.ExitCode}).");
        }
        catch (Exception ex)
        {
            LastError = "Couldn't restart the PC: " + ex.Message;
            lock (_lock) Pending = null;
        }
    }

    private void Log(string text)
    {
        foreach (var rt in _mgr.Runtimes.Where(r => r.State != ServerState.Stopped)) rt.Logs.Add("system", text);
    }
}
