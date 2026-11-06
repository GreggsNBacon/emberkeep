using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Emberkeep.Core;

/// <summary>
/// How players connect: an IP plus <see cref="Port"/>, or (Core Keeper) a <see cref="Code"/>.
/// <see cref="Source"/> says where the value came from, so the UI can explain it.
/// </summary>
public sealed record JoinDetails(int? Port, string? Source, string? Code, string? CodeLabel);

/// <summary>A port found in a server's launch arguments or config: what it's for, its usual value and the value set.</summary>
public sealed record DetectedPort(string Role, string Protocol, int Default, int Port, string Source);

public static class JoinInfo
{
    public static JoinDetails Resolve(ServerInstance s)
    {
        if (s.JoinPort is int manual and > 0) return new(manual, "set in Settings", null, null);
        string dir = s.InstallDir, args = s.Arguments ?? "";
        string P(string rel) => Path.Combine(dir, rel);

        switch (s.GameId)
        {
            case "palworld":
                return Arg(args, @"-port=(\d+)") ?? Default(8211);
            case "enshrouded":
                return Json(P("enshrouded_server.json"), "queryPort", "enshrouded_server.json") ?? Default(15637);
            case "valheim":
                return Arg(args, @"-port\s+""?(\d+)") ?? Default(2456);
            case "satisfactory":
                return Arg(args, @"-Port=(\d+)") ?? Default(7777);
            case "minecraft-java":
                return Properties(P("server.properties"), "server-port") ?? Default(25565);
            case "minecraft-bedrock":
                return Properties(P("server.properties"), "server-port") ?? Default(19132);
            case "vrising":
                return Arg(args, @"-gamePort\s+(\d+)")
                    ?? Json(P("save-data\\Settings\\ServerHostSettings.json"), "Port", "ServerHostSettings.json")
                    ?? Json(P("VRisingServer_Data\\StreamingAssets\\Settings\\ServerHostSettings.json"), "Port", "ServerHostSettings.json")
                    ?? Default(9876);
            case "7dtd":
                return Xml(P("serverconfig.xml"), "ServerPort") ?? Default(26900);
            case "zomboid":
            {
                var ini = ConfigDiscovery.ExpandPattern("%USERPROFILE%\\Zomboid\\Server\\*.ini", dir).FirstOrDefault();
                return (ini != null ? Ini(ini, "DefaultPort", Path.GetFileName(ini)) : null) ?? Default(16261);
            }
            case "terraria":
                return Arg(args, @"-port\s+(\d+)") ?? Properties(P("serverconfig.txt"), "port") ?? Default(7777);
            case "ark-se":
            case "ark-sa":
                return Arg(args, @"[?\s-]Port=(\d+)") ?? Default(7777);
            case "sotf":
                return Json(Environment.ExpandEnvironmentVariables("%USERPROFILE%\\AppData\\LocalLow\\Endnight\\SonsOfTheForestDS\\dedicatedserver.cfg"), "GamePort", "dedicatedserver.cfg")
                    ?? Default(8766);
            case "conan":
                return Arg(args, @"-Port=(\d+)") ?? Ini(P("ConanSandbox\\Saved\\Config\\WindowsServer\\Engine.ini"), "Port", "Engine.ini") ?? Default(7777);
            case "corekeeper":
            {
                // Core Keeper players join with a Game ID, not an address.
                var id = File.Exists(P("GameID.txt")) ? File.ReadAllText(P("GameID.txt")).Trim() : null;
                return new(null, null, string.IsNullOrEmpty(id) ? null : id, "Game ID");
            }
            case "ace":
                return Json(ArgBlobs.LearnedFile(dir, "serverconfig.json"), "server_tcp_listener_port", "serverconfig.json") ?? Default(9700);
            case "rust":
                return Arg(args, @"\+server\.port\s+(\d+)") ?? Default(28015);
            default:
                // Custom servers: try common argument spellings before giving up.
                return Arg(args, @"(?:^|\s)[-+/]{0,2}(?:server\.)?port[=\s:]+(\d+)") ?? new(null, null, null, null);
        }
    }

