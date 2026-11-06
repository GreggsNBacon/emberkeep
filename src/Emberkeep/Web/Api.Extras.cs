using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Emberkeep.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Emberkeep.Web;

/// <summary>Experimental features: backups, schedules, activity, notifications, history, duplicate, export/import.</summary>
public static partial class Api
{
    private static ServerManager? Mgr;

    /// <summary>The name shown in the activity log for whoever made this request.</summary>
    private static string Actor(HttpContext ctx) => WebHost.Who(ctx) is var p && p.IsLocal ? "Host" : p.Username;

    private static void MapExtras(RouteGroupBuilder api, ServerManager mgr, WebHost host)
    {
        // ---------- backups ----------

        api.MapGet("/servers/{id}/backups", (string id) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var s = rt.Config;
            return Results.Ok(new
            {
                folder = host.Backups.DirFor(s),
                busy = host.Backups.Busy(id),
                custom = s.BackupFolders != null,
                folders = s.BackupFolders,
                suggested = GameExtras.For(s.GameId).SaveFolders,
                items = BackupService.Items(s).Select(i => new { i.Configured, i.Resolved, i.IsDir, exists = i.IsDir ? Directory.Exists(i.Resolved) : File.Exists(i.Resolved) }),
                keep = s.BackupKeep,
                onStop = s.BackupOnStop,
                backups = host.Backups.List(s),
                canSaveFirst = GameExtras.For(s.GameId).SaveCommands is { Length: > 0 },
            });
        }).See();

        api.MapPost("/servers/{id}/backups", async (string id, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            try { return Results.Ok(await host.Backups.CreateAsync(rt, "manual", Actor(ctx))); }
            catch (Exception ex) { return Error(ex.Message); }
        }).Need(Permissions.Control);

