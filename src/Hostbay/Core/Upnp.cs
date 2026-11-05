using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Hostbay.Core;

/// <summary>A router found over UPnP that can forward ports (an "Internet Gateway Device").</summary>
public sealed record UpnpGateway(Uri ControlUrl, string ServiceType, IPAddress LocalAddress, string Name);

public sealed class UpnpException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>Minimal UPnP IGD client: SSDP discovery plus the AddPortMapping / DeletePortMapping / GetExternalIPAddress SOAP calls.</summary>
public static class Upnp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    /// <summary>Asks the local network for a UPnP router. Returns null if none answers within the timeout.</summary>
    public static async Task<UpnpGateway?> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // Test hook: talk to a fake router's description URL (may be on loopback) instead of searching the network.
        if (Environment.GetEnvironmentVariable("HOSTBAY_TEST_GATEWAY") is { Length: > 0 } testUrl)
            return await ReadDescriptionAsync(new Uri(testUrl), ct);

        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Search from each home-network address so the request leaves on the right adapter.
        var sources = NetworkInfo.LocalAddresses().Where(a => a.Kind == "lan").Select(a => IPAddress.Parse(a.Address)).ToList();
        if (sources.Count == 0) sources.Add(IPAddress.Any);

        var tasks = sources.Select(async src =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(src, 0));
                var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var st in SearchTargets)
                {
                    var msg = $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(msg);
                    await udp.SendAsync(bytes, bytes.Length, target);
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                while (!cts.IsCancellationRequested)
                {
                    var res = await udp.ReceiveAsync(cts.Token);
                    var text = Encoding.ASCII.GetString(res.Buffer);
                    var m = Regex.Match(text, @"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                    if (m.Success) lock (locations) locations.Add(m.Groups[1].Value.Trim());
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        });
        await Task.WhenAll(tasks);

        foreach (var loc in locations)
        {
            try
            {
                var gw = await ReadDescriptionAsync(new Uri(loc), ct);
                if (gw != null) return gw;
            }
            catch { /* not a usable gateway; try the next answer */ }
        }
        return null;
    }

    private static async Task<UpnpGateway?> ReadDescriptionAsync(Uri location, CancellationToken ct)
    {
        // Only talk to devices on the local network: the description must come from a private address.
        if (!IPAddress.TryParse(location.Host, out var host) || !IsPrivate(host)) return null;
        var xml = XDocument.Parse(await Http.GetStringAsync(location, ct));
        XNamespace ns = xml.Root!.Name.Namespace;
        var baseUrl = xml.Root.Element(ns + "URLBase")?.Value is { Length: > 0 } b ? new Uri(b) : location;
        var name = xml.Descendants(ns + "friendlyName").FirstOrDefault()?.Value ?? "Router";
        var service = xml.Descendants(ns + "service")
            .Select(s => (Type: s.Element(ns + "serviceType")?.Value ?? "", Control: s.Element(ns + "controlURL")?.Value ?? ""))
            .Where(s => s.Type.Contains("WANIPConnection") || s.Type.Contains("WANPPPConnection"))
            .OrderByDescending(s => s.Type.Contains("WANIPConnection"))
            .FirstOrDefault();
        if (string.IsNullOrEmpty(service.Control)) return null;
        var control = new Uri(baseUrl, service.Control);
        if (!IPAddress.TryParse(control.Host, out var ctlHost) || !IsPrivate(ctlHost)) return null;
        return new UpnpGateway(control, service.Type, LocalAddressToward(ctlHost), name);
    }

    /// <summary>The local IP this PC uses to reach the router: the address the router must forward to.</summary>
    private static IPAddress LocalAddressToward(IPAddress router)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(router, 9); // no packet is sent for UDP connect
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }

    private static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) && Environment.GetEnvironmentVariable("HOSTBAY_TEST_GATEWAY") is { Length: > 0 }) return true;
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork &&
               (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }

    public static Task AddPortMappingAsync(UpnpGateway gw, int externalPort, int internalPort, string protocol, string description, int leaseSeconds, CancellationToken ct = default) =>
        SoapAsync(gw, "AddPortMapping", new()
        {
            ["NewRemoteHost"] = "",
            ["NewExternalPort"] = externalPort.ToString(),
            ["NewProtocol"] = protocol,
            ["NewInternalPort"] = internalPort.ToString(),
            ["NewInternalClient"] = gw.LocalAddress.ToString(),
            ["NewEnabled"] = "1",
            ["NewPortMappingDescription"] = description,
            ["NewLeaseDuration"] = leaseSeconds.ToString(),
        }, ct);

    public static Task DeletePortMappingAsync(UpnpGateway gw, int externalPort, string protocol, CancellationToken ct = default) =>
        SoapAsync(gw, "DeletePortMapping", new()
        {
            ["NewRemoteHost"] = "",
            ["NewExternalPort"] = externalPort.ToString(),
            ["NewProtocol"] = protocol,
        }, ct);

    /// <summary>The router's entry for one port: (target IP, description), or null if it has none (error 714).</summary>
    public static async Task<(string InternalClient, string Description)?> GetSpecificMappingAsync(UpnpGateway gw, int externalPort, string protocol, CancellationToken ct = default)
    {
        try
        {
            var xml = await SoapAsync(gw, "GetSpecificPortMappingEntry", new()
            {
                ["NewRemoteHost"] = "",
                ["NewExternalPort"] = externalPort.ToString(),
                ["NewProtocol"] = protocol,
            }, ct);
            string V(string name) => xml.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
            return (V("NewInternalClient"), V("NewPortMappingDescription"));
        }
        catch (UpnpException ex) when (ex.Code is 714 or 713) { return null; }
    }

    public static async Task<string?> GetExternalIpAsync(UpnpGateway gw, CancellationToken ct = default)
    {
        var xml = await SoapAsync(gw, "GetExternalIPAddress", new(), ct);
        var ip = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
        return IPAddress.TryParse(ip, out _) ? ip : null;
    }

    private static async Task<XDocument> SoapAsync(UpnpGateway gw, string action, Dictionary<string, string> args, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append($"<u:{action} xmlns:u=\"{gw.ServiceType}\">");
        foreach (var (k, v) in args) sb.Append($"<{k}>{SecurityElement.Escape(v)}</{k}>");
        sb.Append($"</u:{action}></s:Body></s:Envelope>");

        using var req = new HttpRequestMessage(HttpMethod.Post, gw.ControlUrl)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml"),
        };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{gw.ServiceType}#{action}\"");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch { throw new UpnpException($"The router gave an unreadable answer to {action} (HTTP {(int)resp.StatusCode})."); }
        if (!resp.IsSuccessStatusCode)
        {
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var desc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            throw new UpnpException($"The router refused {action}: {desc ?? "error"} ({code ?? ((int)resp.StatusCode).ToString()}).",
                int.TryParse(code, out var c) ? c : null);
        }
        return doc;
    }
}
