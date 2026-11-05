using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Hostbay.Core;

/// <summary>What Hostbay knows about PortHop on this PC.</summary>
public sealed record PortHopInfo(bool Installed, bool Running, string? Version, string? ExePath, int? Port);

/// <summary>One port as PortHop reports it from its router snapshot.</summary>
public sealed record PortHopPort(int Port, string Protocol, bool Enabled, bool Ours, string Name);

public sealed record PortHopForwarded(string Router, bool RouterFound, string? LocalIp, string ExternalIp, List<PortHopPort> Ports, string? Error);

public sealed record PortHopResult(int Port, string Protocol, bool Ok, bool Skipped, string? Error);

/// <summary>
/// Talks to PortHop, the stand-alone port forwarding app, through its local API (the same one its own window uses).
/// PortHop writes its port to %LOCALAPPDATA%\PortHop\instance.json; requests must come from 127.0.0.1 with an
/// X-PortHop header. When PortHop is installed but closed, Hostbay starts it in the background (tray only).
/// </summary>
public static class PortHopClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Same lookup PortHop uses (process.env.LOCALAPPDATA), so both always agree on where its instance file is.
    private static string LocalAppData =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } env ? env : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string DataDir => Path.Combine(LocalAppData, "PortHop");

    /// <summary>Where PortHop.exe is, if anywhere: next to Hostbay's install, its own install, or its "start with Windows" entry.</summary>
    public static string? FindExe()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("HOSTBAY_PORTHOP_EXE"),
            Path.Combine(LocalAppData, "Programs", "PortHop", "PortHop.exe"),
            Path.Combine(AppContext.BaseDirectory, "PortHop.exe"),
            Path.Combine(AppContext.BaseDirectory, "PortHop", "PortHop.exe"),
        };
        try
        {
            // "Start with Windows" stores: "C:\path\PortHop.exe" --background
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "PortHop", null) is string run)
            {
                var m = System.Text.RegularExpressions.Regex.Match(run, "^\"([^\"]+)\"");
                candidates.Add(m.Success ? m.Groups[1].Value : run.Split(' ')[0]);
            }
        }
        catch { }
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <summary>The port of a running PortHop that answers as PortHop, or null.</summary>
    private static async Task<(int Port, string? Version)?> RunningAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(DataDir, "instance.json")));
            int port = doc.RootElement.GetProperty("port").GetInt32();
            if (port <= 0 || port > 65535) return null;
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var hello = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/api/hello", cts.Token));
            if (hello.RootElement.GetProperty("app").GetString() != "porthop") return null;
            // Older PortHop builds have no api/version fields but forward and remove the same way.
            string? ver = hello.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            return (port, ver);
        }
        catch { return null; }
    }

    public static async Task<PortHopInfo> InfoAsync()
    {
        var running = await RunningAsync();
        var exe = FindExe();
        return new PortHopInfo(exe != null || running != null, running != null, running?.Version, exe, running?.Port);
    }

    /// <summary>Returns PortHop's port, starting it in the background first if it's installed but not running. Null if unavailable.</summary>
    public static async Task<int?> EnsureRunningAsync()
    {
        if (await RunningAsync() is { } r) return r.Port;
        var exe = FindExe();
        if (exe == null) return null;
        try { Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }); }
        catch { return null; }
        for (int i = 0; i < 40; i++) // up to 20 s: first launch of the packaged exe unpacks itself
        {
            await Task.Delay(500);
            if (await RunningAsync() is { } started) return started.Port;
        }
        return null;
    }

    /// <summary>Opens PortHop's own window (starting it if needed).</summary>
    public static async Task<bool> ShowAsync()
    {
        if (await RunningAsync() is { } r) { await PostAsync(r.Port, "/api/show", new { }); return true; }
        var exe = FindExe();
        if (exe == null) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        return true;
    }

    public static async Task<PortHopForwarded?> ForwardedAsync()
    {
        if (await RunningAsync() is not { } r) return null;
        try { return await Http.GetFromJsonAsync<PortHopForwarded>($"http://127.0.0.1:{r.Port}/api/forwarded", Json); }
        catch { return null; } // older PortHop without /api/forwarded
    }

    public static Task<List<PortHopResult>> ForwardAsync(int port, string name, IEnumerable<PortEntry> entries) =>
        ChangeAsync(port, "/api/forward", name, entries);

    public static Task<List<PortHopResult>> RemoveAsync(int port, string name, IEnumerable<PortEntry> entries) =>
        ChangeAsync(port, "/api/remove", name, entries);

    private static async Task<List<PortHopResult>> ChangeAsync(int port, string path, string name, IEnumerable<PortEntry> entries)
    {
        var body = new { name, entries = entries.Select(e => new { proto = e.Protocol, range = e.Range }).ToArray() };
        using var doc = await PostAsync(port, path, body);
        if (doc.RootElement.TryGetProperty("error", out var err)) throw new InvalidOperationException("PortHop: " + err.GetString());
        return doc.RootElement.GetProperty("results").EnumerateArray().Select(r => new PortHopResult(
            r.GetProperty("port").GetInt32(), r.GetProperty("protocol").GetString() ?? "",
            r.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
            r.TryGetProperty("skipped", out var sk) && sk.GetBoolean(),
            r.TryGetProperty("error", out var e) ? e.GetString() : null)).ToList();
    }

    private static async Task<JsonDocument> PostAsync(int port, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-PortHop", "1"); // PortHop's guard against requests from web pages
        using var resp = await Http.SendAsync(req);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }
}