        api.MapPut("/servers/{id}/backups/settings", (string id, BackupSettingsRequest req) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var next = Clone(rt.Config);
            next.BackupFolders = req.Folders?.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            next.BackupKeep = Math.Clamp(req.Keep ?? next.BackupKeep, 1, 500);
            next.BackupOnStop = req.OnStop ?? next.BackupOnStop;
            mgr.Update(id, next);
            return Results.Ok();
        }).Need(Permissions.Settings);

        api.MapPost("/servers/{id}/backups/{name}/restore", async (string id, string name, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            try { await host.Backups.RestoreAsync(rt, name, Actor(ctx)); return Results.Ok(); }
            catch (Exception ex) { return Error(ex.Message); }
        }).Need(Permissions.Settings);

        api.MapDelete("/servers/{id}/backups/{name}", (string id, string name) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            try { host.Backups.Delete(rt.Config, name); return Results.Ok(); }
            catch (Exception ex) { return Error(ex.Message); }
        }).Need(Permissions.Settings);

        // Opens the backup folder in File Explorer (host PC only, like Browse files).
        api.MapPost("/servers/{id}/backups/browse", (string id, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!WebHost.Who(ctx).IsLocal) return Error("This only works on the PC that runs Emberkeep.");
            if (!Platform.IsWindows) return Error("This opens File Explorer, which is only on Windows.");
            var dir = host.Backups.DirFor(rt.Config);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            return Results.Ok();
        }).See();

        // ---------- schedules ----------

        api.MapGet("/servers/{id}/schedules", (string id) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var now = DateTime.Now;
            return Results.Ok(new
            {
                tasks = rt.Config.Schedules.Select(t => new { task = t, next = Scheduler.Next(t, now)?.ToUniversalTime() }),
                canWarn = GameExtras.For(rt.Config.GameId).Broadcast != null,
                canCommand = rt.Config.CommandChannel != CommandChannel.None,
                canUpdate = rt.Config.SteamCmdAppId != null,
            });
        }).See();

        api.MapPut("/servers/{id}/schedules", (string id, List<ScheduledTask> tasks, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var allowed = new[] { "restart", "stop", "start", "backup", "command", "update" };
            foreach (var t in tasks)
            {
                if (!allowed.Contains(t.Action)) return Error("Unknown task.");
                if (t.Kind is not ("daily" or "interval")) return Error("Unknown schedule.");
                if (t.Kind == "daily" && !TimeOnly.TryParse(t.Time, out _)) return Error("Pick a time like 05:00.");
                t.EveryMinutes = Math.Clamp(t.EveryMinutes, 5, 7 * 24 * 60);
                t.Days = t.Days.Where(d => d is >= 0 and <= 6).Distinct().ToList();
                if (string.IsNullOrWhiteSpace(t.Id)) t.Id = Guid.NewGuid().ToString("N")[..8];
            }
            // Scheduled console commands need the console permission, like typing them.
            bool commandsChanged = !tasks.Where(t => t.Action == "command").Select(t => t.Command)
                .SequenceEqual(rt.Config.Schedules.Where(t => t.Action == "command").Select(t => t.Command));
            if (commandsChanged && !WebHost.Who(ctx).Can(Permissions.Console, id)) return Error("Scheduling console commands needs the console permission.", 403);
            var next = Clone(rt.Config);
            // Keep when each task last ran.
            foreach (var t in tasks)
                if (rt.Config.Schedules.FirstOrDefault(o => o.Id == t.Id) is { } old) { t.LastRun = old.LastRun; t.LastResult = old.LastResult; }
            next.Schedules = tasks;
            mgr.Update(id, next);
            mgr.Activity.Add("settings", $"Schedule changed ({tasks.Count} task{(tasks.Count == 1 ? "" : "s")}).", next, Actor(ctx));
            return Results.Ok();
        }).Need(Permissions.Settings);

        api.MapPost("/servers/{id}/schedules/{taskId}/run", async (string id, string taskId, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            var t = rt?.Config.Schedules.FirstOrDefault(x => x.Id == taskId);
            if (rt == null || t == null) return Results.NotFound();
            if (t.Action == "command" && !WebHost.Who(ctx).Can(Permissions.Console, id)) return Error("Running console commands needs the console permission.", 403);
            return Results.Ok(new { result = await host.Scheduler.RunAsync(rt, t, Actor(ctx)) });
        }).Need(Permissions.Control);

        // ---------- history, activity ----------

        api.MapGet("/servers/{id}/history", (string id) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            return Results.Ok(rt.History.Select(h => new { t = h.T, cpu = Math.Round(h.Cpu, 1), mem = h.Mem }));
        }).See();

        api.MapGet("/activity", (string? server, int? count, HttpContext ctx) =>
        {
            var p = WebHost.Who(ctx);
            var list = mgr.Activity.Recent(Math.Clamp(count ?? 100, 1, 1000), server);
            // Accounts only see activity of the servers they can see; app-wide entries need full access.
            return Results.Ok(list.Where(e => e.ServerId != null ? p.CanSee(e.ServerId) : p.Full));
        });

        // ---------- notifications ----------

        api.MapPut("/notifications", (NotificationSettings n, HttpContext ctx) =>
        {
            n.WebhookUrl = n.WebhookUrl?.Trim() ?? "";
            if (n.WebhookUrl.Length > 0 && !Notifier.IsWebhook(n.WebhookUrl))
                return Error("Paste a Discord webhook URL (https://discord.com/api/webhooks/…), or leave it empty.");
            mgr.Settings.Notifications = n;
            mgr.Save();
            return Results.Ok(n);
        }).Full();

        api.MapPost("/notifications/test", async () =>
        {
            var n = mgr.Settings.Notifications;
            mgr.Activity.Add("pc", "This is a test notification from Emberkeep.", null, null, "info");
            if (!Notifier.IsWebhook(n.WebhookUrl)) return Results.Ok(new { webhook = (string?)null });
            var err = await host.Notifier.SendAsync(n.WebhookUrl, "🔔 Test notification from **Emberkeep**.");
            return err == null ? Results.Ok(new { webhook = "sent" }) : Error("Discord didn't accept it: " + err);
        }).Full();

        // ---------- duplicate ----------

        api.MapPost("/servers/{id}/duplicate", (string id, DuplicateRequest req, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!WebHost.Who(ctx).AllServers) return Error("Your group only sees some servers, so it can't add new ones.", 403);
            var name = string.IsNullOrWhiteSpace(req.Name) ? rt.Config.Name + " (copy)" : req.Name.Trim();
            var dir = string.IsNullOrWhiteSpace(req.Dir) ? rt.Config.InstallDir : Path.GetFullPath(req.Dir.Trim());
            if (req.CopyFiles)
            {
                if (dir.Equals(Path.GetFullPath(rt.Config.InstallDir), StringComparison.OrdinalIgnoreCase)) return Error("Choose a different folder to copy the files into.");
                if (rt.State != ServerState.Stopped) return Error("Stop the server before copying its files.");
                try { return Results.Ok(new { job = JobDto(host.Installer.Copy(rt, dir, name)) }); }
                catch (InvalidOperationException ex) { return Error(ex.Message); }
            }
            if (!Directory.Exists(dir)) return Error("That folder doesn't exist.");
            var copy = Clone(rt.Config);
            copy.Id = Guid.NewGuid().ToString("N")[..12];
            copy.Name = name;
            copy.InstallDir = dir;
            copy.AddedAt = DateTime.UtcNow;
            foreach (var t in copy.Schedules) { t.Id = Guid.NewGuid().ToString("N")[..8]; t.LastRun = null; t.LastResult = null; }
            var added = mgr.Add(copy);
            mgr.Activity.Add("settings", $"Added as a copy of {rt.Config.Name}.", copy, Actor(ctx));
            return Results.Ok(new { server = Detail(added, WebHost.Who(ctx)) });
        }).Need(Permissions.Launch);

        // ---------- export / import ----------

        // The server list (with their settings and schedules) as a file, e.g. to move Emberkeep to another PC.
        api.MapGet("/export", (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"emberkeep-servers-{DateTime.Now:yyyy-MM-dd}.json\"";
            return Results.Json(new { emberkeep = 1, exported = DateTime.UtcNow, servers = mgr.Settings.Servers }, ServerManager.Json);
        }).Full();

        api.MapPost("/import", (ImportRequest req, HttpContext ctx) =>
        {
            if (req.Servers == null) return Error("That file doesn't have any servers in it.");
            var have = mgr.Settings.Servers.Select(s => Path.GetFullPath(s.InstallDir).TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0, skipped = 0;
            foreach (var s in req.Servers)
            {
                if (string.IsNullOrWhiteSpace(s.InstallDir) || have.Contains(Path.GetFullPath(s.InstallDir).TrimEnd('\\'))) { skipped++; continue; }
                s.Id = Guid.NewGuid().ToString("N")[..12];
                s.AddedAt = DateTime.UtcNow;
                foreach (var t in s.Schedules) t.LastRun = null;
                mgr.Add(s);
                have.Add(Path.GetFullPath(s.InstallDir).TrimEnd('\\'));
                added++;
            }
            mgr.Activity.Add("settings", $"Imported {added} server{(added == 1 ? "" : "s")}{(skipped > 0 ? $" ({skipped} already here)" : "")}.", null, Actor(ctx));
            return Results.Ok(new { added, skipped });
        }).Full();
    }

    private static ServerInstance Clone(ServerInstance s) =>
        JsonSerializer.Deserialize<ServerInstance>(JsonSerializer.Serialize(s, ServerManager.Json), ServerManager.Json)!;

    /// <summary>Other servers set to the same join port, so friends might reach the wrong one (or one fails to start).</summary>
    private static List<object> PortConflicts(ServerManager mgr, ServerRuntime rt)
    {
        var mine = JoinInfo.Resolve(rt.Config).Port;
        if (mine is not int port) return new();
        return mgr.Runtimes.Where(o => o != rt && JoinInfo.Resolve(o.Config).Port == port)
            .Select(o => (object)new { port, id = o.Config.Id, name = o.Config.Name, running = o.State != ServerState.Stopped }).ToList();
    }

    private sealed record BackupSettingsRequest(List<string>? Folders, int? Keep, bool? OnStop);
    private sealed record DuplicateRequest(string? Name, string? Dir, bool CopyFiles);
    private sealed record ImportRequest(List<ServerInstance>? Servers);
}
