using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>Discord settings. The bot token is encrypted for the current Windows user (DPAPI) and never sent to the UI.</summary>
public sealed class DiscordSettings
{
    public string TokenProtected { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string BotName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Where the live status board is posted, and the message Emberkeep keeps editing.</summary>
    public string BoardChannelId { get; set; } = "";
    public string BoardChannelName { get; set; } = "";
    public string BoardMessageId { get; set; } = "";
    public bool AllServers { get; set; } = true;
    public List<string> ServerIds { get; set; } = new();
    /// <summary>Host name to share instead of the public IP, e.g. a dynamic DNS name. Empty means the public IP.</summary>
    public string AddressHost { get; set; } = "";
    public bool ShowOffline { get; set; } = true;
    /// <summary>Command and button replies are only visible to the person who asked.</summary>
    public bool PrivateReplies { get; set; } = true;
    /// <summary>The channel the bot posts news in: crashes, address changes, updates, restarts.</summary>
    public DiscordUpdates Updates { get; set; } = new();
}

/// <summary>What the bot posts in the updates channel, and who it pings.</summary>
public sealed class DiscordUpdates
{
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    /// <summary>A role to mention for crashes and address changes (people get a notification). Empty: no ping.</summary>
    public string PingRoleId { get; set; } = "";
    public string PingRoleName { get; set; } = "";
    public bool Crashes { get; set; } = true;
    public bool AddressChanges { get; set; } = true;
    public bool GameUpdates { get; set; } = true;
    public bool Restarts { get; set; } = true;
    public bool StartStop { get; set; }
}

public sealed record DiscordLogLine(DateTime Time, string Text, bool Problem);

/// <summary>What the bot is doing, for the UI.</summary>
public sealed record DiscordStatus(string State, string Message, string? BotName, int Guilds, string? BoardError, string? UpdatesError);

public sealed class DiscordException(int status, string message, int code = 0) : Exception(message)
{
    public int Status { get; } = status;
    public int Code { get; } = code;
}

/// <summary>
/// A small Discord bot built on the gateway (for slash commands and buttons) and the REST API (for the status board).
/// It only shares information: server status and the address to join with. It can't control servers.
/// </summary>
public sealed class DiscordBot : IDisposable
{
    public static string ApiBase { get; } = AppEnv.Get("DISCORD_API") is { Length: > 0 } b ? b.TrimEnd('/') : "https://discord.com/api/v10";

    /// <summary>View channel, send messages, embed links, read message history.</summary>
    public const long InvitePermissions = 1024 | 2048 | 16384 | 65536;

    /// <summary>REST bodies: leave out nulls (optional fields such as an embed's thumbnail).</summary>
    private static readonly JsonSerializerOptions Out = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    /// <summary>Gateway messages: keep nulls. Discord requires fields like "since" and a heartbeat's "d" even when null, and closes the connection (4002) without them.</summary>
    private static readonly JsonSerializerOptions GatewayJson = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hostbay.Discord.v1"); // Emberkeep's old name: changing it would lose saved bot tokens

    private readonly ServerManager _mgr;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _boardLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _guilds = new();
    private readonly Timer _boardTimer;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ClientWebSocket? _ws;
    private string? _token;
    private string _lastBoard = "";
    private string _lastPresence = "";
    private string _state = "off", _message = "Not set up.";
    private string? _boardError;
    private int _boardPending;
    private volatile bool _sessionReady;
    private string? _updatesError;
    // Resuming: after a short drop Discord replays what was missed, without a full reconnect.
    private string? _sessionId, _resumeUrl, _gatewayUrl;
    private long? _seq;
    private volatile bool _acked = true;
    private CancellationTokenSource? _wake;
    private DateTime _lastWake;
    private readonly List<DiscordLogLine> _history = new();
    private readonly SemaphoreSlim _postLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _crashPosted = new();
    private readonly ConcurrentDictionary<string, byte> _awaitingBack = new();
    private readonly ConcurrentDictionary<string, DateTime> _warnPosted = new();

