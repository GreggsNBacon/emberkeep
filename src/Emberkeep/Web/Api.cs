using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberkeep.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Emberkeep.Web;

/// <summary>
/// The REST API. Every endpoint states what it needs, and the check runs on the server before the handler:
/// hiding a button in the page is only a convenience, never the protection.
///   .See()        the signed-in account's group can see this server
///   .Need(perm)   ...and has that permission (for app-wide actions: has it at all)
///   .Full()       full access only (accounts, app settings)
/// </summary>
public static partial class Api
{
    public static void Map(WebApplication app, ServerManager mgr, WebHost host)
    {
        var api = app.MapGroup("/api");
        Mgr = mgr;

        // ---------- who am I ----------

        api.MapGet("/me", (HttpContext ctx) =>
        {
            var p = WebHost.Who(ctx);
            return Results.Ok(new
            {
                username = p.Username, isLocal = p.IsLocal, full = p.Full,
                permissions = p.Full ? Permissions.All.Select(a => a.Id).ToList() : p.Perms.ToList(),
                allServers = p.AllServers,
                canAddServers = p.CanAnywhere(Permissions.Launch) && p.AllServers,
            });
        });

        api.MapPost("/me/password", (HttpContext ctx, ChangePasswordRequest req) =>
        {
            var p = WebHost.Who(ctx);
            try
            {
                host.Auth.ChangeOwnPassword(p, req.Current ?? "", req.Next ?? "");
                // The change signed this account out everywhere, including here; hand back a fresh session.
                if (host.Auth.RenewSession(p) is { } sid) WebHost.SetSessionCookie(ctx, sid);
                host.ApplyForwarding();
                return Results.Ok();
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { return Error(ex.Message); }
        });

        // ---------- catalog & state ----------

        api.MapGet("/games", () => GameCatalog.All.Select(GameDto));

        api.MapGet("/servers", (HttpContext ctx) => Visible(mgr, ctx).Select(Summary));

        api.MapGet("/servers/{id}", (string id, HttpContext ctx) =>
            mgr.Get(id) is { } rt ? Results.Ok(Detail(rt, WebHost.Who(ctx))) : Results.NotFound()).See();

        api.MapPost("/servers", (CreateServerRequest req, HttpContext ctx) =>
        {
            if (!WebHost.Who(ctx).AllServers) return Error("Your group only sees some servers, so it can't add new ones.", 403);
            if (string.IsNullOrWhiteSpace(req.InstallDir) || !Directory.Exists(req.InstallDir))
                return Error("That folder doesn't exist.");
            var game = GameCatalog.Get(req.GameId ?? "generic");
            var inst = GameCatalog.CreateInstance(game, Path.GetFullPath(req.InstallDir), string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim(), req.SteamAppId);
            var rt = mgr.Add(inst);
            _ = ArtCache.AutoDetectAsync(mgr, inst.Id); // custom servers: find the game's artwork by name
            return Results.Ok(Detail(rt, WebHost.Who(ctx)));
        }).Need(Permissions.Launch);

        api.MapPut("/servers/{id}", (string id, ServerInstance body, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var p = WebHost.Who(ctx);
            bool canSettings = p.Can(Permissions.Settings, id) || p.Can(Permissions.Launch, id);
            bool canLaunch = p.Can(Permissions.Launch, id);
            if (!canSettings) return Forbidden();

            // Start from what's saved and copy over only the fields this account may change; anything else in
            // the request is ignored, so a crafted request can't slip a launch change through.
            var cur = rt.Config;
            var next = JsonSerializer.Deserialize<ServerInstance>(JsonSerializer.Serialize(cur, ServerManager.Json), ServerManager.Json)!;
            if (string.IsNullOrWhiteSpace(body.Name)) return Error("The server needs a name.");
            next.Name = body.Name.Trim();
            next.AutoRestart = body.AutoRestart;
            next.Notes = body.Notes ?? "";
            next.UpdateBeforeStart = body.UpdateBeforeStart;
            next.StartWithEmberkeep = body.StartWithEmberkeep;
            next.CommandChannel = body.CommandChannel;
            next.StopMethod = body.StopMethod;
            next.StopCommands = body.StopCommands ?? new();
            next.StopTimeoutSeconds = Math.Clamp(body.StopTimeoutSeconds, 5, 900);
            next.Rcon = body.Rcon ?? new();
            next.RconFromConfig = body.RconFromConfig;
            next.JoinPort = body.JoinPort;
            next.ArtAppId = body.ArtAppId;
            next.ForwardWhileRunning = body.ForwardWhileRunning;
            next.ForwardPorts = body.ForwardPorts;
            if (!string.IsNullOrWhiteSpace(next.ForwardPorts))
            {
                try { next.ForwardPorts = RouterPorts.Format(RouterPorts.Parse(next.ForwardPorts)); } // tidy: "udp 1-2,tcp 3" -> "UDP 1-2, TCP 3"
                catch (FormatException ex) { return Error($"Router ports: {ex.Message}"); }
            }
            if (canLaunch)
            {
                // These decide what runs and which files are read: they can run any program on this PC.
                next.GameId = body.GameId;
                next.InstallDir = body.InstallDir;
                next.Executable = body.Executable;
                next.Arguments = body.Arguments;
                next.WorkingDirectory = body.WorkingDirectory;
                next.Launcher = body.Launcher ?? "";
                next.Environment = body.Environment ?? new();
                next.ExtraLogFiles = body.ExtraLogFiles ?? new();
                next.SteamAppId = body.SteamAppId;
            }
            mgr.Update(id, next);
            return Results.Ok(Detail(rt, p));
        }).See();

        // ---------- learn from launcher (launch permission: it opens programs and sets what runs) ----------

        api.MapGet("/servers/{id}/learn", (string id) => Results.Ok(host.Learn.Status(id))).Need(Permissions.Launch);

        api.MapPost("/servers/{id}/learn", (string id) =>
        {
            try { host.Learn.Start(id); return Results.Ok(host.Learn.Status(id)); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        api.MapPost("/servers/{id}/learn/apply", (string id, HttpContext ctx) =>
        {
            try { host.Learn.Apply(id); return Results.Ok(Detail(mgr.Get(id)!, WebHost.Who(ctx))); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        api.MapDelete("/servers/{id}/learn", (string id) => { host.Learn.Cancel(id); return Results.Ok(); }).Need(Permissions.Launch);

        api.MapDelete("/servers/{id}", (string id) =>
        {
            if (host.Installer.Busy(id)) return Error("The server's files are being updated. Cancel that first.");
            try { mgr.Remove(id); host.Auth.ForgetServer(id); mgr.Save(); return Results.Ok(); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        // "Browse files": open the server's folder in File Explorer. Only from this PC: from anywhere else it would open
        // a window on the host where nobody is looking.
        api.MapPost("/servers/{id}/browse", (string id, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote == null || !System.Net.IPAddress.IsLoopback(remote)) return Error("Browse files only works on the PC that runs Emberkeep.");
            var dir = rt.Config.InstallDir;
            if (!Directory.Exists(dir)) return Error($"The server folder can't be found: {dir}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            return Results.Ok();
        }).See();

        // "Check files again": look at the folder afresh, e.g. after the server finished downloading.
        api.MapPost("/servers/{id}/recheck", (string id, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (rt.State != ServerState.Stopped) return Error("Stop the server before checking its files again.");
            try
            {
                var (next, changes) = ServerRecheck.Run(rt);
                if (changes.Count > 0) mgr.Update(id, next);
                _ = ArtCache.AutoDetectAsync(mgr, id); // a newly recognised game may now have artwork
                return Results.Ok(new { changes, executableFound = ServerRecheck.ExecutableFound(rt), detail = Detail(rt, WebHost.Who(ctx)) });
            }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        api.MapPost("/servers/reorder", (List<string> ids) => { mgr.Reorder(ids); return Results.Ok(); }).Full();

        // ---------- install servers with SteamCMD ----------

        // Status for the Install tab: is SteamCMD set up, and where new servers go by default.
        api.MapGet("/install", (HttpContext ctx) => new
        {
            steamCmdInstalled = SteamCmd.IsInstalled(mgr.Settings),
            steamCmdPath = SteamCmd.ExePath(mgr.Settings),
            ownSteamCmd = string.IsNullOrWhiteSpace(mgr.Settings.SteamCmdPath) && AppEnv.Get("STEAMCMD") == null,
            installRoot = Path.GetDirectoryName(host.Installer.SuggestedDir("x")) ?? "",
            jobs = host.Installer.Jobs.Select(JobDto),
        }).Need(Permissions.Launch);

        api.MapGet("/install/suggest", (string title) => new { dir = host.Installer.SuggestedDir(title ?? "Server") }).Need(Permissions.Launch);

        // Free space on the drive a folder is on, so the Install tab can warn before a big download.
        api.MapGet("/install/space", (string dir) =>
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (root == null) return Results.Ok(new { freeBytes = (long?)null });
                var d = new DriveInfo(root);
                return Results.Ok(new { freeBytes = (long?)(d.IsReady ? d.AvailableFreeSpace : null) });
            }
            catch { return Results.Ok(new { freeBytes = (long?)null }); }
        }).Need(Permissions.Launch);

        // "Other Steam server": looks an app ID up through SteamCMD so the user sees its name before downloading.
        api.MapGet("/install/appinfo/{appId:int}", async (int appId, CancellationToken ct) =>
        {
            try
            {
                await SteamCmd.EnsureAsync(mgr.Settings, _ => { }, ct);
                var name = await SteamCmd.AppNameAsync(mgr.Settings, appId, ct);
                var known = GameCatalog.BySteamAppId(appId);
                return name == null ? Error($"Steam doesn't know an app with ID {appId}.")
                    : Results.Ok(new { appId, name, gameId = known?.Id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        api.MapPost("/install", (InstallRequest req, HttpContext ctx) =>
        {
            if (!WebHost.Who(ctx).AllServers) return Error("Your group only sees some servers, so it can't add new ones.", 403);
            int appId = req.AppId ?? 0;
            string title = req.Name ?? "";
            if (req.GameId is { } gid)
            {
                var g = GameCatalog.Get(gid);
                if (g.Id == "generic" || g.SteamAppIds.Length == 0) return Error("Emberkeep can't download that game's server.");
                if (!g.SteamCmdAnonymous) return Error($"{g.Name}'s server comes with the game, so install it through Steam, then add its folder.");
                appId = g.SteamAppIds[0];
                if (string.IsNullOrWhiteSpace(title)) title = g.Name;
            }
            try { return Results.Ok(JobDto(host.Installer.Install(appId, string.IsNullOrWhiteSpace(title) ? $"App {appId}" : title.Trim(), req.Dir ?? "", req.GameId, req.Name))); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        api.MapPost("/install/{jobId}/cancel", (string jobId) =>
            host.Installer.Cancel(jobId) ? Results.Ok() : Error("That download has already finished.")).Need(Permissions.Launch);
        api.MapPost("/install/clear", () => { host.Installer.ClearFinished(); return Results.Ok(); }).Need(Permissions.Launch);

        api.MapPost("/servers/{id}/update-files", (string id) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            try { return Results.Ok(JobDto(host.Installer.Update(rt))); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).Need(Permissions.Launch);

        // ---------- control ----------

        api.MapPost("/servers/{id}/start", (string id, HttpContext ctx) => host.Installer.Busy(id) ? Error("The server's files are being updated. Start it when that finishes.")
            : Control(mgr, id, rt => { rt.Actor = Actor(ctx); _ = host.Installer.StartAsync(rt); })).Need(Permissions.Control);
        api.MapPost("/servers/{id}/stop", (string id, HttpContext ctx) => Control(mgr, id, rt => { rt.Actor = Actor(ctx); _ = rt.StopAsync(); })).Need(Permissions.Control);
        api.MapPost("/servers/{id}/restart", (string id, HttpContext ctx) => host.Installer.Busy(id) ? Error("The server's files are being updated. Start it when that finishes.")
            : Control(mgr, id, rt => { rt.Actor = Actor(ctx); _ = rt.RestartAsync(); })).Need(Permissions.Control);
        MapExtras(api, mgr, host);
        api.MapPost("/servers/{id}/kill", (string id) => Control(mgr, id, rt => rt.Kill())).Need(Permissions.Kill);
        api.MapPost("/servers/{id}/logs/clear", (string id) => Control(mgr, id, rt => rt.Logs.Clear())).Need(Permissions.Console);

        api.MapPost("/servers/{id}/command", async (string id, CommandRequest req, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            try
            {
                rt.Logs.Add("system", $"Command from {WebHost.Who(ctx).Username}:");
                var reply = await rt.SendCommandAsync(req.Command ?? "");
                return Results.Ok(new { reply });
            }
            catch (Exception ex)
            {
                rt.Logs.Add("error", ex.Message);
                return Error(ex.Message);
            }
        }).Need(Permissions.Console);

        // ---------- live streams (Server-Sent Events) ----------
        // Streams re-check the session on every update, so a signed-out or disabled account stops receiving data.

        api.MapGet("/events", async (HttpContext ctx) =>
        {
            var cookie = AuthService.SessionCookie(ctx);
            PrepareSse(ctx);
            using var signal = new SemaphoreSlim(0);
            void OnChange() { if (signal.CurrentCount == 0) signal.Release(); }
            mgr.Changed += OnChange;
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    var who = host.Auth.Resolve(cookie);
                    if (who == null) break;
                    var payload = JsonSerializer.Serialize(mgr.Runtimes.Where(r => who.CanSee(r.Config.Id)).Select(Summary), ServerManager.JsonCompact);
                    await ctx.Response.WriteAsync($"event: servers\ndata: {OneLine(payload)}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await signal.WaitAsync(TimeSpan.FromSeconds(2), ctx.RequestAborted);
                    await Task.Delay(100, ctx.RequestAborted); // coalesce bursts
                }
            }
            catch (OperationCanceledException) { }
            finally { mgr.Changed -= OnChange; }
        });

        api.MapGet("/servers/{id}/logs", async (string id, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) { ctx.Response.StatusCode = 404; return; }
            var cookie = AuthService.SessionCookie(ctx);
            PrepareSse(ctx);
            var (backlog, ch) = rt.Logs.Subscribe();
            try
            {
                await WriteEvent(ctx, "backlog", backlog);
                var batch = new List<LogLine>();
                while (await ch.Reader.WaitToReadAsync(ctx.RequestAborted))
                {
                    if (host.Auth.Resolve(cookie) is not { } who || !who.CanSee(id)) break;
                    batch.Clear();
                    while (batch.Count < 500 && ch.Reader.TryRead(out var line)) batch.Add(line);
                    await WriteEvent(ctx, "lines", batch);
                    await Task.Delay(50, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally { rt.Logs.Unsubscribe(ch); }
        }).See();

        // ---------- config files (often hold admin passwords, so they need their own permission) ----------

        api.MapGet("/servers/{id}/configs", (string id) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var files = ConfigDiscovery.Discover(rt.Config);
            var missing = ConfigDiscovery.MissingTemplate(rt.Config);
            return Results.Ok(new
            {
                files,
                template = missing is { } m ? new { source = m.Source, destination = m.Destination } : null,
            });
        }).Need(Permissions.Configs);

        api.MapGet("/servers/{id}/configs/file", (string id, string path) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!ConfigDiscovery.IsAllowed(rt.Config, path)) return Error("That file isn't one of this server's config files.", 403);
            var (text, enc, newline) = ConfigFiles.Read(path);
            var fi = new FileInfo(path);
            return Results.Ok(new
            {
                path = fi.FullName,
                content = text,
                format = ConfigDiscovery.DetectFormat(path, text),
                encoding = enc.WebName,
                newline = newline == "\r\n" ? "CRLF" : "LF",
                modified = fi.LastWriteTimeUtc,
                running = rt.State != ServerState.Stopped,
            });
        }).Need(Permissions.Configs);

        api.MapPut("/servers/{id}/configs/file", (string id, SaveFileRequest req, HttpContext ctx) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (req.Path == null || !ConfigDiscovery.IsAllowed(rt.Config, req.Path)) return Error("That file isn't one of this server's config files.", 403);
            var fi = new FileInfo(req.Path);
            if (req.ExpectedModified is DateTime exp && fi.Exists && Math.Abs((fi.LastWriteTimeUtc - exp.ToUniversalTime()).TotalMilliseconds) > 2 && !req.Force)
                return Results.Json(new { error = "The file was changed on disk since you opened it.", conflict = true }, statusCode: 409);
            try
            {
                var backup = ConfigFiles.Write(rt.Config, req.Path, req.Content ?? "");
                rt.Logs.Add("system", $"{WebHost.Who(ctx).Username} saved {Path.GetFileName(req.Path)}.");
                return Results.Ok(new { modified = new FileInfo(req.Path).LastWriteTimeUtc, backup });
            }
            catch (Exception ex) { return Error($"Couldn't save: {ex.Message}"); }
        }).Need(Permissions.Configs);

        api.MapGet("/servers/{id}/configs/backups", (string id, string path) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!ConfigDiscovery.IsAllowed(rt.Config, path)) return Error("Not allowed.", 403);
            return Results.Ok(ConfigFiles.ListBackups(rt.Config, path).Select(b => new { b.Name, b.Created, b.Size }));
        }).Need(Permissions.Configs);

        api.MapGet("/servers/{id}/configs/backup", (string id, string path, string backup) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!ConfigDiscovery.IsAllowed(rt.Config, path)) return Error("Not allowed.", 403);
            var file = ConfigFiles.ListBackups(rt.Config, path).FirstOrDefault(b => b.Name == backup);
            if (file == null) return Results.NotFound();
            var (text, _, _) = ConfigFiles.Read(file.FullPath);
            return Results.Ok(new { content = text });
        }).Need(Permissions.Configs);

        api.MapPost("/servers/{id}/configs/from-template", (string id) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            var t = ConfigDiscovery.MissingTemplate(rt.Config);
            if (t == null) return Error("There's no default config to copy.");
            var (src, dst) = t.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst)) ConfigFiles.Backup(rt.Config, dst);
            // Palworld's defaults file has a long comment header; the server only reads the settings section.
            var text = File.ReadAllText(src);
            int idx = text.IndexOf("[/Script/", StringComparison.Ordinal);
            File.WriteAllText(dst, idx > 0 ? text[idx..] : text);
            return Results.Ok(new { path = dst });
        }).Need(Permissions.Configs);

        // ---------- discovery (browsing the host's disks: launch permission) ----------

        api.MapGet("/scan/steam", () =>
        {
            var root = SteamScanner.FindSteamRoot();
            return Results.Ok(new
            {
                steamFound = root != null,
                steamRoot = root,
                libraries = root != null ? SteamScanner.FindLibraries(root) : new List<string>(),
                candidates = SteamScanner.Scan(mgr.Settings.Servers),
            });
        }).Need(Permissions.Launch);

        api.MapGet("/scan/folder", (string path) =>
        {
            if (!Directory.Exists(path)) return Error("That folder doesn't exist.");
            return Results.Ok(SteamScanner.ScanFolder(Path.GetFullPath(path), mgr.Settings.Servers));
        }).Need(Permissions.Launch);

        api.MapGet("/fs", (string? path, bool? files) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new FsEntry(d.Name, d.RootDirectory.FullName, true));
                    return Results.Ok(new { path = "", parent = (string?)null, entries = drives });
                }
                var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(path));
                if (!dir.Exists) return Error("Folder not found.");
                var entries = dir.EnumerateDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && !d.Attributes.HasFlag(FileAttributes.System))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new FsEntry(d.Name, d.FullName, true)).ToList();
                if (files == true)
                {
                    entries.AddRange(dir.EnumerateFiles()
                        .Where(f => f.Extension is ".exe" or ".bat" or ".cmd" or ".jar" or ".EXE" or ".BAT")
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(f => new FsEntry(f.Name, f.FullName, false)));
                }
                return Results.Ok(new { path = dir.FullName, parent = dir.Parent?.FullName ?? "", entries });
            }
            catch (UnauthorizedAccessException) { return Error("Access to that folder is denied."); }
        }).Need(Permissions.Launch);

        // ---------- network, artwork, join address ----------

        // This PC's addresses for "Copy address" and "Fill current IP". The public IP is only
        // looked up (via ipify.org) when the UI asks for it.
        api.MapGet("/network", async (bool? includePublic) => new
        {
            local = NetworkInfo.LocalAddresses(),
            publicIp = includePublic == true ? await NetworkInfo.PublicIpAsync() : null,
        });

        // Game artwork, cached from Steam's CDN. Long browser caching: the image for an app ID never changes.
        api.MapGet("/art/{appId:int}/{kind}", async (int appId, string kind, HttpContext ctx) =>
        {
            var path = await ArtCache.GetAsync(appId, kind);
            if (path == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(path, path.EndsWith(".png") ? "image/png" : "image/jpeg");
        });

        api.MapGet("/art/search", async (string q) =>
        {
            try { return Results.Ok(await ArtCache.SearchAsync(q)); }
            catch { return Error("Couldn't reach the Steam store. Check your internet connection."); }
        }).Need(Permissions.Settings);

        api.MapGet("/servers/{id}/join", (string id) =>
            mgr.Get(id) is { } rt ? Results.Ok(JoinInfo.Resolve(rt.Config)) : Results.NotFound()).See();

        // ---------- router ports (through Watchfire when available) ----------

        api.MapGet("/servers/{id}/ports", async (string id, bool? refresh) =>
            mgr.Get(id) is { } rt ? Results.Ok(await host.Ports.StatusAsync(rt.Config, refresh == true)) : Results.NotFound()).See();

        api.MapPost("/servers/{id}/ports/open", async (string id) =>
            mgr.Get(id) is { } rt ? Results.Ok(await host.Ports.OpenAsync(rt.Config)) : Results.NotFound()).Need(Permissions.Ports);

        api.MapPost("/servers/{id}/ports/close", async (string id) =>
            mgr.Get(id) is { } rt ? Results.Ok(await host.Ports.CloseAsync(rt.Config)) : Results.NotFound()).Need(Permissions.Ports);

        api.MapGet("/watchfire", async () => Results.Ok(await WatchfireClient.InfoAsync()));

        // Opens a window on the host PC itself.
        api.MapPost("/watchfire/show", async () =>
            await WatchfireClient.ShowAsync() ? Results.Ok() : Error("Watchfire isn't installed on this PC.")).Full();

        // ---------- updates (full access) ----------

        api.MapGet("/update", () => UpdateDto(mgr, host)).Full();
        api.MapPost("/update/check", async () => { await host.Updates.CheckAsync(); return Results.Ok(UpdateDto(mgr, host)); }).Full();
        api.MapPost("/update/install", async () =>
        {
            try { await host.Updates.InstallAsync(); return Results.Ok(new { started = true }); }
            catch (Exception ex) { return Error(ex.Message); }
        }).Full();

        // ---------- restart this PC (full access) ----------

        api.MapGet("/pc/restart", () => PcRestartDto(host)).Full();
        api.MapPost("/pc/restart", () => { host.PcRestart.RestartSoon(); return Results.Ok(PcRestartDto(host)); }).Full();
        api.MapPost("/pc/restart/cancel", () =>
            host.PcRestart.Cancel() ? Results.Ok(PcRestartDto(host)) : Error("It's too late to cancel: the servers are already being stopped.")).Full();

        // ---------- app settings (full access) ----------

        api.MapGet("/settings", () => SettingsDto(mgr, host)).Full();

        api.MapPut("/settings", (SettingsRequest req) =>
        {
            var s = mgr.Settings;
            bool needsRestart = false;
            if (req.Port is int port)
            {
                if (port < 1024 || port > 65535) return Error("Pick a port between 1024 and 65535.");
                needsRestart |= port != host.Port;
                s.Port = port;
            }
            if (req.RemoteAccess is bool remote)
            {
                if (remote && !host.Auth.HasAccounts) return Error("Create an account before turning on remote access.");
                needsRestart |= remote != host.ListeningOnLan;
                s.RemoteAccess = remote;
                if (!remote) s.InternetAccess = false; // internet access rides on remote access
            }
            if (req.MinimizeToTray is bool tray) s.MinimizeToTray = tray;
            if (req.StartWithWindows is bool startup)
            {
                try { WindowsStartup.Set(startup); }
                catch (Exception ex) { return Error("Couldn't change Windows startup: " + ex.Message); }
            }
            if (req.DailyRestartTime is string time)
            {
                if (!TimeOnly.TryParse(time, out var t)) return Error("Pick a time for the daily restart, like 05:00.");
                s.DailyRestartTime = t.ToString("HH:mm");
            }
            if (req.DailyRestart is bool daily) s.DailyRestart = daily;
            if (req.SteamCmdPath is string scp)
            {
                scp = scp.Trim().Trim('"');
                if (scp.Length > 0 && (!scp.EndsWith("steamcmd.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(scp)))
                    return Error("Choose steamcmd.exe, or leave the box empty to let Emberkeep set SteamCMD up itself.");
                if (scp.Length > 0 && !SteamCmd.SignedByValve(scp)) return Error("That steamcmd.exe isn't signed by Valve, so Emberkeep won't use it.");
                s.SteamCmdPath = scp;
            }
            if (req.BackupRoot is string broot)
            {
                broot = broot.Trim().Trim('"');
                if (broot.Length > 0 && !Path.IsPathFullyQualified(broot)) return Error("Choose a full folder path for backups.");
                s.BackupRoot = broot;
            }
            if (req.InstallRoot is string root)
            {
                root = root.Trim().Trim('"');
                if (root.Length > 0 && !Path.IsPathFullyQualified(root)) return Error(@"Choose a full folder path, like D:\Game Servers.");
                s.InstallRoot = root;
            }
            if (req.CheckForUpdates is bool updates)
            {
                s.CheckForUpdates = updates;
                if (updates && host.Updates.CheckedAt == null) _ = host.Updates.CheckAsync(); // turned on before the first check ran
            }
            if (req.InternetAccess is bool internet)
            {
                if (internet && !(req.RemoteAccess ?? s.RemoteAccess))
                    return Error("Turn on remote access first.");
                if (internet && !host.Auth.AllPasswordsLong)
                    return Error($"Every enabled account needs a password of at least {AuthService.MinInternetPasswordLength} characters before Emberkeep can face the internet.");
                s.InternetAccess = internet;
            }
            mgr.Save();
            // A restart re-applies forwarding itself; otherwise apply the change now.
            if (needsRestart) _ = Task.Run(async () => { await Task.Delay(300); await host.RestartAsync(); });
            else host.ApplyForwarding();
            return Results.Ok(new { restarting = needsRestart });
        }).Full();

        // ---------- Discord bot (full access) ----------

        api.MapGet("/discord", () => Results.Ok(DiscordDto(mgr, host))).Full();

        api.MapPut("/discord", async (DiscordRequest req) =>
        {
            var d = mgr.Settings.Discord;
            if (req.AddressHost is string ah)
            {
                ah = ah.Trim();
                if (ah.Length > 253 || !System.Text.RegularExpressions.Regex.IsMatch(ah, @"^[A-Za-z0-9.\-:\[\]]*$"))
                    return Error("Enter a host name like play.example.com, or leave it empty to share your public IP.");
                d.AddressHost = ah;
            }
            if (req.AllServers is bool all) d.AllServers = all;
            if (req.ServerIds is { } ids) d.ServerIds = ids.Where(id => mgr.Get(id) != null).Distinct().ToList();
            if (req.ShowOffline is bool off) d.ShowOffline = off;
            if (req.PrivateReplies is bool pr) d.PrivateReplies = pr;
            if (req.Updates is { } u)
            {
                bool Id(string? v) => string.IsNullOrEmpty(v) || v.All(char.IsDigit);
                if (!Id(u.ChannelId) || !Id(u.PingRoleId)) return Error("Pick the channel and role from the lists.");
                d.Updates = new DiscordUpdates
                {
                    ChannelId = u.ChannelId ?? "", ChannelName = u.ChannelName ?? "",
                    PingRoleId = u.PingRoleId ?? "", PingRoleName = u.PingRoleName ?? "",
                    Crashes = u.Crashes, AddressChanges = u.AddressChanges, GameUpdates = u.GameUpdates, Restarts = u.Restarts, StartStop = u.StartStop,
                };
            }
            if (req.Enabled is bool en && en != d.Enabled)
            {
                d.Enabled = en;
                mgr.Save();
                if (en) host.Discord.Start(); else await host.Discord.StopAsync();
            }
            mgr.Save();
            host.Discord.SettingsChanged();
            return Results.Ok(DiscordDto(mgr, host));
        }).Full();

        api.MapPost("/discord/token", async (DiscordTokenRequest req) =>
        {
            try { await host.Discord.SetTokenAsync(req.Token ?? ""); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Error(ex.Message); }
            await Task.Delay(1500); // usually long enough to come online, so the dialog shows it
            return Results.Ok(DiscordDto(mgr, host));
        }).Full();

        api.MapDelete("/discord/token", async () =>
        {
            await host.Discord.ForgetAsync();
            return Results.Ok(DiscordDto(mgr, host));
        }).Full();

        api.MapGet("/discord/channels", async () =>
        {
            try { return Results.Ok(await host.Discord.ChannelsAsync()); }
            catch (DiscordException ex) { return Error($"Discord said: {ex.Message}"); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException) { return Error("Couldn't reach Discord. Check the bot is connected."); }
        }).Full();

        api.MapPost("/discord/board", async (DiscordBoardRequest req) =>
        {
            if (string.IsNullOrEmpty(req.ChannelId)) return Error("Pick a channel.");
            try { await host.Discord.PostBoardAsync(req.ChannelId, req.ChannelName ?? ""); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            catch (System.Net.Http.HttpRequestException) { return Error("Couldn't reach Discord."); }
            return Results.Ok(DiscordDto(mgr, host));
        }).Full();

        // Posts a hello in the updates channel, to check the bot may post (and ping) there.
        api.MapPost("/discord/updates/test", async () =>
        {
            try
            {
                await host.Discord.PostUpdateAsync("👋 Emberkeep will post server news here: crashes, address changes, updates and restarts.", 0x5865F2,
                    ping: mgr.Settings.Discord.Updates.PingRoleId.Length > 0);
            }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            catch (DiscordException) { return Error(host.Discord.Status.UpdatesError ?? "Discord didn't accept it."); }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException) { return Error("Couldn't reach Discord."); }
            return Results.Ok(DiscordDto(mgr, host));
        }).Full();

        api.MapPost("/discord/retry", () => { host.Discord.RetryNow(); return Results.Ok(DiscordDto(mgr, host)); }).Full();

        api.MapDelete("/discord/board", async () =>
        {
            try { await host.Discord.RemoveBoardAsync(); }
            catch (System.Net.Http.HttpRequestException) { return Error("Couldn't reach Discord."); }
            return Results.Ok(DiscordDto(mgr, host));
        }).Full();

        // ---------- accounts & groups (full access) ----------

        api.MapGet("/accounts", () => Results.Ok(AccountsDto(mgr, host))).Full();

        api.MapPost("/accounts/users", (NewUserRequest req) => Guard(() =>
        {
            host.Auth.CreateUser(req.Username ?? "", req.Password ?? "", req.GroupId ?? "");
            host.ApplyForwarding();
        }, () => AccountsDto(mgr, host))).Full();

        api.MapPut("/accounts/users/{uid}", (string uid, UpdateUserRequest req) => Guard(() =>
        {
            host.Auth.UpdateUser(uid, req.GroupId, req.Disabled, req.Password);
            host.ApplyForwarding();
        }, () => AccountsDto(mgr, host))).Full();

        api.MapDelete("/accounts/users/{uid}", (string uid) => Guard(() =>
        {
            host.Auth.DeleteUser(uid);
            if (!host.Auth.HasAccounts && host.ListeningOnLan) _ = Task.Run(host.RestartAsync); // nobody left to sign in remotely
            host.ApplyForwarding();
        }, () => AccountsDto(mgr, host))).Full();

        api.MapPost("/accounts/groups", (UserGroup req) => Guard(() => host.Auth.SaveGroup(req), () => AccountsDto(mgr, host))).Full();

        api.MapDelete("/accounts/groups/{gid}", (string gid) => Guard(() => host.Auth.DeleteGroup(gid), () => AccountsDto(mgr, host))).Full();
    }

    // ---------- permission checks (endpoint filters) ----------

    private static string? RouteId(HttpContext ctx) => ctx.GetRouteValue("id") as string;

    /// <summary>The account's group must be able to see the server named in the route.</summary>
    private static RouteHandlerBuilder See(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (c, next) =>
    {
        var p = WebHost.Who(c.HttpContext);
        var id = RouteId(c.HttpContext);
        return id != null && !p.CanSee(id) ? Results.NotFound() : await next(c); // don't even confirm it exists
    });

    /// <summary>Needs <paramref name="perm"/> on the server in the route, or anywhere for app-wide actions.</summary>
    private static RouteHandlerBuilder Need(this RouteHandlerBuilder b, string perm) => b.AddEndpointFilter(async (c, next) =>
    {
        var p = WebHost.Who(c.HttpContext);
        var id = RouteId(c.HttpContext);
        if (id != null && !p.CanSee(id)) return Results.NotFound();
        bool ok = id != null ? p.Can(perm, id) : p.CanAnywhere(perm);
        return ok ? await next(c) : Forbidden();
    });

    private static RouteHandlerBuilder Full(this RouteHandlerBuilder b) => b.AddEndpointFilter(async (c, next) =>
        WebHost.Who(c.HttpContext).Full ? await next(c) : Forbidden());

    private static IResult Forbidden() => Error("Your account isn't allowed to do that. Ask the host to change your group's permissions.", 403);

    private static IEnumerable<ServerRuntime> Visible(ServerManager mgr, HttpContext ctx)
    {
        var p = WebHost.Who(ctx);
        return mgr.Runtimes.Where(r => p.CanSee(r.Config.Id));
    }

    private static IResult Guard(Action action, Func<object> result)
    {
        try { action(); return Results.Ok(result()); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { return Error(ex.Message); }
    }

    // ---------- helpers ----------

    private static IResult Error(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);

    private static IResult Control(ServerManager mgr, string id, Action<ServerRuntime> action)
    {
        var rt = mgr.Get(id);
        if (rt == null) return Results.NotFound();
        action(rt);
        return Results.Ok(Summary(rt));
    }

    private static void PrepareSse(HttpContext ctx)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static async Task WriteEvent<T>(HttpContext ctx, string name, T data)
    {
        var json = JsonSerializer.Serialize(data, ServerManager.JsonCompact);
        await ctx.Response.WriteAsync($"event: {name}\ndata: {OneLine(json)}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static string OneLine(string json) => json.Replace("\r", "").Replace("\n", "");

    private static object JobDto(InstallJob j) => new
    {
        j.Id, j.Kind, j.AppId, j.Title, j.Dir, j.GameId, j.ServerId, state = j.State.ToString().ToLowerInvariant(), j.Percent,
        j.BytesDone, j.BytesTotal, j.Message, j.Error, j.Finished, j.StartedAt, j.FinishedAt,
    };

    private sealed record InstallRequest(string? GameId, int? AppId, string? Dir, string? Name);

    private static object GameDto(GameDefinition g) => new
    {
        g.Id, g.Name, g.Badge, g.Color, g.SteamAppIds, g.SteamCmdAnonymous, g.ArtAppId, g.DefaultArguments, g.Notes, g.QuickCommands,
        commands = g.Commands, stop = g.Stop, g.StopCommands, g.LogFiles, g.ConfigFiles, g.DefaultRconPort,
    };

    private static object Summary(ServerRuntime rt) => new
    {
        id = rt.Config.Id,
        name = rt.Config.Name,
        gameId = rt.Config.GameId,
        state = rt.State,
        pid = rt.Pid,
        startedAt = rt.StartedAt,
        uptimeSeconds = rt.StartedAt is DateTime t ? (long)(DateTime.Now - t).TotalSeconds : 0,
        cpu = Math.Round(rt.CpuPercent, 1),
        memory = rt.MemoryBytes,
        players = rt.Players.Supported && rt.State == ServerState.Running ? new { count = rt.Players.Count, names = rt.Players.Names } : null,
        lastExitCode = rt.LastExitCode,
        lastError = rt.LastError,
        adopted = rt.Adopted,
        commandChannel = rt.Config.CommandChannel,
        installDir = rt.Config.InstallDir,
        artAppId = ArtCache.AppIdFor(rt.Config),
    };

    private const string Hidden = "(hidden: needs the launch settings permission)";

    /// <summary>A server's details, trimmed to what this account may see.</summary>
    private static object Detail(ServerRuntime rt, Principal p)
    {
        string id = rt.Config.Id;
        bool canLaunch = p.Can(Permissions.Launch, id);
        bool canSettings = canLaunch || p.Can(Permissions.Settings, id);
        var can = Permissions.All.Where(a => p.Can(a.Id, id)).Select(a => a.Id).ToList();

        ServerInstance? config = null;
        if (canSettings)
        {
            config = JsonSerializer.Deserialize<ServerInstance>(JsonSerializer.Serialize(rt.Config, ServerManager.Json), ServerManager.Json)!;
            if (!canLaunch)
            {
                // Launch arguments and environment often carry passwords (Valheim's -password, tokens…).
                config.Arguments = Hidden;
                config.Environment = new();
                config.ExtraLogFiles = new();
            }
        }
        var rc = canSettings && rt.Config.CommandChannel is CommandChannel.Rcon or CommandChannel.Telnet ? rt.ResolveRemoteConsole() : null;
        return new
        {
            summary = Summary(rt),
            can,
            config,
            resolvedExecutable = canSettings ? rt.ResolveExecutable() : null,
            resolvedWorkingDirectory = canSettings ? rt.ResolveWorkingDirectory() : null,
            installDirExists = Directory.Exists(rt.Config.InstallDir),
            executableFound = ServerRecheck.ExecutableFound(rt),
            portConflicts = Mgr != null ? PortConflicts(Mgr, rt) : new(),
            remoteConsole = rc == null ? null : new { rc.Host, rc.Port, hasPassword = rc.Password.Length > 0 },
            logFiles = canLaunch ? rt.LogFilePatterns() : Enumerable.Empty<string>(),
            join = JoinInfo.Resolve(rt.Config),
            launcherFound = LauncherLearn.LauncherPath(rt.Config) != null,
            // A game set up through its own launcher that Emberkeep hasn't learned yet.
            needsLearn = canLaunch && LauncherLearn.LauncherPath(rt.Config) != null && GameCatalog.Get(rt.Config.GameId).Launcher != null
                && !ArgBlobs.HasPlaceholders(rt.Config.Arguments) && string.IsNullOrWhiteSpace(rt.Config.Arguments),
        };
    }

    private static object SettingsDto(ServerManager mgr, WebHost host) => new
    {
        port = host.Port,
        configuredPort = mgr.Settings.Port,
        remoteAccess = mgr.Settings.RemoteAccess,
        listeningOnLan = host.ListeningOnLan,
        hasAccounts = host.Auth.HasAccounts,
        minimizeToTray = mgr.Settings.MinimizeToTray,
        startWithWindows = WindowsStartup.Enabled,
        startupOtherCopy = !WindowsStartup.Enabled && WindowsStartup.RegisteredCommand() != null,
        steamCmdPath = mgr.Settings.SteamCmdPath,
        steamCmdInUse = SteamCmd.ExePath(mgr.Settings),
        steamCmdInstalled = SteamCmd.IsInstalled(mgr.Settings),
        installRoot = Path.GetDirectoryName(host.Installer.SuggestedDir("x")) ?? "",
        backupRoot = host.Backups.Root,
        notifications = mgr.Settings.Notifications,
        dailyRestart = mgr.Settings.DailyRestart,
        dailyRestartTime = mgr.Settings.DailyRestartTime,
        pcRestart = PcRestartDto(host),
        internetAccess = mgr.Settings.InternetAccess,
        longPassword = host.Auth.AllPasswordsLong,
        minInternetPasswordLength = AuthService.MinInternetPasswordLength,
        forwarding = host.Forwarder.Status,
        lanUrls = NetworkInfo.LocalAddresses().Select(a => $"http://{a.Address}:{host.Port}"),
        dataDir = ServerManager.DataDir,
        version = typeof(Api).Assembly.GetName().Version?.ToString(3),
        checkForUpdates = mgr.Settings.CheckForUpdates,
    };

    /// <summary>Whether a newer Emberkeep is out, for the sidebar notice and App settings.</summary>
    private static object UpdateDto(ServerManager mgr, WebHost host)
    {
        var u = host.Updates;
        return new
        {
            enabled = mgr.Settings.CheckForUpdates,
            current = UpdateChecker.CurrentVersion.ToString(),
            latest = u.LatestVersion,
            checkedAt = u.CheckedAt,
            error = u.Error,
            installing = u.Installing,
            available = u.Available is { } a ? new { a.Version, a.Title, a.Notes, a.PageUrl, canInstall = a.InstallerUrl != null } : null,
        };
    }

    /// <summary>Accounts (never their password hashes), groups, the permission list and the servers groups can pick.</summary>
    private static object AccountsDto(ServerManager mgr, WebHost host) => new
    {
        users = mgr.Settings.Users.Select(u => new { u.Id, u.Username, u.GroupId, u.Disabled, u.LongPassword, u.CreatedAt, u.LastSignIn }),
        groups = mgr.Settings.Groups.Select(g => new { g.Id, g.Name, g.FullAccess, g.Permissions, g.AllServers, g.ServerIds, g.BuiltIn, members = mgr.Settings.Users.Count(u => u.GroupId == g.Id) }),
        permissions = Permissions.All.Select(a => new { id = a.Id, label = a.Label, help = a.Help, risky = a.Id == Permissions.Launch }),
        servers = mgr.Settings.Servers.Select(s => new { s.Id, s.Name, s.GameId }),
        minPasswordLength = AuthService.MinPasswordLength,
        minInternetPasswordLength = AuthService.MinInternetPasswordLength,
        remoteAccess = mgr.Settings.RemoteAccess,
    };

    /// <summary>Discord setup for the UI. The token itself never leaves the host.</summary>
    private static object DiscordDto(ServerManager mgr, WebHost host)
    {
        var d = mgr.Settings.Discord;
        return new
        {
            status = host.Discord.Status,
            hasToken = host.Discord.HasToken,
            applicationId = d.ApplicationId,
            enabled = d.Enabled,
            inviteUrl = host.Discord.InviteUrl,
            board = new { channelId = d.BoardChannelId, channelName = d.BoardChannelName, posted = d.BoardMessageId.Length > 0 },
            allServers = d.AllServers,
            serverIds = d.ServerIds,
            addressHost = d.AddressHost,
            showOffline = d.ShowOffline,
            privateReplies = d.PrivateReplies,
            updates = d.Updates,
            history = host.Discord.History,
            servers = mgr.Settings.Servers.Select(s => new { s.Id, s.Name, s.GameId }),
        };
    }

    private sealed record DiscordRequest(bool? Enabled, bool? AllServers, List<string>? ServerIds, string? AddressHost, bool? ShowOffline, bool? PrivateReplies, DiscordUpdatesRequest? Updates);
    private sealed record DiscordUpdatesRequest(string? ChannelId, string? ChannelName, string? PingRoleId, string? PingRoleName, bool Crashes, bool AddressChanges, bool GameUpdates, bool Restarts, bool StartStop);
    private sealed record DiscordTokenRequest(string? Token);
    private sealed record DiscordBoardRequest(string? ChannelId, string? ChannelName);
    private sealed record CreateServerRequest(string? GameId, string? InstallDir, string? Name, int? SteamAppId);
    private sealed record CommandRequest(string? Command);
    private sealed record SaveFileRequest(string? Path, string? Content, DateTime? ExpectedModified, bool Force);
    private sealed record SettingsRequest(int? Port, bool? RemoteAccess, bool? MinimizeToTray, bool? InternetAccess, bool? CheckForUpdates, bool? StartWithWindows,
        bool? DailyRestart, string? DailyRestartTime, string? SteamCmdPath, string? InstallRoot, string? BackupRoot);

    private static object PcRestartDto(WebHost host)
    {
        var r = host.PcRestart;
        var p = r.Pending;
        return new
        {
            pending = p == null ? null : new { at = p.At.ToUniversalTime(), reason = p.Reason, stopping = p.Stopping,
                secondsLeft = Math.Max(0, (int)Math.Ceiling((p.At - DateTime.Now).TotalSeconds)) },
            nextDaily = r.NextDaily()?.ToUniversalTime(),
            error = r.LastError,
        };
    }
    private sealed record ChangePasswordRequest(string? Current, string? Next);
    private sealed record NewUserRequest(string? Username, string? Password, string? GroupId);
    private sealed record UpdateUserRequest(string? GroupId, bool? Disabled, string? Password);
    private sealed record FsEntry(string Name, string Path, bool IsDir);
}
