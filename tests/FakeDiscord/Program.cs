// A stand-in for Discord's REST API and gateway, so Emberkeep's bot can be tested without a real bot token.
// Usage: FakeDiscord [port]   then run Emberkeep with EMBERKEEP_DISCORD_API=http://127.0.0.1:<port>/api/v10
// Accepts the token "good-token-0123456789abcdef". Test hooks live under /_test.
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

int port = args.Length > 0 ? int.Parse(args[0]) : 9555;
const string Token = "good-token-0123456789abcdef";
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
var app = builder.Build();
app.UseWebSockets();

var log = new ConcurrentQueue<string>();
var messages = new ConcurrentDictionary<string, JsonNode>();   // id -> message (channel_id, payload)
var callbacks = new ConcurrentDictionary<string, JsonNode>();  // interaction id -> response
JsonNode? commands = null;
JsonNode? presence = null;
int identifies = 0, nextId = 1000, decodeErrors = 0;
WebSocket? socket = null;
var sendLock = new SemaphoreSlim(1, 1);
long seq = 0;

bool Authed(HttpContext c) => c.Request.Headers.Authorization == $"Bot {Token}";
IResult Unauthorized() => Results.Json(new { message = "401: Unauthorized", code = 0 }, statusCode: 401);
async Task<JsonNode?> Body(HttpContext c) => await JsonNode.ParseAsync(c.Request.Body);
async Task Send(object o)
{
    if (socket is not { State: WebSocketState.Open } ws) return;
    var b = JsonSerializer.SerializeToUtf8Bytes(o);
    await sendLock.WaitAsync();
    try { await ws.SendAsync(b, WebSocketMessageType.Text, true, CancellationToken.None); } finally { sendLock.Release(); }
}

app.Use(async (c, next) => { log.Enqueue($"{c.Request.Method} {c.Request.Path}"); await next(); });

var api = app.MapGroup("/api/v10");
api.MapGet("/gateway/bot", (HttpContext c) => Authed(c) ? Results.Ok(new { url = $"ws://127.0.0.1:{port}/gw" }) : Unauthorized());
api.MapGet("/users/@me", (HttpContext c) => Authed(c) ? Results.Ok(new { id = "999", username = "TestBot", discriminator = "0" }) : Unauthorized());
api.MapGet("/applications/@me", (HttpContext c) => Authed(c) ? Results.Ok(new { id = "1111", name = "Game Servers" }) : Unauthorized());
api.MapPut("/applications/{id}/commands", async (HttpContext c) => { if (!Authed(c)) return Unauthorized(); commands = await Body(c); return Results.Ok(commands); });
api.MapGet("/users/@me/guilds", (HttpContext c) => Authed(c) ? Results.Ok(new[] { new { id = "g1", name = "Friends Server" } }) : Unauthorized());
api.MapGet("/guilds/{gid}/channels", (HttpContext c) => Authed(c) ? Results.Ok(new object[]
{
    new { id = "c2", name = "Voice", type = 2, position = 0 },
    new { id = "c3", name = "server-status", type = 0, position = 1 },
    new { id = "c1", name = "general", type = 0, position = 0 },
    new { id = "c4", name = "locked", type = 0, position = 2 },
}) : Unauthorized());
api.MapPost("/channels/{cid}/messages", async (string cid, HttpContext c) =>
{
    if (!Authed(c)) return Unauthorized();
    if (cid == "c4") return Results.Json(new { message = "Missing Permissions", code = 50013 }, statusCode: 403);
    var id = "m" + Interlocked.Increment(ref nextId);
    messages[id] = new JsonObject { ["channel_id"] = cid, ["payload"] = await Body(c), ["edits"] = 0 };
    return Results.Ok(new { id, channel_id = cid });
});
api.MapMethods("/channels/{cid}/messages/{mid}", ["PATCH"], async (string cid, string mid, HttpContext c) =>
{
    if (!Authed(c)) return Unauthorized();
    if (!messages.TryGetValue(mid, out var m)) return Results.Json(new { message = "Unknown Message", code = 10008 }, statusCode: 404);
    m["payload"] = await Body(c); m["edits"] = m["edits"]!.GetValue<int>() + 1;
    return Results.Ok(new { id = mid, channel_id = cid });
});
api.MapDelete("/channels/{cid}/messages/{mid}", (string cid, string mid, HttpContext c) =>
    !Authed(c) ? Unauthorized() : messages.TryRemove(mid, out _) ? Results.NoContent() : Results.Json(new { message = "Unknown Message", code = 10008 }, statusCode: 404));
