using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emberkeep.Core;

/// <summary>One port to open on the router.</summary>
public sealed record PortSpec(string Protocol, int Port)
{
    public string Key => $"{Protocol}/{Port}";
}

/// <summary>An entry as written by people (and by Watchfire): protocol plus a single port or a range.</summary>
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
        ["conan"] = 7777, ["rust"] = 28015, ["ace"] = 9700,
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
        // Start from the game's usual list, then apply every port set in the launch arguments or config files.
        var entries = Parse(game.ForwardPorts);
        var detected = JoinInfo.Ports(s);
        // A join port typed in Settings counts too: it moves the game's main port.
        var join = JoinInfo.Resolve(s);
        if (join.Source == "set in Settings" && join.Port is int jp2 && DefaultJoinPort.TryGetValue(s.GameId, out int jdef))
        {
            detected.RemoveAll(d => d.Default == jdef);
            foreach (var e in entries.Where(e => e.From <= jdef && jdef <= e.To).ToList())
                detected.Add(new DetectedPort("Join", e.Protocol, jdef, jp2, "Settings"));
        }

        var moved = new HashSet<int>(); // indexes of entries already shifted
        var sources = new List<string>();
        // TCP/UDP ports are handled one protocol at a time, so lists like "TCP 26900, UDP 26900-26903" move together.
        foreach (var d in detected.SelectMany(d => d.Protocol == "TCP/UDP" ? new[] { d with { Protocol = "TCP" }, d with { Protocol = "UDP" } } : new[] { d }))
        {
            if (d.Port != d.Default) sources.Add(d.Source);
            // Already opened by an entry placed for another setting (e.g. Enshrouded's query port right after its game port).
            if (moved.Any(k => entries[k].From <= d.Port && d.Port <= entries[k].To && ProtoCovers(entries[k].Protocol, d.Protocol))) continue;
            int delta = d.Port - d.Default, i = -1;
            for (int k = 0; k < entries.Count; k++)
                if (!moved.Contains(k) && entries[k].From <= d.Default && d.Default <= entries[k].To && ProtoCovers(entries[k].Protocol, d.Protocol)) { i = k; break; }
            if (i >= 0 && entries[i].From + delta >= 1 && entries[i].To + delta <= 65535)
            {
                var e = entries[i];
                if (e.Protocol == "TCP/UDP")
                {
                    // Only this protocol moves; the other keeps its range until its own setting is applied.
                    entries.Add(e with { Protocol = d.Protocol == "TCP" ? "UDP" : "TCP" });
                    e = e with { Protocol = d.Protocol };
                }
                entries[i] = e with { From = e.From + delta, To = e.To + delta };
                moved.Add(i);
            }
            else { entries.Add(new PortEntry(d.Protocol, d.Port, d.Port)); moved.Add(entries.Count - 1); sources.Add(d.Source); } // not in the usual list, e.g. Minecraft's query port
        }
        var from = sources.Where(x => x.Length > 0).Distinct().ToList();
        return (MergeProtocols(entries), from.Count > 0 ? $"{game.Name}'s ports, as set in {string.Join(" and ", from)}" : $"{game.Name}'s usual ports");
    }

    /// <summary>"TCP 7780" and "UDP 7780" become "TCP/UDP 7780" again.</summary>
    private static List<PortEntry> MergeProtocols(List<PortEntry> entries)
    {
        var result = new List<PortEntry>(entries);
        for (int a = 0; a < result.Count; a++)
            for (int b = result.Count - 1; b > a; b--)
                if (result[a].From == result[b].From && result[a].To == result[b].To
                    && ((result[a].Protocol, result[b].Protocol) is ("TCP", "UDP") or ("UDP", "TCP")))
                {
                    result[a] = result[a] with { Protocol = "TCP/UDP" };
                    result.RemoveAt(b);
                }
        // Exact duplicates (e.g. a protocol split that didn't move) collapse too.
        return result.Distinct().ToList();
    }

    private static bool ProtoCovers(string entryProto, string proto) => entryProto == "TCP/UDP" || entryProto == proto;
}
