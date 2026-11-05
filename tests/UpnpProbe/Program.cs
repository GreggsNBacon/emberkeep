// Finds the router, reads its public IP and (with --map) adds then removes a test mapping
// for a port nothing listens on, so nothing is actually exposed.
using Hostbay.Core;

var gw = await Upnp.DiscoverAsync(TimeSpan.FromSeconds(3));
if (gw == null) { Console.WriteLine("No UPnP router answered."); return 1; }
Console.WriteLine($"Router: {gw.Name}\n  control: {gw.ControlUrl}\n  service: {gw.ServiceType}\n  this PC: {gw.LocalAddress}");
try { Console.WriteLine($"  external IP: {await Upnp.GetExternalIpAsync(gw)}"); } catch (Exception e) { Console.WriteLine($"  external IP failed: {e.Message}"); }
if (args.Contains("--map"))
{
    const int testPort = 47913;
    try
    {
        try { await Upnp.AddPortMappingAsync(gw, testPort, testPort, "TCP", "Hostbay test (removed)", 120); }
        catch (UpnpException e) when (e.Code == 725) { await Upnp.AddPortMappingAsync(gw, testPort, testPort, "TCP", "Hostbay test (removed)", 0); Console.WriteLine("  (router needs permanent leases)"); }
        Console.WriteLine($"  added mapping TCP {testPort} -> {gw.LocalAddress}:{testPort}");
    }
    catch (Exception e) { Console.WriteLine($"  add failed: {e.Message}"); }
    try { await Upnp.DeletePortMappingAsync(gw, testPort, "TCP"); Console.WriteLine("  deleted mapping"); }
    catch (Exception e) { Console.WriteLine($"  delete failed: {e.Message}"); }
    try { await Upnp.DeletePortMappingAsync(gw, testPort, "TCP"); Console.WriteLine("  second delete unexpectedly succeeded"); }
    catch (UpnpException e) { Console.WriteLine($"  confirmed gone ({e.Message})"); }
}
if (args.Contains("--forwarder"))
{
    // Runs the real PortForwarder loop on the unused test port, then closes it.
    var f = new PortForwarder();
    f.Changed += () => Console.WriteLine($"  forwarder: {f.Status.State}: {f.Status.Message} ext={f.Status.ExternalIp}");
    f.Open(47913);
    for (int i = 0; i < 40 && f.Status.State is "off" or "working"; i++) await Task.Delay(250);
    await f.CloseAsync();
    try { await Upnp.DeletePortMappingAsync(gw, 47913, "TCP"); Console.WriteLine("  mapping still there!"); }
    catch (UpnpException e) { Console.WriteLine($"  confirmed removed ({e.Code})"); }
}
return 0;