api.MapPost("/interactions/{iid}/{tok}/callback", async (string iid, HttpContext c) => { callbacks[iid] = (await Body(c))!; return Results.NoContent(); });

app.Map("/gw", async (HttpContext c) =>
{
    if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
    using var ws = await c.WebSockets.AcceptWebSocketAsync();
    socket = ws;
    await Send(new { op = 10, d = new { heartbeat_interval = 5000 } });
    var buf = new byte[65536];
    while (ws.State == WebSocketState.Open)
    {
        WebSocketReceiveResult r;
        using var ms = new MemoryStream();
        try { do { r = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage); }
        catch { break; }
        if (r.MessageType == WebSocketMessageType.Close) break;
        var msg = JsonNode.Parse(ms.ToArray())!;
        int op = msg["op"]!.GetValue<int>();
        // Like the real gateway, required fields must be present even when null; otherwise close with 4002.
        static bool Has(JsonNode? n, params string[] keys) => n is JsonObject o && keys.All(o.ContainsKey);
        bool valid = op switch
        {
            1 => Has(msg, "op", "d"),
            2 => Has(msg["d"], "token", "intents", "properties"), // the real gateway accepted our sign-in, so stay lenient here
            3 => Has(msg["d"], "since", "activities", "status", "afk"),
            _ => true,
        };
        if (!valid)
        {
            decodeErrors++;
            await ws.CloseAsync((WebSocketCloseStatus)4002, "Error while decoding payload.", CancellationToken.None);
            break;
        }
        if (op == 1) await Send(new { op = 11 });
        else if (op == 2)
        {
            if (msg["d"]!["token"]?.GetValue<string>() != Token) { await ws.CloseAsync((WebSocketCloseStatus)4004, "Authentication failed.", CancellationToken.None); break; }
            identifies++;
            presence = msg["d"]!["presence"]?.DeepClone();
            await Send(new { op = 0, t = "READY", s = ++seq, d = new { v = 10, session_id = "sess", user = new { id = "999", username = "TestBot" }, application = new { id = "1111" }, guilds = new[] { new { id = "g1", unavailable = true } } } });
            await Send(new { op = 0, t = "GUILD_CREATE", s = ++seq, d = new { id = "g1", name = "Friends Server" } });
        }
        else if (op == 3) presence = msg["d"]!.DeepClone();
    }
    socket = null;
});

// Sends an interaction to the bot and waits for its reply.
app.MapPost("/_test/interaction", async (HttpContext c) =>
{
    var d = (await Body(c))!.AsObject();
    var id = "i" + Interlocked.Increment(ref nextId);
    d["id"] = id; d["token"] = "tok" + id; d["application_id"] = "1111";
    await Send(new { op = 0, t = "INTERACTION_CREATE", s = ++seq, d });
    for (int i = 0; i < 60 && !callbacks.ContainsKey(id); i++) await Task.Delay(100);
    return callbacks.TryGetValue(id, out var resp) ? Results.Ok(resp) : Results.Json(new { error = "no reply within 6s" }, statusCode: 504);
});
app.MapGet("/_test/state", () => Results.Ok(new
{
    connected = socket?.State == WebSocketState.Open,
    identifies,
    decodeErrors,
    commands,
    presence,
    messages = messages.ToDictionary(kv => kv.Key, kv => kv.Value),
    log = log.TakeLast(40),
}));
// Drops the gateway connection without a close frame, to test reconnecting.
app.MapPost("/_test/drop", () => { socket?.Abort(); return Results.Ok(); });
// Deletes a message as if someone removed it in Discord.
app.MapPost("/_test/delete/{mid}", (string mid) => messages.TryRemove(mid, out _) ? Results.Ok() : Results.NotFound());

Console.WriteLine($"FakeDiscord on http://127.0.0.1:{port}");
app.Run();
