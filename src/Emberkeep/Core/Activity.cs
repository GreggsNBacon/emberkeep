using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>One thing that happened: who did what, to which server.</summary>
public sealed record ActivityEntry(DateTime Time, string Kind, string Text, string? ServerId, string? ServerName, string Who, string Level);

/// <summary>
/// "Activity": a record of what happened (starts, stops, crashes, backups, schedules, installs, settings) and by whom.
/// Kept in activity.jsonl in the data folder; the newest 2000 entries are kept.
/// </summary>
public sealed class ActivityLog
{
    private const int Keep = 2000;
    private readonly object _lock = new();
    private readonly List<ActivityEntry> _entries = new();
    private readonly string _path = Path.Combine(ServerManager.DataDir, "activity.jsonl");

    public event Action<ActivityEntry>? Added;

    public ActivityLog()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var lines = File.ReadAllLines(_path);
            foreach (var l in lines.TakeLast(Keep))
                try { if (JsonSerializer.Deserialize<ActivityEntry>(l, ServerManager.JsonCompact) is { } e) _entries.Add(e); } catch { }
            if (lines.Length > Keep * 2) File.WriteAllLines(_path, lines.TakeLast(Keep)); // trim the file now and then
        }
        catch { }
    }

    public IReadOnlyList<ActivityEntry> Recent(int count = 200, string? serverId = null)
    {
        lock (_lock)
            return _entries.Where(e => serverId == null || e.ServerId == serverId).Reverse().Take(count).ToList();
    }

    /// <param name="kind">start, stop, crash, backup, restore, schedule, install, update, settings, pc, account</param>
    /// <param name="level">info, good, warn or bad</param>
    public void Add(string kind, string text, ServerInstance? server = null, string? who = null, string level = "info")
    {
        var e = new ActivityEntry(DateTime.UtcNow, kind, text, server?.Id, server?.Name, who ?? "Emberkeep", level);
        lock (_lock)
        {
            _entries.Add(e);
            if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
            try { File.AppendAllText(_path, JsonSerializer.Serialize(e, ServerManager.JsonCompact) + "\n"); } catch { }
        }
        try { Added?.Invoke(e); } catch { }
    }
}

/// <summary>
/// Watches the PC's public IP. When it changes, friends outside the home need the new address, so it goes in the
/// activity log (and from there to notifications and the Discord updates channel). Checked every 5 minutes.
/// </summary>
public sealed class PublicAddressWatcher
{
    private readonly ServerManager _mgr;
    private int _started;
    public PublicAddressWatcher(ServerManager mgr) { _mgr = mgr; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var every = TimeSpan.FromSeconds(int.TryParse(AppEnv.Get("ADDRESS_CHECK_SECONDS"), out var s) && s > 0 ? s : 300);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, every.TotalSeconds)));
            while (true)
            {
                try { await CheckAsync(); } catch { }
                await Task.Delay(every);
            }
        });
    }

    public async Task CheckAsync()
    {
        var ip = await NetworkInfo.PublicIpAsync(fresh: true);
        if (ip == null) return; // offline: try again later
        var old = _mgr.Settings.LastPublicIp;
        if (old == ip) return;
        _mgr.Settings.LastPublicIp = ip;
        _mgr.Save();
        if (old.Length > 0)
            _mgr.Activity.Add("network", $"The public IP changed from {old} to {ip}. Friends outside your home need the new address.", null, null, "warn");
    }
}

/// <summary>
/// Tells you about activity you care about: Windows notifications from the tray icon, and/or a Discord channel webhook.
/// Which kinds are sent is set in App settings → Notifications.
/// </summary>
public sealed class Notifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ServerManager _mgr;
    private readonly SemaphoreSlim _send = new(1, 1);

    /// <summary>Raised for a Windows notification: (title, text, isProblem). The desktop window shows it from the tray.</summary>
    public event Action<string, string, bool>? Windows;

    public Notifier(ServerManager mgr)
    {
        _mgr = mgr;
        mgr.Activity.Added += OnActivity;
    }

    private bool Wanted(ActivityEntry e)
    {
        var n = _mgr.Settings.Notifications;
        return e.Kind switch
        {
            "crash" or "crashloop" => n.Crashes,
            "network" => true,
            "start" or "stop" => n.StartStop,
            "backup" or "restore" => n.Backups || e.Level == "bad",
            "install" or "update" => n.Updates,
            "schedule" => n.Schedules || e.Level == "bad",
            "pc" => true,
            _ => false,
        };
    }

    private void OnActivity(ActivityEntry e)
    {
        if (!Wanted(e)) return;
        var n = _mgr.Settings.Notifications;
        var title = e.ServerName ?? "Emberkeep";
        if (n.Windows) try { Windows?.Invoke(title, e.Text, e.Level is "bad" or "warn"); } catch { }
        if (IsWebhook(n.WebhookUrl)) _ = SendAsync(n.WebhookUrl, $"{Icon(e.Level)} **{title}**: {e.Text}");
    }

    private static string Icon(string level) => level switch { "bad" => "🔴", "warn" => "🟠", "good" => "🟢", _ => "🔵" };

    /// <summary>Only Discord webhook URLs are accepted, so the setting can't be used to make Emberkeep call anything else.</summary>
    public static bool IsWebhook(string? url) =>
        !string.IsNullOrWhiteSpace(url) && (url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase)
            || (AppEnv.Get("WEBHOOK_TEST") is { Length: > 0 } t && url.StartsWith(t, StringComparison.OrdinalIgnoreCase)));

    public async Task<string?> SendAsync(string url, string content)
    {
        await _send.WaitAsync();
        try
        {
            using var res = await Http.PostAsJsonAsync(url, new { content, username = "Emberkeep" });
            await Task.Delay(1200); // stay well inside Discord's webhook rate limit
            return res.IsSuccessStatusCode ? null : $"Discord answered {(int)res.StatusCode}.";
        }
        catch (Exception ex) { return ex.Message; }
        finally { _send.Release(); }
    }
}
