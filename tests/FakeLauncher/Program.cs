// Starts FakeServer.exe (next to it) the way Assetto Corsa EVO's ServerLauncher starts its server:
// "-serverconfig <base64(len + zlib(json))> -seasondefinition <...>", through the shell, then stays open a while.
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;

static string Encode(string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(bytes, 0, bytes.Length);
    return Convert.ToBase64String([.. BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length)), .. ms.ToArray()]);
}
var dir = AppContext.BaseDirectory;
var config = "{\n  \"server_tcp_listener_port\": 9711,\n  \"server_udp_listener_port\": 9711,\n  \"server_http_port\": 8080,\n  \"server_name\": \"Launcher Test\",\n  \"max_players\": 16,\n  \"admin_password\": \"adminpw\"\n}";
var season = "{\n  \"track\": \"Brands Hatch\",\n  \"layout\": \"GP\",\n  \"weather_behaviour\": 1\n}";
Thread.Sleep(int.TryParse(Environment.GetEnvironmentVariable("FAKELAUNCHER_DELAY_MS"), out var d) ? d : 1500); // "the user sets things up"
Process.Start(new ProcessStartInfo(Path.Combine(dir, "FakeServer.exe"))
{
    Arguments = "-serverconfig " + Encode(config) + " -seasondefinition " + Encode(season) + " --rcon=0",
    WorkingDirectory = dir,
    UseShellExecute = true,
    WindowStyle = ProcessWindowStyle.Minimized,
});
Thread.Sleep(15000); // a launcher usually stays open after starting the server
