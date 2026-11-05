using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hostbay.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Hostbay.Web;

public static class Api
{
    public static void Map(WebApplication app, ServerManager mgr, WebHost host)
    {
        var api = app.MapGroup("/api");

        // ---------- catalog & state ----------

        api.MapGet("/games", () => GameCatalog.All.Select(GameDto));

        api.MapGet("/servers", () => mgr.Runtimes.Select(Summary));

        api.MapGet("/servers/{id}", (string id) => mgr.Get(id) is { } rt ? Results.Ok(Detail(rt)) : Results.NotFound());

        api.MapPost("/servers", (CreateServerRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.InstallDir) || !Directory.Exists(req.InstallDir))
                return Error("That folder doesn't exist.");
            var game = GameCatalog.Get(req.GameId ?? "generic");
            var inst = GameCatalog.CreateInstance(game, Path.GetFullPath(req.InstallDir), string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim(), req.SteamAppId);
            var rt = mgr.Add(inst);
            return Results.Ok(Detail(rt));
        });

        api.MapPut("/servers/{id}", (string id, ServerInstance body) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name)) return Error("The server needs a name.");
            body.Environment ??= new();
            body.StopCommands ??= new();
            body.ExtraLogFiles ??= new();
            body.Rcon ??= new();
            body.StopTimeoutSeconds = Math.Clamp(body.StopTimeoutSeconds, 5, 900);
            mgr.Update(id, body);
            return Results.Ok(Detail(rt));
        });

        api.MapDelete("/servers/{id}", (string id) =>
        {
            try { mgr.Remove(id); return Results.Ok(); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        });

        api.MapPost("/servers/reorder", (List<string> ids) => { mgr.Reorder(ids); return Results.Ok(); });

        // ---------- control ----------

        api.MapPost("/servers/{id}/start", (string id) => Control(mgr, id, rt => { _ = rt.StartAsync(); }));
        api.MapPost("/servers/{id}/stop", (string id) => Control(mgr, id, rt => { _ = rt.StopAsync(); }));
        api.MapPost("/servers/{id}/restart", (string id) => Control(mgr, id, rt => { _ = rt.RestartAsync(); }));
        api.MapPost("/servers/{id}/kill", (string id) => Control(mgr, id, rt => rt.Kill()));
        api.MapPost("/servers/{id}/logs/clear", (string id) => Control(mgr, id, rt => rt.Logs.Clear()));

        api.MapPost("/servers/{id}/command", async (string id, CommandRequest req) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            try
            {
                var reply = await rt.SendCommandAsync(req.Command ?? "");
                return Results.Ok(new { reply });
            }
            catch (Exception ex)
            {
                rt.Logs.Add("error", ex.Message);
                return Error(ex.Message);
            }
        });

        // ---------- live streams (Server-Sent Events) ----------

        api.MapGet("/events", async (HttpContext ctx) =>
        {
            PrepareSse(ctx);
            using var signal = new SemaphoreSlim(0);
            void OnChange() { if (signal.CurrentCount == 0) signal.Release(); }
            mgr.Changed += OnChange;
            try
            {
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    var payload = JsonSerializer.Serialize(mgr.Runtimes.Select(Summary), ServerManager.JsonCompact);
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
            PrepareSse(ctx);
            var (backlog, ch) = rt.Logs.Subscribe();
            try
            {
                await WriteEvent(ctx, "backlog", backlog);
                var batch = new List<LogLine>();
                while (await ch.Reader.WaitToReadAsync(ctx.RequestAborted))
                {
                    batch.Clear();
                    while (batch.Count < 500 && ch.Reader.TryRead(out var line)) batch.Add(line);
                    await WriteEvent(ctx, "lines", batch);
                    await Task.Delay(50, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally { rt.Logs.Unsubscribe(ch); }
        });

        // ---------- config files ----------

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
        });

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
        });

        api.MapPut("/servers/{id}/configs/file", (string id, SaveFileRequest req) =>
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
                return Results.Ok(new { modified = new FileInfo(req.Path).LastWriteTimeUtc, backup });
            }
            catch (Exception ex) { return Error($"Couldn't save: {ex.Message}"); }
        });

        api.MapGet("/servers/{id}/configs/backups", (string id, string path) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!ConfigDiscovery.IsAllowed(rt.Config, path)) return Error("Not allowed.", 403);
            return Results.Ok(ConfigFiles.ListBackups(rt.Config, path));
        });

        api.MapGet("/servers/{id}/configs/backup", (string id, string path, string backup) =>
        {
            var rt = mgr.Get(id);
            if (rt == null) return Results.NotFound();
            if (!ConfigDiscovery.IsAllowed(rt.Config, path)) return Error("Not allowed.", 403);
            var file = ConfigFiles.ListBackups(rt.Config, path).FirstOrDefault(b => b.Name == backup);
            if (file == null) return Results.NotFound();
            var (text, _, _) = ConfigFiles.Read(file.FullPath);
            return Results.Ok(new { content = text });
        });

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
        });

        // ---------- discovery ----------

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
        });

        api.MapGet("/scan/folder", (string path) =>
        {
            if (!Directory.Exists(path)) return Error("That folder doesn't exist.");
            return Results.Ok(SteamScanner.ScanFolder(Path.GetFullPath(path), mgr.Settings.Servers));
        });

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
        });

        // ---------- app settings ----------

        api.MapGet("/settings", () => SettingsDto(mgr, host));

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
                if (remote && !host.Auth.HasPassword) return Error("Set a password before turning on remote access.");
                needsRestart |= remote != host.ListeningOnLan;
                s.RemoteAccess = remote;
            }
            if (req.MinimizeToTray is bool tray) s.MinimizeToTray = tray;
            mgr.Save();
            if (needsRestart) _ = Task.Run(async () => { await Task.Delay(300); await host.RestartAsync(); });
            return Results.Ok(new { restarting = needsRestart });
        });

        api.MapPost("/settings/password", (PasswordRequest req, HttpContext ctx) =>
        {
            try { host.Auth.SetPassword(req.Password ?? "", ctx.Request.Cookies[AuthService.CookieName]); return Results.Ok(SettingsDto(mgr, host)); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });
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

    private static object GameDto(GameDefinition g) => new
    {
        g.Id, g.Name, g.Badge, g.Color, g.SteamAppIds, g.DefaultArguments, g.Notes, g.QuickCommands,
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
        lastExitCode = rt.LastExitCode,
        lastError = rt.LastError,
        adopted = rt.Adopted,
        commandChannel = rt.Config.CommandChannel,
        installDir = rt.Config.InstallDir,
    };

    private static object Detail(ServerRuntime rt)
    {
        var rc = rt.Config.CommandChannel is CommandChannel.Rcon or CommandChannel.Telnet ? rt.ResolveRemoteConsole() : null;
        return new
        {
            summary = Summary(rt),
            config = rt.Config,
            resolvedExecutable = rt.ResolveExecutable(),
            resolvedWorkingDirectory = rt.ResolveWorkingDirectory(),
            installDirExists = Directory.Exists(rt.Config.InstallDir),
            remoteConsole = rc == null ? null : new { rc.Host, rc.Port, hasPassword = rc.Password.Length > 0 },
            logFiles = rt.LogFilePatterns(),
        };
    }

    private static object SettingsDto(ServerManager mgr, WebHost host) => new
    {
        port = host.Port,
        configuredPort = mgr.Settings.Port,
        remoteAccess = mgr.Settings.RemoteAccess,
        listeningOnLan = host.ListeningOnLan,
        hasPassword = host.Auth.HasPassword,
        minimizeToTray = mgr.Settings.MinimizeToTray,
        lanUrls = WebHost.LanAddresses().Select(a => $"http://{a}:{host.Port}"),
        dataDir = ServerManager.DataDir,
        version = typeof(Api).Assembly.GetName().Version?.ToString(3),
    };

    private sealed record CreateServerRequest(string? GameId, string? InstallDir, string? Name, int? SteamAppId);
    private sealed record CommandRequest(string? Command);
    private sealed record SaveFileRequest(string? Path, string? Content, DateTime? ExpectedModified, bool Force);
    private sealed record SettingsRequest(int? Port, bool? RemoteAccess, bool? MinimizeToTray);
    private sealed record PasswordRequest(string? Password);
    private sealed record FsEntry(string Name, string Path, bool IsDir);
}

