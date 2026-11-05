using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hostbay.Core;

/// <summary>One port to open on the router.</summary>
public sealed record PortSpec(string Protocol, int Port)
{
    public string Key => $"{Protocol}/{Port}";
}

/// <summary>An entry as written by people (and by PortHop): protocol plus a single port or a range.</summary>
public sealed record PortEntry(string Protocol, int From, int To)
{
    public string Range => From == To ? From.ToString() : $"{From}-{To}";
    public override string ToString() => $"{Protocol} {Range}";
}

/// <summary>Parses, shifts and expands port lists like "UDP 2456-2458, TCP 8888".</summary>
public static class RouterPorts
{
    public const int MaxPorts = 100;

    // The join port each game's port list is written for. Kept in step with JoinInfo's defaults.
    private static readonly Dictionary<string, int> DefaultJoinPort = new()
    {
        ["palworld"] = 8211, ["enshrouded"] = 15637, ["valheim"] = 2456, ["satisfactory"] = 7777,
        ["minecraft-java"] = 25565, ["minecraft-bedrock"] = 19132, ["vrising"] = 9876, ["7dtd"] = 26900,
        ["zomboid"] = 16261, ["terraria"] = 7777, ["ark-se"] = 7777, ["ark-sa"] = 7777, ["sotf"] = 8766,
        ["conan"] = 7777, ["rust"] = 28015,
    };

    /// <summary>Parses "UDP 2456-2458, TCP 8888". Accepts TCP, UDP, TCP/UDP or BOTH. Throws FormatException with a readable message.</summary>
    public static List<PortEntry> Parse(string? text)
    {
        var list = new List<PortEntry>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            var m = Regex.Match(item, @"^(TCP/UDP|UDP/TCP|BOTH|TCP|UDP)\s+(\d{1,5})(?:\s*-\s*(\d{1,5}))?$", RegexOptions.IgnoreCase);
            if (!m.Success) throw new FormatException($"Couldn't read \"{item}\". Write it like \"UDP 2456-2458\" or \"TCP 25565\".");
            int from = int.Parse(m.Groups[2].Value), to = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : from;
            if (from < 1 || to > 65535 || to < from) throw new FormatException($"\"{item}\" isn't a valid port range (1-65535).");
            var proto = m.Groups[1].Value.ToUpperInvariant() switch { "UDP/TCP" or "BOTH" => "TCP/UDP", var p => p };
            list.Add(new PortEntry(proto, from, to));
        }
        if (Expand(list).Count > MaxPorts) throw new FormatException($"That's more than {MaxPorts} ports. Use smaller ranges.");
        return list;
    }

    public static string Format(IEnumerable<PortEntry> entries) => string.Join(", ", entries.Select(e => e.ToString()));

    /// <summary>Every single port, TCP/UDP entries split in two, duplicates removed.</summary>
    public static List<PortSpec> Expand(IEnumerable<PortEntry> entries)
    {
        var seen = new HashSet<string>();
        var list = new List<PortSpec>();
        foreach (var e in entries)
            foreach (var proto in e.Protocol == "TCP/UDP" ? new[] { "TCP", "UDP" } : new[] { e.Protocol })
                for (int p = e.From; p <= e.To; p++)
                {
                    var spec = new PortSpec(proto, p);
                    if (seen.Add(spec.Key)) list.Add(spec);
                }
        return list;
    }

    /// <summary>
    /// The ports to open for a server and where that list came from. The server's own list wins; otherwise the
    /// game's list, with entries containing the default join port moved to the server's actual join port.
    /// </summary>
    public static (List<PortEntry> Entries, string Source) For(ServerInstance s)
    {
        if (!string.IsNullOrWhiteSpace(s.ForwardPorts))
        {
            try { return (Parse(s.ForwardPorts), "set in Settings"); }
            catch (FormatException) { return (new(), "set in Settings (unreadable)"); }
        }
        var game = GameCatalog.Get(s.GameId);
        if (string.IsNullOrWhiteSpace(game.ForwardPorts))
        {
            // Custom servers: open the join port on both protocols if we know it.
            var j = JoinInfo.Resolve(s);
            return j.Port is int jp ? (new() { new PortEntry("TCP/UDP", jp, jp) }, "the join port") : (new(), "none");
        }
        var entries = Parse(game.ForwardPorts);
        int? actual = JoinInfo.Resolve(s).Port;
        if (actual is int a && DefaultJoinPort.TryGetValue(s.GameId, out int def) && a != def)
        {
            int delta = a - def;
            entries = entries.Select(e => e.From <= def && def <= e.To && e.From + delta >= 1 && e.To + delta <= 65535
                ? e with { From = e.From + delta, To = e.To + delta } : e).ToList();
            return (entries, $"{game.Name}'s ports, moved to join port {a}");
        }
        return (entries, $"{game.Name}'s usual ports");
    }
}
