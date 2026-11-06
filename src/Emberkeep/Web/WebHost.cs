using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using Emberkeep.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Emberkeep.Web;

/// <summary>Runs the embedded web server that serves the UI and API to the desktop window and (optionally) the LAN.</summary>
public sealed class WebHost
{
    private readonly ServerManager _mgr;
    private WebApplication? _app;

    public WebHost(ServerManager mgr)
    {
        _mgr = mgr;
        Auth = new AuthService(mgr);
        Ports = new RouterPortService(mgr);
        Discord = new DiscordBot(mgr);
        Learn = new LauncherLearn(mgr);
        Updates = new UpdateChecker(mgr);
        PcRestart = new PcRestart(mgr);
    }

    /// <summary>"Restart PC now" and the daily restart.</summary>
    public PcRestart PcRestart { get; }

    /// <summary>Looks for a newer Emberkeep on GitHub.</summary>
    public UpdateChecker Updates { get; }

    public AuthService Auth { get; }
    /// <summary>Opens Emberkeep's own port on the router (UPnP) when internet access is on.</summary>
    public PortForwarder Forwarder { get; } = new();
    /// <summary>Opens servers' ports on the router, through Watchfire when available.</summary>
    public RouterPortService Ports { get; }
    /// <summary>Optional Discord bot that shares server status and join addresses.</summary>
    public DiscordBot Discord { get; }
    /// <summary>"Learn from launcher": captures how a game's own launcher starts its server.</summary>
    public LauncherLearn Learn { get; }
    public int Port { get; private set; }
    public bool ListeningOnLan { get; private set; }
    public string LocalUrl => $"http://127.0.0.1:{Port}";
    public string LocalLoginUrl => $"{LocalUrl}/auth/local?t={Auth.LocalToken}";

    /// <summary>Raised after a restart, e.g. when the port changed and the desktop window must reconnect.</summary>
    public event Action<bool>? Restarted; // true when the port changed, so the window must move to the new address

    public async Task StartAsync()
    {
        bool lan = _mgr.Settings.RemoteAccess && Auth.HasAccounts;
        Exception? last = null;
        int basePort = _mgr.Settings.Port is > 0 and < 65536 ? _mgr.Settings.Port : 8765;
        for (int port = basePort; port < basePort + 20; port++)
        {
            try
            {
                var app = Build(port, lan);
                await app.StartAsync();
                if (!await AnswersLocally(port))
                {
                    // Listening on all addresses can succeed while another program holds 127.0.0.1 on the same port; the
                    // window would then reach that program and be refused. Use the next port instead.
                    using (var cts = new System.Threading.CancellationTokenSource(2000)) await app.StopAsync(cts.Token);
                    await app.DisposeAsync();
                    last = new IOException($"Another program answers on 127.0.0.1:{port}.");
                    continue;
                }
                _app = app; Port = port; ListeningOnLan = lan;
                ApplyForwarding();
                Discord.Start();
                Updates.Start();
                PcRestart.Start();
                _ = Task.Run(() => ArtCache.AutoDetectAsync(_mgr)); // once per run: art for custom servers that have none
                return;
            }
            catch (Exception ex) when (ex is IOException || ex.InnerException is SocketException || ex is SocketException)
            {
                last = ex; // port busy: try the next one
            }
        }
        throw new InvalidOperationException("Couldn't open a port for the Emberkeep UI.", last);
    }

    // A random value only this copy of Emberkeep knows, to recognise its own answer on 127.0.0.1.
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private const string InstancePath = "/auth/instance";