/// <summary>Reads and writes config files while preserving their encoding and line endings, with backups.</summary>
public static class ConfigFiles
{
    private const int KeepBackups = 25;

    public static (string Text, Encoding Encoding, string Newline) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding enc;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) enc = new UTF8Encoding(true);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) enc = new UnicodeEncoding(false, true);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) enc = new UnicodeEncoding(true, true);
        else enc = new UTF8Encoding(false);
        var text = enc.GetString(bytes, enc.GetPreamble().Length, bytes.Length - enc.GetPreamble().Length);
        var newline = text.Contains("\r\n") || !text.Contains('\n') ? "\r\n" : "\n";
        return (text, enc, newline);
    }

    /// <summary>Saves new content, keeping the file's encoding and line endings. Returns the backup file name, if any.</summary>
    public static string? Write(ServerInstance s, string path, string content)
    {
        Encoding enc = new UTF8Encoding(false);
        string newline = "\r\n";
        string? backup = null;
        if (File.Exists(path))
        {
            (_, enc, newline) = Read(path);
            backup = Backup(s, path);
        }
        var normalized = content.Replace("\r\n", "\n");
        if (newline == "\r\n") normalized = normalized.Replace("\n", "\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".hostbay-tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var pre = enc.GetPreamble();
            fs.Write(pre);
            fs.Write(enc.GetBytes(normalized));
        }
        File.Move(tmp, path, overwrite: true);
        return backup;
    }

    private static string BackupFolder(ServerInstance s, string path)
    {
        // One folder per server and file, keyed by a short hash of the full path so same-named files don't collide.
        path = Path.GetFullPath(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..8];
        return Path.Combine(ServerManager.BackupDir, s.Id, Path.GetFileName(path) + "-" + hash);
    }

    public static string Backup(ServerInstance s, string path)
    {
        var dir = BackupFolder(s, path);
        Directory.CreateDirectory(dir);
        // Millisecond stamps keep names unique and sortable.
        var name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + Path.GetExtension(path);
        File.Copy(path, Path.Combine(dir, name), overwrite: true);
        foreach (var old in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.Name).Skip(KeepBackups)) old.Delete();
        return name;
    }

    public sealed record BackupInfo(string Name, string FullPath, DateTime Created, long Size);

    public static List<BackupInfo> ListBackups(ServerInstance s, string path)
    {
        var dir = new DirectoryInfo(BackupFolder(s, Path.GetFullPath(path)));
        if (!dir.Exists) return new();
        return dir.GetFiles().OrderByDescending(f => f.Name).Select(f => new BackupInfo(f.Name, f.FullName, f.LastWriteTimeUtc, f.Length)).ToList();
    }
}
