using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hostbay.Core;

/// <summary>
/// How players connect: an IP plus <see cref="Port"/>, or (Core Keeper) a <see cref="Code"/>.
/// <see cref="Source"/> says where the value came from, so the UI can explain it.
/// </summary>
public sealed record JoinDetails(int? Port, string? Source, string? Code, string? CodeLabel);

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
            case "rust":
                return Arg(args, @"\+server\.port\s+(\d+)") ?? Default(28015);
            default:
                // Custom servers: try common argument spellings before giving up.
                return Arg(args, @"(?:^|\s)[-+/]{0,2}(?:server\.)?port[=\s:]+(\d+)") ?? new(null, null, null, null);
        }
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
