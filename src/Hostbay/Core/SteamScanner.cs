using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Hostbay.Core;

public sealed record ScanCandidate(
    string GameId, string GameName, string Name, string InstallDir, int? SteamAppId, bool AlreadyAdded, string Source);

/// <summary>Finds dedicated servers installed through Steam (including the Tools library).</summary>
public static class SteamScanner
{
    public static string? FindSteamRoot()
    {
        string? p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                 ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                 ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;
        if (p != null)
        {
            p = Path.GetFullPath(p.Replace('/', '\\'));
            if (Directory.Exists(p)) return p;
        }
        foreach (var guess in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (Directory.Exists(guess)) return guess;
        return null;
    }

    public static List<string> FindLibraries(string steamRoot)
    {
        var libs = new List<string> { steamRoot };
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            try
            {
                var root = Vdf.Parse(File.ReadAllText(vdfPath));
                var folders = root.Child("libraryfolders");
                if (folders != null)
                {
                    foreach (var f in folders.Children.Values)
                    {
                        var path = f.Value("path");
                        if (!string.IsNullOrEmpty(path)) libs.Add(Path.GetFullPath(path));
                    }
                }
            }
            catch { /* malformed vdf: fall back to the main library only */ }
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    public static List<ScanCandidate> Scan(IEnumerable<ServerInstance> existing)
    {
        var results = new List<ScanCandidate>();
        var root = FindSteamRoot();
        if (root == null) return results;
        var known = new HashSet<string>(existing.Select(e => Normalize(e.InstallDir)), StringComparer.OrdinalIgnoreCase);

        foreach (var lib in FindLibraries(root))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); } catch { continue; }

            foreach (var mf in manifests)
            {
                VdfNode? app;
                try { app = Vdf.Parse(File.ReadAllText(mf)).Child("AppState"); } catch { continue; }
                if (app == null) continue;
                if (!int.TryParse(app.Value("appid"), out int appId)) continue;
                var name = app.Value("name") ?? $"App {appId}";
                var installdir = app.Value("installdir");
                if (string.IsNullOrEmpty(installdir)) continue;
                var dir = Path.Combine(steamapps, "common", installdir);
                if (!Directory.Exists(dir)) continue;

                var game = GameCatalog.BySteamAppId(appId);
                // Terraria's app is the game itself; only offer it if the server binary is there.
                if (game != null && game.DetectFiles.Length > 0 && !game.DetectFiles.Any(f => File.Exists(Path.Combine(dir, f))))
                    game = null;
                if (game == null)
                {
                    // Not in the catalog, but looks like a server: offer it as a custom server.
                    bool looksLikeServer = name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase)
                                        || name.EndsWith(" Server", StringComparison.OrdinalIgnoreCase)
                                        || name.Contains(" DS", StringComparison.Ordinal);
                    if (!looksLikeServer) continue;
                    game = GameCatalog.DetectInFolder(dir).FirstOrDefault() ?? GameCatalog.Get("generic");
                }
                results.Add(new ScanCandidate(game.Id, game.Name, ServerNames.Clean(name), dir, appId, known.Contains(Normalize(dir)), "Steam"));
            }
        }
        return results.OrderBy(r => r.AlreadyAdded).ThenBy(r => r.Name).ToList();
    }

    public static List<ScanCandidate> ScanFolder(string dir, IEnumerable<ServerInstance> existing)
    {
        var known = new HashSet<string>(existing.Select(e => Normalize(e.InstallDir)), StringComparer.OrdinalIgnoreCase);
        var games = GameCatalog.DetectInFolder(dir);
        if (games.Count == 0) games.Add(GameCatalog.Get("generic"));
        var folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return games.Select(g => new ScanCandidate(g.Id, g.Name, g.Id == "generic" ? ServerNames.Clean(folderName) : g.Name, dir,
            g.SteamAppIds.FirstOrDefault() is var a && a != 0 ? a : null, known.Contains(Normalize(dir)), "Folder")).ToList();
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p; }
    }
}

/// <summary>Minimal parser for Valve's text KeyValues (.vdf/.acf) format.</summary>
public sealed class VdfNode
{
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VdfNode? Child(string key) => Children.GetValueOrDefault(key);
    public string? Value(string key) => Values.GetValueOrDefault(key);
}

public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        int i = 0;
        var root = new VdfNode();
        ParseInto(root, text, ref i);
        return root;
    }

    private static void ParseInto(VdfNode node, string s, ref int i)
    {
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return;
            if (s[i] == '}') { i++; return; }
            string key = ReadToken(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '{')
            {
                i++;
                var child = new VdfNode();
                ParseInto(child, s, ref i);
                node.Children[key] = child;
            }
            else
            {
                node.Values[key] = ReadToken(s, ref i);
            }
        }
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) i++;
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; }
            else break;
        }
    }

    private static string ReadToken(string s, ref int i)
    {
        var sb = new StringBuilder();
        if (i < s.Length && s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                }
                else sb.Append(s[i]);
                i++;
            }
            i++; // closing quote
        }
        else
        {
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}') sb.Append(s[i++]);
        }
        return sb.ToString();
    }
}
