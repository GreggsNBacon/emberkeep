using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hostbay.Core;

/// <summary>What the UI shows about Hostbay's own port forwarding.</summary>
public sealed record ForwardStatus(string State, string Message, int? Port, string? ExternalIp, string? Router, string? LocalIp);

/// <summary>
/// Keeps Hostbay's own web port forwarded on the router via UPnP while internet access is on.
/// The mapping is leased for an hour and renewed every 20 minutes, and removed when switched off or on exit,
/// so a crash leaves the port open for at most an hour.
/// </summary>
public sealed class PortForwarder : IDisposable
{
    private const string Description = "Hostbay remote access";
    private const int LeaseSeconds = 3600;
    private static readonly TimeSpan Renew = TimeSpan.FromMinutes(20);

    private readonly object _lock = new();
    private CancellationTokenSource? _loop;
    private UpnpGateway? _gateway;
    private int? _mappedPort;
    private bool _permanentLease;

    public ForwardStatus Status { get; private set; } = new("off", "Off.", null, null, null, null);

    public event Action? Changed;

    private void Set(ForwardStatus s) { Status = s; Changed?.Invoke(); }

    /// <summary>Starts (or restarts) forwarding <paramref name="port"/>; runs in the background.</summary>
    public void Open(int port)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _loop?.Cancel();
            _loop = cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => LoopAsync(port, cts.Token));
    }

    private async Task LoopAsync(int port, CancellationToken ct)
    {
        // Moving to a different port: remove the old mapping first.
        if (_mappedPort is int old && old != port) await RemoveAsync();

        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = Renew;
            try
            {
                Set(new("working", "Asking your router to open the port…", port, null, null, null));
                _gateway ??= await Upnp.DiscoverAsync(TimeSpan.FromSeconds(3), ct)
                    ?? throw new UpnpException("No router answered. UPnP may be turned off in your router's settings.");
                try
                {
                    await Upnp.AddPortMappingAsync(_gateway, port, port, "TCP", Description, _permanentLease ? 0 : LeaseSeconds, ct);
                }
                catch (UpnpException ex) when (ex.Code == 725 && !_permanentLease)
                {
                    // Some routers only accept permanent mappings; we still remove it when switched off.
                    _permanentLease = true;
                    await Upnp.AddPortMappingAsync(_gateway, port, port, "TCP", Description, 0, ct);
                }
                _mappedPort = port;
                string? external = null;
                try { external = await Upnp.GetExternalIpAsync(_gateway, ct); } catch { }
                // A private "public" address means another router or the internet provider sits in front of this one (CGNAT).
                bool doubleNat = external != null && IsPrivate(external);
                Set(new(doubleNat ? "limited" : "open",
                    doubleNat
                        ? $"Your router opened port {port}, but it sits behind another router or your internet provider's network ({external}), so it may still be unreachable from the internet."
                        : $"Port {port} is open on {_gateway.Name}.",
                    port, external ?? await NetworkInfo.PublicIpAsync(), _gateway.Name, _gateway.LocalAddress.ToString()));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (ex is not UpnpException) _gateway = null; // network trouble: look for the router again next time
                var local = _gateway?.LocalAddress.ToString();
                Set(new("failed",
                    $"{ex.Message} You can forward TCP port {port} to {local ?? "this PC"} in your router's settings instead.",
                    port, null, _gateway?.Name, local));
                wait = TimeSpan.FromMinutes(2); // retry sooner after a failure
            }
            try { await Task.Delay(wait, ct); } catch { return; }
        }
    }

    /// <summary>Stops forwarding and removes the mapping from the router.</summary>
    public async Task CloseAsync()
    {
        lock (_lock) { _loop?.Cancel(); _loop = null; }
        await RemoveAsync();
        Set(new("off", "Off.", null, null, null, null));
    }

    private async Task RemoveAsync()
    {
        if (_gateway == null || _mappedPort is not int port) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Upnp.DeletePortMappingAsync(_gateway, port, "TCP", cts.Token);
        }
        catch { /* already gone or router unreachable; the lease expires on its own */ }
        _mappedPort = null;
    }

    private static bool IsPrivate(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    public void Dispose()
    {
        try { CloseAsync().Wait(TimeSpan.FromSeconds(6)); } catch { }
    }
}
