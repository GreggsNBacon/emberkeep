using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>An address this PC can be reached at. Kind is "lan" (home network) or "vpn" (Tailscale, ZeroTier, Radmin…).</summary>
public sealed record LocalAddress(string Address, string Adapter, string Kind, bool Primary);

/// <summary>Works out which IP addresses friends can use to reach this PC.</summary>
public static class NetworkInfo
{
    private static readonly string[] VpnHints = ["tailscale", "zerotier", "radmin", "hamachi", "wireguard", "openvpn", "tap-windows", "nordlynx", "wintun"];
    private static readonly string[] VirtualHints = ["vethernet", "hyper-v", "virtualbox", "vmware", "docker", "wsl", "loopback", "bluetooth"];

    public static List<LocalAddress> LocalAddresses()
    {
        var result = new List<LocalAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var label = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            bool vpn = VpnHints.Any(label.Contains);
            if (!vpn && VirtualHints.Any(label.Contains)) continue; // VM/WSL switches aren't reachable by friends
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                var a = ua.Address;
                if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                var s = a.ToString();
                if (s.StartsWith("169.254.")) continue; // no DHCP lease
                // Tailscale's 100.64.0.0/10 range marks it as VPN even if the adapter name doesn't.
                bool cgnat = a.GetAddressBytes() is var b && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
                result.Add(new LocalAddress(s, nic.Name, vpn || cgnat ? "vpn" : "lan", !vpn && !cgnat && hasGateway));
            }
        }
        // The adapter with the internet gateway is the one friends on the same network reach.
        return result.OrderByDescending(a => a.Primary).ThenBy(a => a.Kind == "vpn").ThenBy(a => a.Adapter).ToList();
    }

    private static readonly SemaphoreSlim PublicLock = new(1, 1);
    private static (string? Ip, DateTime At) _publicCache;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Asks ipify.org for this network's public IP. Cached for 10 minutes; only called when the user asks for it.</summary>
    public static async Task<string?> PublicIpAsync()
    {
        await PublicLock.WaitAsync();
        try
        {
            if (_publicCache.Ip != null && DateTime.UtcNow - _publicCache.At < TimeSpan.FromMinutes(10)) return _publicCache.Ip;
            var text = (await Http.GetStringAsync("https://api.ipify.org")).Trim();
            if (!IPAddress.TryParse(text, out _)) return null;
            _publicCache = (text, DateTime.UtcNow);
            return text;
        }
        catch { return null; }
        finally { PublicLock.Release(); }
    }
}
