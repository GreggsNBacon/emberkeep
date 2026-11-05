using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hostbay.Core;

public sealed record PortState(string Protocol, int Port, bool Open, string? Error = null);

/// <summary>A server's router ports as the UI shows them.</summary>
public sealed record RouterPortsStatus(
    string Backend,          // porthop | builtin
    string BackendLabel,     // human-readable: "PortHop", "PortHop (starts when needed)", "Hostbay (built in)"
    string Spec,             // "UDP 2456-2458, TCP 8888"
    string Source,           // where the list came from
    List<PortState> Ports,
    string State,            // open | partial | closed | unknown | none | working
    string? ExternalIp,
    string? Message,
    DateTime CheckedAt);

/// <summary>
/// Opens and closes servers' ports on the router. Uses PortHop when it's installed (starting it in the background
/// if needed) so its list, tray and restore-after-reboot stay in charge; otherwise forwards with Hostbay's own UPnP.
/// </summary>
public sealed class RouterPortService
{
    private const string Tag = "Hostbay: ";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);
    private static string RegistryPath => Path.Combine(ServerManager.DataDir, "router-forwards.json");

    private readonly ServerManager _mgr;
    private readonly ConcurrentDictionary<string, RouterPortsStatus> _cache = new();
    private readonly SemaphoreSlim _routerLock = new(1, 1); // one router conversation at a time
    private readonly Dictionary<string, ServerState> _lastState = new();
    private UpnpGateway? _gw;
    private DateTime _gwAt;

    public RouterPortService(ServerManager mgr)
    {
        _mgr = mgr;
        lock (_lastState) foreach (var r in mgr.Runtimes) _lastState[r.Config.Id] = r.State; // don't act on servers already running
        mgr.Changed += OnServersChanged;
    }

    // ---------- status ----------

    public async Task<RouterPortsStatus> StatusAsync(ServerInstance s, bool refresh = false)
    {
        if (!refresh && _cache.TryGetValue(s.Id, out var hit) && DateTime.UtcNow - hit.CheckedAt < CacheFor) return hit;
        var (entries, source) = RouterPorts.For(s);
        var spec = RouterPorts.Format(entries);
        var wanted = RouterPorts.Expand(entries);
        var ph = await PortHopClient.InfoAsync();
        var (backend, label) = Backend(ph);
        if (wanted.Count == 0) return Remember(s, new(backend, label, spec, source, new(), "none", null, "No ports to open. Add them under Settings → Router ports.", DateTime.UtcNow));

        // PortHop answers from its cached router snapshot: instant, no router traffic.
        if (ph.Running && await PortHopClient.ForwardedAsync() is { } fw && fw.RouterFound)
        {
            var open = new HashSet<string>(fw.Ports.Where(p => p.Enabled).Select(p => $"{p.Protocol}/{p.Port}"));
            var ports = wanted.Select(w => new PortState(w.Protocol, w.Port, open.Contains(w.Key))).ToList();
            return Remember(s, Summarize(backend, label, spec, source, ports, fw.ExternalIp, null));
        }

        // Otherwise ask the router about each port.
        await _routerLock.WaitAsync();
        try
        {
            var gw = await GatewayAsync();
            if (gw == null) return Remember(s, new(backend, label, spec, source, wanted.Select(w => new PortState(w.Protocol, w.Port, false)).ToList(),
                "unknown", null, "No router answered over UPnP, so Hostbay can't check or open ports. UPnP may be off in your router's settings.", DateTime.UtcNow));
            var ports = new List<PortState>();
            foreach (var w in wanted)
            {
                var m = await Upnp.GetSpecificMappingAsync(gw, w.Port, w.Protocol);
                ports.Add(new PortState(w.Protocol, w.Port, m is { } e && e.InternalClient == gw.LocalAddress.ToString()));
            }
            string? ext = null;
            try { ext = await Upnp.GetExternalIpAsync(gw); } catch { }
            return Remember(s, Summarize(backend, label, spec, source, ports, ext, null));
        }
        catch (Exception ex) { return Remember(s, new(backend, label, spec, source, new(), "unknown", null, ex.Message, DateTime.UtcNow)); }
        finally { _routerLock.Release(); }
    }

    // ---------- open / close ----------

    public async Task<RouterPortsStatus> OpenAsync(ServerInstance s) => await ChangeAsync(s, open: true);
    public async Task<RouterPortsStatus> CloseAsync(ServerInstance s) => await ChangeAsync(s, open: false);

    private async Task<RouterPortsStatus> ChangeAsync(ServerInstance s, bool open)
    {
        var (entries, source) = RouterPorts.For(s);
        var spec = RouterPorts.Format(entries);
        var wanted = RouterPorts.Expand(entries);
        if (wanted.Count == 0) return await StatusAsync(s, refresh: true);
        string name = Tag + s.Name;
        var log = _mgr.Get(s.Id)?.Logs;

        // PortHop first: start it if it's installed. Closing goes through it too, so it forgets the ports
        // and won't re-open them the next time it starts.
        int? phPort = await PortHopClient.EnsureRunningAsync();
        if (phPort is int port)
        {
            try
            {
                var results = open ? await PortHopClient.ForwardAsync(port, name, entries) : await PortHopClient.RemoveAsync(port, name, entries);
                var leftovers = results.Where(r => r.Skipped).Select(r => new PortSpec(r.Protocol, r.Port)).ToList();
                // PortHop leaves ports it didn't open alone; ones Hostbay opened itself earlier are closed here.
                if (!open && leftovers.Count > 0) await BuiltinRemoveAsync(leftovers, onlyOurs: true);
                var ports = results.Select(r => new PortState(r.Protocol, r.Port, open ? r.Ok && !r.Skipped : !r.Ok && !r.Skipped, r.Error)).ToList();
                var info = await PortHopClient.InfoAsync();
                var (backend, label) = Backend(info);
                var st = Summarize(backend, label, spec, source, ports, (await PortHopClient.ForwardedAsync())?.ExternalIp, FirstError(ports));
                Log(log, open, st, "PortHop");
                return Remember(s, st);
            }
            catch (Exception ex)
            {
                var st = new RouterPortsStatus("porthop", "PortHop", spec, source, new(), "unknown", null, ex.Message, DateTime.UtcNow);
                log?.Add("error", $"Router ports: {ex.Message}");
                return Remember(s, st);
            }
        }

        // No PortHop: Hostbay's own UPnP.
        await _routerLock.WaitAsync();
        try
        {
            var gw = await GatewayAsync() ?? throw new UpnpException("No router answered over UPnP. UPnP may be off in your router's settings.");
            var ports = new List<PortState>();
            var registry = LoadRegistry();
            foreach (var w in wanted)
            {
                try
                {
                    if (open)
                    {
                        await Upnp.AddPortMappingAsync(gw, w.Port, w.Port, w.Protocol, name, 0);
                        registry.RemoveAll(r => r.Key == w.Key);
                        registry.Add(new RegistryEntry(w.Protocol, w.Port, name));
                        ports.Add(new PortState(w.Protocol, w.Port, true));
                    }
                    else
                    {
                        var m = await Upnp.GetSpecificMappingAsync(gw, w.Port, w.Protocol);
                        bool mine = m is { } e && e.InternalClient == gw.LocalAddress.ToString();
                        if (mine) await Upnp.DeletePortMappingAsync(gw, w.Port, w.Protocol);
                        registry.RemoveAll(r => r.Key == w.Key);
                        ports.Add(new PortState(w.Protocol, w.Port, false));
                    }
                }
                catch (UpnpException ex) when (ex.Code == 718)
                {
                    ports.Add(new PortState(w.Protocol, w.Port, false, "Already forwarded to another device"));
                }
                catch (Exception ex) { ports.Add(new PortState(w.Protocol, w.Port, !open, ex.Message)); }
            }
            SaveRegistry(registry);
            string? ext = null;
            try { ext = await Upnp.GetExternalIpAsync(gw); } catch { }
            var st = Summarize("builtin", "Hostbay (built in)", spec, source, ports, ext, FirstError(ports));
            Log(log, open, st, "Hostbay");
            return Remember(s, st);
        }
        catch (Exception ex)
        {
            log?.Add("error", $"Router ports: {ex.Message}");
            return Remember(s, new("builtin", "Hostbay (built in)", spec, source, new(), "unknown", null, ex.Message, DateTime.UtcNow));
        }
        finally { _routerLock.Release(); }
    }

    private async Task BuiltinRemoveAsync(List<PortSpec> ports, bool onlyOurs)
    {
        var registry = LoadRegistry();
        var ours = new HashSet<string>(registry.Select(r => r.Key));
        await _routerLock.WaitAsync();
        try
        {
            var gw = await GatewayAsync();
            if (gw == null) return;
            foreach (var p in ports)
            {
                if (onlyOurs && !ours.Contains(p.Key)) continue;
                try
                {
                    var m = await Upnp.GetSpecificMappingAsync(gw, p.Port, p.Protocol);
                    if (m is { } e && e.InternalClient == gw.LocalAddress.ToString()) await Upnp.DeletePortMappingAsync(gw, p.Port, p.Protocol);
                }
                catch { }
                registry.RemoveAll(r => r.Key == p.Key);
            }
            SaveRegistry(registry);
        }
        finally { _routerLock.Release(); }
    }

    // ---------- automatic open/close with the server ----------

    private void OnServersChanged()
    {
        var actions = new List<(ServerRuntime Rt, bool Open)>();
        lock (_lastState)
        {
            foreach (var rt in _mgr.Runtimes)
            {
                var id = rt.Config.Id;
                var now = rt.State;
                _lastState.TryGetValue(id, out var before);
                _lastState[id] = now;
                if (!rt.Config.ForwardWhileRunning || before == now) continue;
                if (now == ServerState.Running && before is ServerState.Stopped or ServerState.Starting) actions.Add((rt, true));
                else if (now == ServerState.Stopped && before is ServerState.Running or ServerState.Stopping) actions.Add((rt, false));
            }
        }
        foreach (var (rt, open) in actions)
            _ = Task.Run(async () =>
            {
                rt.Logs.Add("system", open ? "Opening router ports (Open ports while the server runs is on)…" : "Closing router ports…");
                await ChangeAsync(rt.Config, open);
            });
    }

    // ---------- helpers ----------

    private static (string Backend, string Label) Backend(PortHopInfo ph) =>
        ph.Running ? ("porthop", ph.Version is { } v ? $"PortHop {v}" : "PortHop")
        : ph.Installed ? ("porthop", "PortHop (starts when needed)")
        : ("builtin", "Hostbay (built in)");

    private static RouterPortsStatus Summarize(string backend, string label, string spec, string source, List<PortState> ports, string? ext, string? message)
    {
        int open = ports.Count(p => p.Open);
        string state = ports.Count == 0 ? "none" : open == ports.Count ? "open" : open == 0 ? "closed" : "partial";
        return new(backend, label, spec, source, ports, state, string.IsNullOrEmpty(ext) ? null : ext, message, DateTime.UtcNow);
    }

    private static string? FirstError(List<PortState> ports) =>
        ports.FirstOrDefault(p => p.Error != null) is { } e ? $"{e.Protocol} {e.Port}: {e.Error}" : null;

    private static void Log(LogBuffer? log, bool open, RouterPortsStatus st, string via)
    {
        if (log == null) return;
        var list = string.Join(", ", st.Ports.Select(p => $"{p.Protocol} {p.Port}"));
        if (st.Message != null) log.Add("error", $"Router ports via {via}: {st.Message}");
        else log.Add("system", open ? $"Opened router ports via {via}: {list}." : $"Closed router ports via {via}: {list}.");
    }

    private RouterPortsStatus Remember(ServerInstance s, RouterPortsStatus st) { _cache[s.Id] = st; return st; }

    private async Task<UpnpGateway?> GatewayAsync()
    {
        if (_gw != null && DateTime.UtcNow - _gwAt < TimeSpan.FromMinutes(5)) return _gw;
        _gw = await Upnp.DiscoverAsync(TimeSpan.FromSeconds(3));
        _gwAt = DateTime.UtcNow;
        return _gw;
    }

    private sealed record RegistryEntry(string Protocol, int Port, string Name)
    {
        public string Key => $"{Protocol}/{Port}";
    }

    private static List<RegistryEntry> LoadRegistry()
    {
        try { return JsonSerializer.Deserialize<List<RegistryEntry>>(File.ReadAllText(RegistryPath), ServerManager.Json) ?? new(); }
        catch { return new(); }
    }

    private static void SaveRegistry(List<RegistryEntry> list)
    {
        try { File.WriteAllText(RegistryPath, JsonSerializer.Serialize(list, ServerManager.Json)); } catch { }
    }
}