    /// <summary>
    /// Ports the server listens on that players (or the server browser) need, read from launch arguments and
    /// config files wherever they're set. Only ports that are actually set somewhere are returned; admin ports
    /// such as RCON are never included, since they shouldn't be opened to the internet.
    /// </summary>
    public static List<DetectedPort> Ports(ServerInstance s)
    {
        string dir = s.InstallDir, args = s.Arguments ?? "";
        string P(string rel) => Path.Combine(dir, rel);
        var list = new List<DetectedPort>();
        void Add(string role, string proto, int def, params JoinDetails?[] readers)
        {
            var hit = readers.FirstOrDefault(r => r?.Port is > 0 and < 65536);
            if (hit != null) list.Add(new DetectedPort(role, proto, def, hit.Port!.Value, hit.Source ?? ""));
        }

        switch (s.GameId)
        {
            case "palworld":
                Add("Game", "UDP", 8211, Arg(args, @"-port=(\d+)"));
                break;
            case "enshrouded":
                Add("Game", "UDP", 15636, Json(P("enshrouded_server.json"), "gamePort", "enshrouded_server.json"));
                Add("Query", "UDP", 15637, Json(P("enshrouded_server.json"), "queryPort", "enshrouded_server.json"));
                break;
            case "valheim":
                Add("Game", "UDP", 2456, Arg(args, @"-port\s+""?(\d+)"));
                break;
            case "satisfactory":
                Add("Game", "TCP/UDP", 7777, Arg(args, @"-Port=(\d+)"));
                Add("Reliable", "TCP", 8888, Arg(args, @"-ReliablePort=(\d+)"));
                break;
            case "minecraft-java":
            {
                var props = ConfigReaders.IniValues(P("server.properties"));
                Add("Game", "TCP", 25565, Properties(P("server.properties"), "server-port"));
                // The query port only matters (and is only open) when querying is switched on.
                if (props.GetValueOrDefault("enable-query")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                    Add("Query", "UDP", 25565, Properties(P("server.properties"), "query.port") ?? Properties(P("server.properties"), "server-port") ?? new(25565, "server.properties", null, null));
                break;
            }
            case "minecraft-bedrock":
                Add("Game", "UDP", 19132, Properties(P("server.properties"), "server-port"));
                Add("Game (IPv6)", "UDP", 19133, Properties(P("server.properties"), "server-portv6"));
                break;
            case "vrising":
            {
                string a = P("save-data\\Settings\\ServerHostSettings.json"), b = P("VRisingServer_Data\\StreamingAssets\\Settings\\ServerHostSettings.json");
                Add("Game", "UDP", 9876, Arg(args, @"-gamePort\s+(\d+)"), Json(a, "Port", "ServerHostSettings.json"), Json(b, "Port", "ServerHostSettings.json"));
                Add("Query", "UDP", 9877, Arg(args, @"-queryPort\s+(\d+)"), Json(a, "QueryPort", "ServerHostSettings.json"), Json(b, "QueryPort", "ServerHostSettings.json"));
                break;
            }
            case "7dtd":
                Add("Game", "TCP/UDP", 26900, Xml(P("serverconfig.xml"), "ServerPort"));
                break;
            case "zomboid":
            {
                var ini = ConfigDiscovery.ExpandPattern("%USERPROFILE%\\Zomboid\\Server\\*.ini", dir).FirstOrDefault();
                if (ini == null) break;
                Add("Game", "UDP", 16261, Ini(ini, "DefaultPort", Path.GetFileName(ini)));
                Add("Direct", "UDP", 16262, Ini(ini, "UDPPort", Path.GetFileName(ini)));
                break;
            }
            case "terraria":
                Add("Game", "TCP", 7777, Arg(args, @"-port\s+(\d+)"), Properties(P("serverconfig.txt"), "port"));
                break;
            case "ark-se":
            case "ark-sa":
                Add("Game", "UDP", 7777, Arg(args, @"[?\s-]Port=(\d+)"));
                Add("Query", "UDP", 27015, Arg(args, @"[?\s-]QueryPort=(\d+)"));
                break;
            case "sotf":
            {
                var cfg = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\AppData\\LocalLow\\Endnight\\SonsOfTheForestDS\\dedicatedserver.cfg");
                Add("Game", "UDP", 8766, Json(cfg, "GamePort", "dedicatedserver.cfg"));
                Add("Query", "UDP", 27016, Json(cfg, "QueryPort", "dedicatedserver.cfg"));
                Add("Blob sync", "UDP", 9700, Json(cfg, "BlobSyncPort", "dedicatedserver.cfg"));
                break;
            }
            case "conan":
            {
                var engine = P("ConanSandbox\\Saved\\Config\\WindowsServer\\Engine.ini");
                Add("Game", "UDP", 7777, Arg(args, @"-Port=(\d+)"), Ini(engine, "Port", "Engine.ini"));
                Add("Query", "UDP", 27015, Arg(args, @"-QueryPort=(\d+)"), Ini(engine, "GameServerQueryPort", "Engine.ini"));
                break;
            }
            case "ace":
                Add("Game", "TCP", 9700, Json(ArgBlobs.LearnedFile(dir, "serverconfig.json"), "server_tcp_listener_port", "serverconfig.json"));
                Add("Game", "UDP", 9700, Json(ArgBlobs.LearnedFile(dir, "serverconfig.json"), "server_udp_listener_port", "serverconfig.json"));
                break;
            case "rust":
                Add("Game", "UDP", 28015, Arg(args, @"\+server\.port\s+(\d+)"));
                Add("Query", "UDP", 28017, Arg(args, @"\+server\.queryport\s+(\d+)"));
                Add("Rust+ app", "TCP", 28082, Arg(args, @"\+app\.port\s+(\d+)"));
                break;
        }
        return list;
    }

    private static JoinDetails Default(int port) => new(port, "game default", null, null);

    private static JoinDetails? Arg(string args, string pattern)
    {
        var m = Regex.Match(args, pattern, RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? new(p, "launch arguments", null, null) : null;
    }

    private static JoinDetails? Properties(string path, string key)
    {
        var v = ConfigReaders.IniValues(path).GetValueOrDefault(key);
        return int.TryParse(v, out var p) ? new(p, Path.GetFileName(path), null, null) : null;
    }

    private static JoinDetails? Ini(string path, string key, string label)
    {
        var v = ConfigReaders.IniValues(path).GetValueOrDefault(key);
        return int.TryParse(v, out var p) ? new(p, label, null, null) : null;
    }

    private static JoinDetails? Xml(string path, string key)
    {
        var v = ConfigReaders.XmlProperties(path)?.GetValueOrDefault(key);
        return int.TryParse(v, out var p) ? new(p, Path.GetFileName(path), null, null) : null;
    }

    private static JoinDetails? Json(string path, string key, string label)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var n)) return new(n, label, null, null);
                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out var sn)) return new(sn, label, null, null);
            }
        }
        catch { }
        return null;
    }
}