    public DiscordBot(ServerManager mgr)
    {
        _mgr = mgr;
        _mgr.Changed += () => QueueBoardUpdate();
        // Catch changes that don't raise events: the public IP, ports edited in config files.
        _boardTimer = new Timer(_ => QueueBoardUpdate(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        _mgr.Activity.Added += OnActivity;
        // Reconnect straight away when the network comes back or the PC wakes, instead of waiting for the next retry.
        NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) NetworkBack("The network came back."); };
        NetworkChange.NetworkAddressChanged += (_, _) => NetworkBack("The network changed.");
        if (OperatingSystem.IsWindows()) try { Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) NetworkBack("The PC woke up."); }; } catch { }
    }

    /// <summary>The last connection events, newest first, so a flaky connection can be understood.</summary>
    public IReadOnlyList<DiscordLogLine> History { get { lock (_history) return _history.AsEnumerable().Reverse().ToList(); } }

    private void Log(string text, bool problem = false)
    {
        lock (_history)
        {
            if (_history.Count > 0 && _history[^1].Text == text && DateTime.UtcNow - _history[^1].Time < TimeSpan.FromMinutes(5)) return;
            _history.Add(new DiscordLogLine(DateTime.UtcNow, text, problem));
            if (_history.Count > 25) _history.RemoveAt(0);
        }
    }

    /// <summary>"Retry now": skips the wait before the next connection attempt.</summary>
    public void RetryNow()
    {
        if (_loop is not { IsCompleted: false }) { Start(); return; }
        if (_state != "online") { Log("Retrying now."); _wake?.Cancel(); }
    }

    private void NetworkBack(string why)
    {
        if (_token == null || _loop is not { IsCompleted: false }) return;
        lock (_history) { if (DateTime.UtcNow - _lastWake < TimeSpan.FromSeconds(5)) return; _lastWake = DateTime.UtcNow; }
        if (_state != "online") { Log($"{why} Reconnecting now."); _wake?.Cancel(); return; }
        // Online as far as we know, but the connection may have died quietly: check with a heartbeat.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000); // let the network settle
            if (_ws is not { State: WebSocketState.Open } ws) return;
            _acked = false;
            try { await SendAsync(ws, new { op = 1, d = _seq }); } catch { }
            await Task.Delay(8000);
            if (!_acked && _ws == ws) { Log($"{why} The connection stopped answering, so reconnecting.", true); ws.Abort(); }
        });
    }

    private DiscordSettings S => _mgr.Settings.Discord;

    public bool HasToken => S.TokenProtected.Length > 0;

    public DiscordStatus Status => new(_state, _message, S.BotName.Length > 0 ? S.BotName : null, _guilds.Count, _boardError, _updatesError);

    public string? InviteUrl => S.ApplicationId.Length > 0
        ? $"https://discord.com/oauth2/authorize?client_id={S.ApplicationId}&scope=bot%20applications.commands&permissions={InvitePermissions}"
        : null;

    // ---------------- lifecycle ----------------

    /// <summary>Starts the bot if it's set up and switched on. Safe to call repeatedly.</summary>
    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        if (!HasToken || !S.Enabled) { SetState("off", HasToken ? "Switched off." : "Not set up."); return; }
        _token = Unprotect(S.TokenProtected);
        if (_token == null) { SetState("error", "The saved token can't be read on this Windows account. Paste it again."); return; }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts; var loop = _loop;
        _cts = null; _loop = null;
        if (cts == null) return;
        cts.Cancel();
        try { if (_ws is { State: WebSocketState.Open } ws) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { if (loop != null) await loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _guilds.Clear();
        SetState("off", HasToken ? "Switched off." : "Not set up.");
    }

    /// <summary>Checks a token with Discord, then saves it encrypted and connects.</summary>
    public async Task SetTokenAsync(string token)
    {
        token = token.Trim();
        if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) token = token[4..].Trim();
        if (token.Length < 20 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("That doesn't look like a bot token. Copy it from the Bot page of your Discord application.");
        JsonElement me, app;
        try
        {
            me = await RestAsync(HttpMethod.Get, "/users/@me", null, token);
            app = await RestAsync(HttpMethod.Get, "/applications/@me", null, token);
        }
        catch (DiscordException ex) when (ex.Status == 401)
        {
            throw new ArgumentException("Discord didn't accept that token. Reset the token on the Bot page and paste the new one.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Couldn't reach Discord. Check your internet connection.");
        }
        await StopAsync();
        S.TokenProtected = Protect(token);
        S.ApplicationId = app.GetProperty("id").GetString() ?? "";
        S.BotName = me.GetProperty("username").GetString() ?? "";
        S.Enabled = true;
        _mgr.Save();
        Start();
    }

    /// <summary>Forgets the token and disconnects. The board message stays in Discord as it is.</summary>
    public async Task ForgetAsync()
    {
        await StopAsync();
        S.TokenProtected = ""; S.ApplicationId = ""; S.BotName = ""; S.BoardMessageId = "";
        _sessionId = null; _resumeUrl = null; _seq = null;
        _mgr.Save();
        _token = null;
        SetState("off", "Not set up.");
    }

    private void SetState(string state, string message) { _state = state; _message = message; }

    private static string Protect(string s) => Platform.Protect(s, Entropy);

    private static string? Unprotect(string s) => Platform.Unprotect(s, Entropy);

    // ---------------- gateway ----------------

    private async Task RunAsync(CancellationToken ct)
    {
        int backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            if (_state != "online") SetState("connecting", "Connecting to Discord…");
            try
            {
                await ConnectOnceAsync(ct);
                backoff = 1;
                Log("Discord asked the bot to reconnect.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (DiscordException ex) when (ex.Status == 401 || ex.Code is 4004 or 4010 or 4011 or 4012 or 4013 or 4014)
            {
                var msg = ex.Code == 4004 || ex.Status == 401
                    ? "Discord rejected the bot token. It may have been reset: paste the new one."
                    : $"Discord closed the connection: {ex.Message}";
                SetState("error", msg);
                Log(msg, true);
                return;
            }
            catch (Exception ex)
            {
                if (ex is DiscordException { Code: 4007 or 4009 }) ForgetSession(); // the session can't be resumed: start a new one
                if (_sessionReady) backoff = 1; // it was working: this is a one-off drop, not a failing connection
                var why = ex is HttpRequestException or WebSocketException ? Plain(ex) : ex.Message;
                SetState("connecting", $"Lost the connection to Discord ({why}). Retrying in {backoff}s…");
                Log($"Connection lost: {why}.", true);
            }
            if (_state != "online") _guilds.Clear();
            using (var wake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                _wake = wake;
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), wake.Token); }
                catch { if (ct.IsCancellationRequested) return; backoff = 1; } // woken early: the network is back
                finally { _wake = null; }
            }
            backoff = Math.Min(backoff * 2, 30);
            if (_state == "online") SetState("connecting", "Reconnecting to Discord…");
        }
    }

    private void ForgetSession() { _sessionId = null; _resumeUrl = null; _seq = null; }

    /// <summary>Network errors in words people know, instead of socket jargon.</summary>
    private static string Plain(Exception ex)
    {
        var m = (ex.InnerException ?? ex).Message;
        if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase) || m.Contains("name", StringComparison.OrdinalIgnoreCase) && m.Contains("resolve", StringComparison.OrdinalIgnoreCase))
            return "no internet connection, or Discord can't be found";
        if (m.Contains("timed out", StringComparison.OrdinalIgnoreCase) || ex is TaskCanceledException) return "Discord didn't answer in time";
        if (m.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "the connection was refused";
        if (m.Contains("without completing the close handshake", StringComparison.OrdinalIgnoreCase) || m.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("aborted", StringComparison.OrdinalIgnoreCase)) return "the connection dropped";
        return m.TrimEnd('.');
    }

    private async Task<string> GatewayUrlAsync()
    {
        if (_gatewayUrl != null) return _gatewayUrl;
        var gw = await RestAsync(HttpMethod.Get, "/gateway/bot", null);
        return _gatewayUrl = gw.GetProperty("url").GetString() ?? "wss://gateway.discord.gg";
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        _sessionReady = false;
        bool resume = _sessionId != null && _resumeUrl != null && _seq != null;
        var url = (resume ? _resumeUrl! : await GatewayUrlAsync()) + "/?v=10&encoding=json";
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _ws = ws;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await ws.ConnectAsync(new Uri(url), connectTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Discord didn't answer in time"); }
            catch when (!resume) { _gatewayUrl = null; throw; } // ask for the address again next time
        }

        using var hb = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acked = true;
        try
        {
            while (true)
            {
                using var doc = await ReceiveAsync(ws, ct);
                if (doc == null)
                {
                    int code = (int?)ws.CloseStatus ?? 0;
                    throw new DiscordException(0, ws.CloseStatusDescription is { Length: > 0 } d ? d : $"closed ({code})", code);
                }
                var root = doc.RootElement;
                int op = root.GetProperty("op").GetInt32();
                if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) _seq = s.GetInt64();
                switch (op)
                {
                    case 10: // hello: start heartbeating, then identify
                    {
                        int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay((int)(interval * Random.Shared.NextDouble()), hb.Token);
                                while (!hb.Token.IsCancellationRequested)
                                {
                                    if (!_acked) { Log("Discord stopped answering heartbeats.", true); ws.Abort(); return; } // zombie connection: drop it and reconnect
                                    _acked = false;
                                    await SendAsync(ws, new { op = 1, d = _seq });
                                    await Task.Delay(interval, hb.Token);
                                }
                            }
                            catch { }
                        });
                        if (resume)
                        {
                            await SendAsync(ws, new { op = 6, d = new { token = _token, session_id = _sessionId, seq = _seq } });
                            break;
                        }
                        await SendAsync(ws, new
                        {
                            op = 2,
                            d = new
                            {
                                token = _token,
                                intents = 1, // GUILDS: join/leave events, not privileged
                                properties = new Dictionary<string, string> { ["os"] = "windows", ["browser"] = "Emberkeep", ["device"] = "Emberkeep" },
                                presence = PresencePayload(),
                            },
                        });
                        break;
                    }
                    case 11: _acked = true; break;
                    case 1: await SendAsync(ws, new { op = 1, d = _seq }); break;
                    case 7: return; // asked to reconnect (and resume)
                    case 9: // invalid session: resumable only if d is true
                        if (!(root.TryGetProperty("d", out var rd) && rd.ValueKind == JsonValueKind.True)) ForgetSession();
                        Log("Discord ended the session; starting a new one.");
                        await Task.Delay(Random.Shared.Next(1000, 5000), ct);
                        return;
                    case 0: OnDispatch(root.GetProperty("t").GetString(), root.GetProperty("d").Clone()); break;
                }
            }
        }
        finally
        {
            hb.Cancel();
            _ws = null;
        }
    }

    private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray());
    }

    private async Task SendAsync(ClientWebSocket ws, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, GatewayJson);
        await _sendLock.WaitAsync();
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    private void OnDispatch(string? type, JsonElement d)
    {
        switch (type)
        {
            case "READY":
                _sessionReady = true;
                _sessionId = d.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                _resumeUrl = d.TryGetProperty("resume_gateway_url", out var ru) ? ru.GetString() : null;
                Log("Connected.");
                _guilds.Clear();
                foreach (var g in d.GetProperty("guilds").EnumerateArray()) _guilds[g.GetProperty("id").GetString()!] = 0;
                if (d.TryGetProperty("user", out var u) && u.GetProperty("username").GetString() is { } name && name != S.BotName) { S.BotName = name; _mgr.Save(); }
                SetState("online", "Online.");
                _lastPresence = "";
                _ = Task.Run(RegisterCommandsAsync);
                QueueBoardUpdate(force: true);
                break;
            case "RESUMED":
                _sessionReady = true;
                SetState("online", "Online.");
                Log("Reconnected (picked up where it left off).");
                QueueBoardUpdate(force: true);
                break;
            case "GUILD_CREATE":
                _guilds[d.GetProperty("id").GetString()!] = 0;
                break;
            case "GUILD_DELETE":
                if (!(d.TryGetProperty("unavailable", out var un) && un.ValueKind == JsonValueKind.True)) _guilds.TryRemove(d.GetProperty("id").GetString()!, out _);
                break;
            case "INTERACTION_CREATE":
                _ = Task.Run(() => HandleInteractionAsync(d));
                break;
        }
    }

    private object PresencePayload()
    {
        var shown = Shown().ToList();
        int online = shown.Count(r => r.State == ServerState.Running);
        string text = shown.Count == 0 ? "No servers shared" : $"{online} of {shown.Count} server{(shown.Count == 1 ? "" : "s")} online";
        _lastPresence = text;
        return new { since = (long?)null, activities = new[] { new { name = "Custom Status", type = 4, state = text } }, status = "online", afk = false };
    }

    private async Task UpdatePresenceAsync()
    {
        if (_ws is not { State: WebSocketState.Open } ws || _state != "online") return;
        var before = _lastPresence;
        var p = PresencePayload();
        if (_lastPresence != before) await SendAsync(ws, new { op = 3, d = p });
    }

    // ---------------- slash commands and buttons ----------------

    private async Task RegisterCommandsAsync()
    {
        if (S.ApplicationId.Length == 0) return;
        var serverOption = new { type = 3, name = "server", description = "Which server", required = false, autocomplete = true };
        object[] commands =
        [
            new { name = "servers", description = "Show the game servers and how to join them", type = 1, contexts = new[] { 0 } },
            new { name = "ip", description = "Get the address to join a game server", type = 1, contexts = new[] { 0 }, options = new[] { serverOption } },
        ];
        try { await RestAsync(HttpMethod.Put, $"/applications/{S.ApplicationId}/commands", commands); }
        catch { /* the board still works without commands */ }
    }

    private async Task HandleInteractionAsync(JsonElement i)
    {
        try
        {
            string id = i.GetProperty("id").GetString()!, token = i.GetProperty("token").GetString()!;
            int type = i.GetProperty("type").GetInt32();
            var data = i.TryGetProperty("data", out var dd) ? dd : default;
            object? response = null;

            if (type == 4) // autocomplete
            {
                string typed = Option(data, "server") ?? "";
                var choices = Shown().Where(r => r.Config.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
                    .Take(25).Select(r => new { name = Trim(r.Config.Name, 100), value = r.Config.Id }).ToArray();
                response = new { type = 8, data = new { choices } };
            }
            else if (type == 2)
            {
                string name = data.GetProperty("name").GetString() ?? "";
                if (name == "servers") response = Reply(await BoardPayloadAsync());
                else if (name == "ip")
                {
                    var wanted = Option(data, "server");
                    var shown = Shown().ToList();
                    var rt = wanted == null ? (shown.Count == 1 ? shown[0] : null)
                        : shown.FirstOrDefault(r => r.Config.Id == wanted)
                          ?? shown.FirstOrDefault(r => r.Config.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                          ?? shown.FirstOrDefault(r => r.Config.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                    response = rt != null ? Reply(await AddressPayloadAsync(rt))
                        : Reply(new { content = wanted == null && shown.Count > 1 ? "Pick a server: `/ip server:<name>`, or use `/servers` to see them all." : "No shared server matches that name." });
                }
            }
            else if (type == 3)
            {
                string cid = data.GetProperty("custom_id").GetString() ?? "";
                var rt = cid.StartsWith("hb:ip:") ? Shown().FirstOrDefault(r => r.Config.Id == cid[6..]) : null;
                response = Reply(rt != null ? await AddressPayloadAsync(rt) : new { content = "That server isn't shared any more." });
            }
            if (response != null) await RestAsync(HttpMethod.Post, $"/interactions/{id}/{token}/callback", response);
        }
        catch { /* a failed reply just shows "interaction failed" in Discord */ }
    }

    private object Reply(object data)
    {
        // Merge the ephemeral flag into the message data.
        var node = JsonSerializer.SerializeToNode(data, Out)!.AsObject();
        if (S.PrivateReplies) node["flags"] = 64;
        return new { type = 4, data = node };
    }

    private static string? Option(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("options", out var opts)) return null;
        foreach (var o in opts.EnumerateArray())
            if (o.GetProperty("name").GetString() == name) return o.GetProperty("value").ToString();
        return null;
    }

    // ---------------- what gets shown ----------------

    private IEnumerable<ServerRuntime> Shown() =>
        _mgr.Runtimes.Where(r => S.AllServers || S.ServerIds.Contains(r.Config.Id))
            .Where(r => S.ShowOffline || r.State != ServerState.Stopped);

    private async Task<string?> HostAsync() => S.AddressHost.Trim() is { Length: > 0 } h ? h : await NetworkInfo.PublicIpAsync();

    private static (string Text, int Color, string Dot) Describe(ServerState s) => s switch
    {
        ServerState.Running => ("Online", 0x3BA55D, "🟢"),
        ServerState.Starting => ("Starting", 0xFAA61A, "🟡"),
        ServerState.Stopping => ("Stopping", 0xFAA61A, "🟡"),
        _ => ("Offline", 0x747F8D, "⚫"),
    };

    private static string Address(string? host, JoinDetails j) => host == null ? "" : j.Port is int p ? $"{host}:{p}" : host;

    private object Embed(ServerRuntime rt, string? host)
    {
        var cfg = rt.Config;
        var (text, color, dot) = Describe(rt.State);
        var join = JoinInfo.Resolve(cfg);
        string game = GameCatalog.Get(cfg.GameId).Name;
        string desc = $"{dot} **{text}**" + (cfg.GameId == "generic" ? "" : $" · {game}");
        if (rt.State == ServerState.Running && rt.StartedAt is DateTime t) desc += $" · started <t:{new DateTimeOffset(t).ToUnixTimeSeconds()}:R>";
        var fields = new List<object>();
        if (join.Code != null) fields.Add(new { name = join.CodeLabel ?? "Code", value = $"```{join.Code}```", inline = false });
        else if (host != null) fields.Add(new { name = "Address", value = $"```{Address(host, join)}```", inline = false });
        else fields.Add(new { name = "Address", value = "Couldn't look up the public IP right now.", inline = false });
        int? art = ArtCache.AppIdFor(cfg);
        return new
        {
            title = Trim(cfg.Name, 256),
            description = desc,
            color,
            fields,
            thumbnail = art is int a ? new { url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{a}/library_600x900.jpg" } : null,
        };
    }

    /// <summary>The board: one card per server, no buttons (an empty list removes buttons from older boards).</summary>
    private async Task<object> BoardPayloadAsync()
    {
        var host = await HostAsync();
        var shown = Shown().ToList();
        var embeds = shown.Take(10).Select(r => Embed(r, host)).ToList();
        if (embeds.Count == 0)
            embeds.Add(new { title = "Game servers", description = S.ShowOffline ? "No servers are shared yet." : "No servers are online right now.", color = 0x747F8D });
        return new
        {
            content = shown.Count > 10 ? $"Showing 10 of {shown.Count} servers. Use `/ip` for the rest." : "",
            embeds,
            components = Array.Empty<object>(),
        };
    }

    private async Task<object> AddressPayloadAsync(ServerRuntime rt)
    {
        var host = await HostAsync();
        var join = JoinInfo.Resolve(rt.Config);
        var (text, _, dot) = Describe(rt.State);
        string value = join.Code ?? (host != null ? Address(host, join) : "");
        if (value.Length == 0) return new { content = "Couldn't look up the server's address right now. Try again in a minute." };
        string what = join.Code != null ? (join.CodeLabel ?? "code") : "address";
        // The value alone in the message text, so "Copy Text" on mobile copies exactly it.
        return new { content = value, embeds = new[] { new { description = $"{dot} **{rt.Config.Name}** is {text.ToLowerInvariant()}. That's the {what} to join with.", color = Describe(rt.State).Color } } };
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------------- status board ----------------

    /// <summary>Lists the text channels the bot can see, per Discord server.</summary>
    public async Task<List<object>> ChannelsAsync()
    {
        var result = new List<object>();
        var guilds = await RestAsync(HttpMethod.Get, "/users/@me/guilds", null);
        foreach (var g in guilds.EnumerateArray())
        {
            string gid = g.GetProperty("id").GetString()!;
            var channels = new List<object>();
            try
            {
                var list = await RestAsync(HttpMethod.Get, $"/guilds/{gid}/channels", null);
                foreach (var c in list.EnumerateArray().Where(c => c.GetProperty("type").GetInt32() is 0 or 5)
                             .OrderBy(c => c.TryGetProperty("position", out var p) ? p.GetInt32() : 0))
                    channels.Add(new { id = c.GetProperty("id").GetString(), name = c.GetProperty("name").GetString() });
            }
            catch { }
            var roles = new List<object>();
            try
            {
                var list = await RestAsync(HttpMethod.Get, $"/guilds/{gid}/roles", null);
                foreach (var r in list.EnumerateArray()
                             .Where(r => r.GetProperty("id").GetString() != gid && !(r.TryGetProperty("managed", out var mg) && mg.GetBoolean()))
                             .OrderByDescending(r => r.TryGetProperty("position", out var p) ? p.GetInt32() : 0))
                    roles.Add(new { id = r.GetProperty("id").GetString(), name = r.GetProperty("name").GetString(), mentionable = r.TryGetProperty("mentionable", out var mn) && mn.GetBoolean() });
            }
            catch { }
            result.Add(new { id = gid, name = g.GetProperty("name").GetString(), channels, roles });
        }
        return result;
    }

    /// <summary>Posts the board in a channel (removing the old one if it moved) and keeps it updated.</summary>
    public async Task PostBoardAsync(string channelId, string channelName)
    {
        if (_token == null) throw new InvalidOperationException("Connect the bot first.");
        await _boardLock.WaitAsync();
        try
        {
            if (S.BoardMessageId.Length > 0 && S.BoardChannelId.Length > 0)
            {
                try { await RestAsync(HttpMethod.Delete, $"/channels/{S.BoardChannelId}/messages/{S.BoardMessageId}", null); } catch { }
            }
            var payload = await BoardPayloadAsync();
            JsonElement msg;
            try { msg = await RestAsync(HttpMethod.Post, $"/channels/{channelId}/messages", payload); }
            catch (DiscordException ex) { throw new InvalidOperationException(BoardProblem(ex)); }
            S.BoardChannelId = channelId;
            S.BoardChannelName = channelName;
            S.BoardMessageId = msg.GetProperty("id").GetString() ?? "";
            _mgr.Save();
            _lastBoard = JsonSerializer.Serialize(payload, Out);
            _boardError = null;
        }
        finally { _boardLock.Release(); }
    }

    /// <summary>Deletes the board message from Discord.</summary>
    public async Task RemoveBoardAsync()
    {
        await _boardLock.WaitAsync();
        try
        {
            if (S.BoardMessageId.Length > 0 && _token != null)
            {
                try { await RestAsync(HttpMethod.Delete, $"/channels/{S.BoardChannelId}/messages/{S.BoardMessageId}", null); }
                catch (DiscordException ex) when (ex.Status == 404) { }
            }
            S.BoardMessageId = ""; S.BoardChannelId = ""; S.BoardChannelName = "";
            _mgr.Save();
            _boardError = null;
        }
        finally { _boardLock.Release(); }
    }

    // ---------------- updates channel ----------------

    private DiscordUpdates U => S.Updates;
    private bool Shared(string? serverId) => serverId != null && (S.AllServers || S.ServerIds.Contains(serverId));

    private void OnActivity(ActivityEntry e)
    {
        if (_token == null || !S.Enabled || U.ChannelId.Length == 0) return;
        if (e.Kind == "network") { if (U.AddressChanges && S.AddressHost.Trim().Length == 0) _ = Task.Run(PostAddressChangeAsync); return; }
        if (!Shared(e.ServerId) || _mgr.Get(e.ServerId!) is not { } rt) return;
        string name = $"**{rt.Config.Name}**";
        switch (e.Kind)
        {
            case "crash" when U.Crashes:
                // A server that crashes again soon after gets one message, not one per crash.
                if (_crashPosted.TryGetValue(rt.Config.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(10)) return;
                _crashPosted[rt.Config.Id] = DateTime.UtcNow;
                _awaitingBack[rt.Config.Id] = 0;
                Post($"🔴 {name} crashed." + (rt.Config.AutoRestart ? " It's restarting by itself." : " It'll be back once the host restarts it."), 0xED4245, ping: true);
                break;
            case "crashloop" when U.Crashes:
                _awaitingBack.TryRemove(rt.Config.Id, out _);
                Post($"🔴 {name} keeps crashing, so it won't restart by itself. It'll be back once the host fixes it.", 0xED4245, ping: true);
                break;
            case "start":
                if (_awaitingBack.TryRemove(rt.Config.Id, out _) && U.Crashes) Post($"🟢 {name} is back online.", 0x3BA55D, server: rt);
                else if (U.StartStop && e.Who != "schedule") Post($"🟢 {name} is online.", 0x3BA55D, server: rt);
                break;
            case "stop" when U.StartStop && e.Who != "schedule":
                Post($"⚫ {name} went offline.", 0x747F8D);
                break;
            case "update" when U.GameUpdates && e.Level == "good":
                Post($"⬆️ {name} was updated to the newest version. If the game says your version doesn't match, update the game (Steam usually does it by itself).", 0x5865F2);
                break;
            case "schedule" when U.Restarts && e.Level != "bad" && (e.Text.EndsWith("Restarted.") || e.Text.EndsWith("Updated and started again.")):
                Post($"🟢 {name} is back online after its scheduled {(e.Text.Contains("Updated") ? "update" : "restart")}.", 0x3BA55D, server: rt);
                break;
        }
    }

    /// <summary>The scheduler is about to restart, stop or update a server: say so once, at the first warning.</summary>
    public void OnScheduledWarning(ServerRuntime rt, ScheduledTask t, int minutes)
    {
        if (_token == null || !S.Enabled || U.ChannelId.Length == 0 || !U.Restarts || !Shared(rt.Config.Id)) return;
        if (_warnPosted.TryGetValue(t.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(15)) return;
        _warnPosted[t.Id] = DateTime.UtcNow;
        var when = $"in {minutes} minute{(minutes == 1 ? "" : "s")}";
        Post(t.Action switch
        {
            "stop" => $"🟠 **{rt.Config.Name}** shuts down {when} (scheduled).",
            "update" => $"🟠 **{rt.Config.Name}** restarts {when} to update (scheduled). It's usually back within a few minutes.",
            _ => $"🟠 **{rt.Config.Name}** restarts {when} (scheduled). It's usually back within a minute or two.",
        }, 0xFAA61A);
    }

    private async Task PostAddressChangeAsync()
    {
        var host = await HostAsync();
        var lines = _mgr.Runtimes.Where(r => Shared(r.Config.Id)).Select(r => (r, j: JoinInfo.Resolve(r.Config)))
            .Where(x => x.j.Code == null && host != null).Select(x => $"**{x.r.Config.Name}**: `{Address(host, x.j)}`").ToList();
        if (lines.Count == 0) return;
        Post("🌐 The server address changed. Join with the new one:\n" + string.Join("\n", lines), 0x5865F2, ping: true);
        QueueBoardUpdate(force: true);
    }

    private void Post(string text, int color, bool ping = false, ServerRuntime? server = null) => _ = Task.Run(() => PostUpdateAsync(text, color, ping, server));

    /// <summary>Posts a message in the updates channel. Works while the gateway is reconnecting (it's a plain web request).</summary>
    public async Task PostUpdateAsync(string text, int color, bool ping = false, ServerRuntime? server = null)
    {
        if (_token == null || U.ChannelId.Length == 0) throw new InvalidOperationException("Pick an updates channel first.");
        var fields = new List<object>();
        if (server != null)
        {
            var j = JoinInfo.Resolve(server.Config);
            var host = j.Code == null ? await HostAsync() : null;
            if (j.Code != null) fields.Add(new { name = j.CodeLabel ?? "Code", value = $"```{j.Code}```", inline = false });
            else if (host != null) fields.Add(new { name = "Address", value = $"```{Address(host, j)}```", inline = false });
        }
        bool doPing = ping && U.PingRoleId.Length > 0;
        var payload = new
        {
            content = doPing ? $"<@&{U.PingRoleId}>" : "",
            embeds = new[] { new { description = Trim(text, 4000), color, fields = fields.Count > 0 ? fields : null, timestamp = DateTime.UtcNow.ToString("o") } },
            allowed_mentions = new { parse = Array.Empty<string>(), roles = doPing ? new[] { U.PingRoleId } : Array.Empty<string>() },
        };
        await _postLock.WaitAsync();
        try
        {
            await RestAsync(HttpMethod.Post, $"/channels/{U.ChannelId}/messages", payload);
            _updatesError = null;
        }
        catch (DiscordException ex) { _updatesError = BoardProblem(ex); Log("Couldn't post an update: " + _updatesError, true); throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { _updatesError = "Couldn't reach Discord to post an update."; Log(_updatesError, true); throw; }
        finally { _postLock.Release(); }
    }

    /// <summary>Called after the settings change: re-renders the board and the bot's status line.</summary>
    public void SettingsChanged() => QueueBoardUpdate(force: true);

    /// <summary>Coalesces bursts of changes into one edit a couple of seconds later.</summary>
    private void QueueBoardUpdate(bool force = false)
    {
        if (force) _lastBoard = "";
        if (Interlocked.Exchange(ref _boardPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            Interlocked.Exchange(ref _boardPending, 0);
            try { await UpdatePresenceAsync(); } catch { }
            await UpdateBoardAsync();
        });
    }

    private async Task UpdateBoardAsync()
    {
        if (_token == null || _state != "online" || S.BoardMessageId.Length == 0) return;
        await _boardLock.WaitAsync();
        try
        {
            if (S.BoardMessageId.Length == 0) return;
            var payload = await BoardPayloadAsync();
            var json = JsonSerializer.Serialize(payload, Out);
            if (json == _lastBoard) return; // nothing changed: don't spend rate limit
            try
            {
                await RestAsync(HttpMethod.Patch, $"/channels/{S.BoardChannelId}/messages/{S.BoardMessageId}", payload);
                _lastBoard = json;
                _boardError = null;
            }
            catch (DiscordException ex) when (ex.Status == 404)
            {
                S.BoardMessageId = "";
                _mgr.Save();
                _boardError = "The status board message was deleted in Discord. Post it again to bring it back.";
            }
            catch (DiscordException ex) { _boardError = BoardProblem(ex); }
        }
        catch { }
        finally { _boardLock.Release(); }
    }

    private static string BoardProblem(DiscordException ex) => ex.Code switch
    {
        50001 => "The bot can't see that channel. Give it the View Channel permission there.",
        50013 => "The bot isn't allowed to post in that channel. Give it Send Messages and Embed Links there.",
        10003 => "That channel doesn't exist any more.",
        _ => $"Discord said: {ex.Message}",
    };

    // ---------------- REST ----------------

    private async Task<JsonElement> RestAsync(HttpMethod method, string path, object? body, string? token = null)
    {
        token ??= _token ?? throw new InvalidOperationException("The bot isn't set up.");
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, ApiBase + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            req.Headers.UserAgent.ParseAdd("DiscordBot (https://github.com/emberkeep, 1.0) Emberkeep");
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Out), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                double wait = 1;
                try { using var d = JsonDocument.Parse(text); wait = d.RootElement.GetProperty("retry_after").GetDouble(); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0.2, 30)));
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                string message = $"{(int)resp.StatusCode} {resp.ReasonPhrase}"; int code = 0;
                try { using var d = JsonDocument.Parse(text); message = d.RootElement.GetProperty("message").GetString() ?? message; code = d.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : 0; } catch { }
                throw new DiscordException((int)resp.StatusCode, message, code);
            }
            if (string.IsNullOrWhiteSpace(text)) return default;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
    }

    public void Dispose()
    {
        _boardTimer.Dispose();
        _http.Dispose();
    }
}