    private async Task<bool> AnswersLocally(int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var answer = await http.GetStringAsync($"http://127.0.0.1:{port}{InstancePath}");
            return answer == _instanceId;
        }
        catch { return false; }
    }

    public async Task RestartAsync()
    {
        int oldPort = Port;
        var old = _app;
        _app = null;
        if (old != null)
        {
            using (var cts = new System.Threading.CancellationTokenSource(2000)) await old.StopAsync(cts.Token);
            await old.DisposeAsync();
        }
        await StartAsync();
        Restarted?.Invoke(Port != oldPort);
    }

    public async Task StopAsync()
    {
        await Forwarder.CloseAsync(); // take the port back off the router on exit
        await Discord.StopAsync();
        if (_app != null) { using (var cts = new System.Threading.CancellationTokenSource(2000)) await _app.StopAsync(cts.Token); await _app.DisposeAsync(); _app = null; }
    }

    /// <summary>Opens or closes the router port to match the settings: needs remote access, a long password and the switch on.</summary>
    public void ApplyForwarding()
    {
        var s = _mgr.Settings;
        bool want = s.InternetAccess && Auth.AllPasswordsLong && ListeningOnLan;
        if (want) Forwarder.Open(Port);
        else if (Forwarder.Status.State != "off") _ = Forwarder.CloseAsync();
    }

    private WebApplication Build(int port, bool lan)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (lan) k.ListenAnyIP(port);
            else k.Listen(IPAddress.Loopback, port);
            k.AddServerHeader = false;
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = ServerManager.Json.PropertyNamingPolicy;
            foreach (var c in ServerManager.Json.Converters) o.SerializerOptions.Converters.Add(c);
        });

        var app = builder.Build();
        app.Use(AuthMiddleware);
        Api.Map(app, _mgr, this);
        app.MapFallback("{**path}", ServeStatic);
        return app;
    }

    // ---------- auth ----------

    private static readonly string[] PublicPaths = ["/login", "/login.html", "/app.css", "/theme.js", "/favicon.svg", "/fonts/"];

    private async Task AuthMiddleware(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var remote = ctx.Connection.RemoteIpAddress;
        bool loopback = remote != null && IPAddress.IsLoopback(remote);

        if (path == InstancePath)
        {
            // Only from this PC: lets Emberkeep check at startup that 127.0.0.1 really reaches it.
            if (loopback) await ctx.Response.WriteAsync(_instanceId);
            else ctx.Response.StatusCode = 404;
            return;
        }

        if (path == "/auth/local")
        {
            if (loopback && Auth.CheckLocalToken(ctx.Request.Query["t"]))
            {
                SetSessionCookie(ctx, Auth.CreateLocalSession());
                string target = ctx.Request.Query["next"].ToString();
                // Only same-site relative paths, never "//host" or absolute URLs.
                ctx.Response.Redirect(target.StartsWith('/') && !target.StartsWith("//") && !target.Contains('\\') ? target : "/");
            }
            else if (loopback)
            {
                // An old link (the token changes every launch), e.g. from browser history or a restored tab. Instead of a
                // bare "access denied": carry on if this browser is still signed in, otherwise explain on the sign-in page.
                ctx.Response.Redirect(Auth.Resolve(AuthService.SessionCookie(ctx)) != null ? "/" : "/login?expired=1");
            }
            else ctx.Response.StatusCode = 403;
            return;
        }

        // State-changing requests must carry a custom header, which other websites can't add cross-origin.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            && ctx.Request.Headers["X-Emberkeep"] != "1")
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        if (path == "/auth/login" && HttpMethods.IsPost(ctx.Request.Method))
        {
            var body = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            var (session, err) = Auth.Login(body?.Username ?? "", body?.Password ?? "", remote);
            if (session != null) { SetSessionCookie(ctx, session); await ctx.Response.WriteAsJsonAsync(new { ok = true }); }
            else { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = err }); }
            return;
        }
        if (path == "/auth/logout")
        {
            Auth.EndSession(AuthService.SessionCookie(ctx));
            ctx.Response.Cookies.Delete(AuthService.CookieName(ctx));
            ctx.Response.Redirect("/login");
            return;
        }

        if (PublicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || (p.EndsWith('/') && path.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
        {
            await next();
            return;
        }

        // Everything else (the app page, its script, all data) needs a signed-in account. The account and its
        // rights are looked up again on every request, and endpoints check them before doing anything.
        var principal = Auth.Resolve(AuthService.SessionCookie(ctx));
        if (principal == null)
        {
            if (path.StartsWith("/api/")) { ctx.Response.StatusCode = 401; return; }
            ctx.Response.Redirect("/login");
            return;
        }
        ctx.Items[PrincipalKey] = principal;
        await next();
    }

    public const string PrincipalKey = "hb.principal";

    /// <summary>The signed-in account for this request (set by the auth middleware).</summary>
    public static Principal Who(HttpContext ctx) => (Principal)ctx.Items[PrincipalKey]!;

    public static void SetSessionCookie(HttpContext ctx, string session) =>
        ctx.Response.Cookies.Append(AuthService.CookieName(ctx), session, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(30),
        });

    private sealed record LoginRequest(string? Username, string? Password);

    // ---------- static files (embedded) ----------

    private static async Task ServeStatic(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path == "/" || path == "") path = "/index.html";
        if (path == "/login") path = "/login.html";
        var name = "wwwroot" + path;
        var asm = Assembly.GetExecutingAssembly();
        // Embedded resource names keep the folder separator MSBuild produced.
        using var stream = asm.GetManifestResourceStream(name) ?? asm.GetManifestResourceStream(name.Replace('/', '\\').Replace("wwwroot\\", "wwwroot/"));
        if (stream == null) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        await stream.CopyToAsync(ctx.Response.Body);
    }
}
