using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>
/// Game artwork from Steam's CDN, cached in %APPDATA%\Emberkeep\art so it loads instantly, works offline
/// and is served to remote browsers by Emberkeep itself.
/// </summary>
public static class ArtCache
{
    /// <summary>Art kinds the UI asks for, mapped to Steam's file names.</summary>
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["header"] = "header.jpg",        // 460x215 store header
        ["hero"] = "library_hero.jpg",    // 3840x1240 library banner
        ["cover"] = "library_600x900.jpg",// portrait library cover
        ["logo"] = "logo.png",            // transparent game logo
    };

    private static readonly string Dir = Path.Combine(ServerManager.DataDir, "art");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(1);

    /// <summary>Which Steam app's art to show for a server: its override, else its game's, else its own app ID.</summary>
    public static int? AppIdFor(ServerInstance s) =>
        s.ArtAppId is > 0 ? s.ArtAppId : GameCatalog.Get(s.GameId).ArtAppId ?? (s.GameId == "generic" ? s.SteamAppId : null);

    /// <summary>Returns the cached file path, downloading it first if needed. Null if Steam has no such image.</summary>
    public static async Task<string?> GetAsync(int appId, string kind)
    {
        if (appId <= 0 || !Kinds.TryGetValue(kind, out var file)) return null;
        var folder = Path.Combine(Dir, appId.ToString());
        var path = Path.Combine(folder, file);
        var missing = path + ".missing";
        if (File.Exists(path)) return path;
        if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < RetryMissingAfter) return null;

        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(folder);
            var resp = await Http.GetAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{file}");
            if (resp.StatusCode == HttpStatusCode.NotFound && kind == "header" && await StoreHeaderUrlAsync(appId) is string storeUrl)
            {
                // Newer apps keep their art at hashed URLs; the store API knows the header's real address.
                resp.Dispose();
                resp = await Http.GetAsync(storeUrl);
            }
            using var _ = resp;
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Remember the miss so we don't ask Steam again on every page load.
                await File.WriteAllTextAsync(missing, "");
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null; // network trouble: try again next time
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            File.Delete(missing);
            return path;
        }
        catch { return null; }
        finally { gate.Release(); }
    }

    private static async Task<string?> StoreHeaderUrlAsync(int appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic"));
            var entry = doc.RootElement.GetProperty(appId.ToString());
            if (!entry.GetProperty("success").GetBoolean()) return null;
            var url = entry.GetProperty("data").GetProperty("header_image").GetString();
            // Only follow URLs on Steam's own CDN.
            return url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
                && (u.Host.EndsWith(".steamstatic.com") || u.Host.EndsWith(".steampowered.com")) ? url : null;
        }
        catch { return null; }
    }

    private static int _startupScanDone;

    /// <summary>
    /// Custom servers have no catalog art, and the Steam app they came from is usually a dedicated-server tool with no
    /// pictures. For those, look the game up on the Steam store by the server's (tidied) name and use its art.
    /// With no <paramref name="onlyId"/>, checks every custom server once per run.
    /// </summary>
    public static async Task AutoDetectAsync(ServerManager mgr, string? onlyId = null)
    {
        if (onlyId == null && Interlocked.Exchange(ref _startupScanDone, 1) == 1) return;
        var targets = mgr.Settings.Servers.Where(s => (onlyId == null || s.Id == onlyId)
            && s.ArtAppId is null && GameCatalog.Get(s.GameId).ArtAppId is null).ToList();
        foreach (var s in targets)
        {
            try
            {
                if (s.SteamAppId is int own && await GetAsync(own, "header") != null) continue; // its own app has art: keep it
                if (await FindGameAsync(s.Name) is int found) mgr.SetArt(s.Id, found);
            }
            catch { }
        }
    }

    /// <summary>The Steam game best matching a server name, ignoring "Dedicated Server" and similar. Null if nothing is a clear match.</summary>
    public static async Task<int?> FindGameAsync(string serverName)
    {
        var name = ServerNames.Clean(serverName);
        static string N(string x) => System.Text.RegularExpressions.Regex.Replace(x.ToLowerInvariant(), "[^a-z0-9]", "");
        var want = N(name);
        if (want.Length < 3) return null;
        List<SearchResult> results;
        try { results = await SearchAsync(name); } catch { return null; }
        // Exact name first; otherwise a game whose name starts with it, skipping soundtracks, demos, servers and the like.
        var extras = new System.Text.RegularExpressions.Regex(@"soundtrack|\bost\b|\bdlc\b|demo|playtest|dedicated|\bserver\b|artbook|season pass|supporter", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var hit = results.FirstOrDefault(r => N(r.Name) == want)
               ?? results.FirstOrDefault(r => !extras.IsMatch(r.Name) && N(r.Name).StartsWith(want));
        return hit?.Id;
    }

    public sealed record SearchResult(int Id, string Name);

    /// <summary>Searches the Steam store by name, so custom servers can pick their game's art.</summary>
    public static async Task<List<SearchResult>> SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return new();
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term.Trim())}&cc=US&l=en";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("items", out var items)) return new();
        return items.EnumerateArray()
            .Where(i => i.TryGetProperty("type", out var t) && t.GetString() == "app")
            .Select(i => new SearchResult(i.GetProperty("id").GetInt32(), i.GetProperty("name").GetString() ?? ""))
            .Take(10)
            .ToList();
    }
}
