using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emberkeep.Core;

public sealed record ConfigFileInfo(string Path, string DisplayPath, string Name, string Format, bool Known, long Size, DateTime Modified);

/// <summary>Finds config files for a server: the game's known locations first, then a heuristic scan of its folder.</summary>
public static class ConfigDiscovery
{
    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".json", ".cfg", ".properties", ".xml", ".yml", ".yaml", ".toml", ".conf", ".lua", ".txt" };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "log", "crashes", "crash-reports", "cache", "libraries", "versions", "node_modules", "__pycache__",
        "Engine", "Content", "Paks", "Movies", "MonoBleedingEdge", "Managed", "il2cpp_data", "Plugins", "Binaries",
        "steamapps", "steamcmd", "redist", "_CommonRedist", "D3D12", "Mono", "jre", "jdk", "java", "jre64",
        "world", "world_nether", "world_the_end", "save-data\\Saves", "Saves", "backups", "Backup", "Data", "Mods",
    };

    // Plain .txt files only count when the name looks config-ish.
    private static readonly string[] TxtHints = ["config", "setting", "admin", "ban", "whitelist", "allowlist", "permitted", "ops", "eula", "jvm", "motd"];

    private const long MaxSize = 2 * 1024 * 1024;

    public static List<ConfigFileInfo> Discover(ServerInstance s)
    {
        var game = GameCatalog.Get(s.GameId);
        var found = new Dictionary<string, ConfigFileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in game.ConfigFiles)
        {
            foreach (var path in ExpandPattern(pattern, s.InstallDir))
                Add(found, path, s.InstallDir, known: true);
        }

        // JSON files that the launch arguments pass encoded (see ArgBlobs) are this server's main config.
        foreach (var path in ArgBlobs.PlaceholderFiles(s.Arguments, s.InstallDir))
            if (File.Exists(path)) Add(found, path, s.InstallDir, known: true);

        if (Directory.Exists(s.InstallDir))
        {
            foreach (var path in HeuristicScan(s.InstallDir, depth: 0, maxDepth: 5, budget: new int[] { 4000 }))
                Add(found, path, s.InstallDir, known: false);
        }

        // Known files keep the game's own order (most important first); discovered ones go shallowest first.
        var known = found.Values.Where(f => f.Known);
        var other = found.Values.Where(f => !f.Known)
            .OrderBy(f => f.DisplayPath.Count(c => c == '\\'))
            .ThenBy(f => f.DisplayPath, StringComparer.OrdinalIgnoreCase);
        return known.Concat(other).ToList();
    }

    /// <summary>True if <paramref name="path"/> is one of this server's discovered config files. Guards the file API.</summary>
    public static bool IsAllowed(ServerInstance s, string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        return Discover(s).Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))
            || TemplateDestination(s) is string dest && string.Equals(dest, full, StringComparison.OrdinalIgnoreCase);
    }

    public static string? TemplateDestination(ServerInstance s)
    {
        var t = GameCatalog.Get(s.GameId).DefaultConfigTemplate;
        return t == null ? null : Path.GetFullPath(Path.Combine(s.InstallDir, t.Value.Destination));
    }

    /// <summary>If the game ships a defaults file but the real config is missing or empty, says so.</summary>
    public static (string Source, string Destination)? MissingTemplate(ServerInstance s)
    {
        var t = GameCatalog.Get(s.GameId).DefaultConfigTemplate;
        if (t == null) return null;
        var src = Path.Combine(s.InstallDir, t.Value.Source);
        var dst = Path.Combine(s.InstallDir, t.Value.Destination);
        if (!File.Exists(src)) return null;
        if (File.Exists(dst) && new FileInfo(dst).Length > 10) return null;
        return (src, dst);
    }

    public static string DetectFormat(string path, string? content = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == "palworldsettings.ini" || name == "defaultpalworldsettings.ini") return "palworld";
        return ext switch
        {
            ".ini" => "ini",
            ".properties" => "properties",
            ".json" => "json",
            ".cfg" when content != null && content.TrimStart().StartsWith('{') => "json",
            ".cfg" => "ini",
            ".xml" => "xml",
            ".yml" or ".yaml" => "yaml",
            ".toml" => "ini",
            ".lua" => "lua",
            _ when name == "serverconfig.txt" || name == "eula.txt" || name == "user_jvm_args.txt" => "properties",
            _ => "text",
        };
    }

    private static void Add(Dictionary<string, ConfigFileInfo> found, string path, string installDir, bool known)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return; }
        if (found.ContainsKey(full)) return;
        try
        {
            var fi = new FileInfo(full);
            if (!fi.Exists || fi.Length > MaxSize) return;
            string display = full;
            if (!string.IsNullOrEmpty(installDir))
            {
                var rel = Path.GetRelativePath(installDir, full);
                if (!rel.StartsWith("..") && !Path.IsPathRooted(rel)) display = rel;
            }
            display = display.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            string? head = fi.Extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ? ReadHead(full) : null;
            found[full] = new ConfigFileInfo(full, display, fi.Name, DetectFormat(full, head), known, fi.Length, fi.LastWriteTimeUtc);
        }
        catch { }
    }

    private static string? ReadHead(string path)
    {
        try
        {
            using var r = new StreamReader(path);
            var buf = new char[256];
            int n = r.Read(buf, 0, buf.Length);
            return new string(buf, 0, n);
        }
        catch { return null; }
    }

    /// <summary>Expands %ENV% vars and '*' wildcards (one per path segment) into existing files.</summary>
    public static IEnumerable<string> ExpandPattern(string pattern, string baseDir)
    {
        var expanded = Environment.ExpandEnvironmentVariables(pattern);
        if (!Path.IsPathRooted(expanded))
        {
            if (string.IsNullOrEmpty(baseDir)) yield break;
            expanded = Path.Combine(baseDir, expanded);
        }
        if (!expanded.Contains('*'))
        {
            if (File.Exists(expanded)) yield return expanded;
            yield break;
        }

        var segments = expanded.Split('\\');
        var current = new List<string> { segments[0] + "\\" };
        for (int i = 1; i < segments.Length; i++)
        {
            bool last = i == segments.Length - 1;
            var seg = segments[i];
            var next = new List<string>();
            foreach (var dir in current)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    if (seg.Contains('*'))
                        next.AddRange(last ? Directory.EnumerateFiles(dir, seg) : Directory.EnumerateDirectories(dir, seg));
                    else
                    {
                        var p = Path.Combine(dir, seg);
                        if (last ? File.Exists(p) : Directory.Exists(p)) next.Add(p);
                    }
                }
                catch { }
            }
            current = next;
        }
        foreach (var f in current) yield return f;
    }

    private static IEnumerable<string> HeuristicScan(string dir, int depth, int maxDepth, int[] budget)
    {
        List<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(dir).ToList();
            dirs = depth < maxDepth ? Directory.EnumerateDirectories(dir).ToList() : new();
        }
        catch { yield break; }

        foreach (var f in files)
        {
            if (--budget[0] < 0) yield break;
            if (LooksLikeConfig(f)) yield return f;
        }
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
            // Unreal games keep their configs under <Game>\Saved\Config, and we still want those.
            foreach (var f in HeuristicScan(d, depth + 1, maxDepth, budget)) yield return f;
        }
    }

    private static bool LooksLikeConfig(string path)
    {
        var ext = Path.GetExtension(path);
        if (!ConfigExtensions.Contains(ext)) return false;
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("log") && !name.Contains("login") && !name.Contains("catalog")) return false;
        if (name.StartsWith("steam_appid") || name.Contains("license") || name.Contains("readme") || name.Contains("changelog")
            || name.Contains("credits") || name.Contains("thirdparty") || name.Contains("third_party") || name.Contains("eula_") ) return false;
        if (name.EndsWith(".runtimeconfig") || name.EndsWith(".deps") || name.Contains("unitycrashhandler")
            || name is "boot" or "scenarios" or "runtimeinitializeonloads" or "scriptingassemblies") return false;
        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !TxtHints.Any(h => name.Contains(h))) return false;
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && (name.Contains("manifest") || name.Contains("usercache") || name.Contains("version"))) return false;
        try { if (new FileInfo(path).Length > MaxSize) return false; } catch { return false; }
        return true;
    }
}
