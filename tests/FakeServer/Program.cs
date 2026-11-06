// A stand-in game server for testing Emberkeep: logs to stdout and a file, reads commands
// from stdin, saves on Ctrl+C, and answers Source RCON on the port given by --rcon=<port>.
using System.Net;
using System.Net.Sockets;
using System.Text;

var rconPort = args.Select(a => a.StartsWith("--rcon=") ? int.Parse(a[7..]) : 0).FirstOrDefault(p => p > 0);
var password = args.Select(a => a.StartsWith("--pw=") ? a[5..] : null).FirstOrDefault(p => p != null) ?? "secret";
Directory.CreateDirectory("logs");
var log = new StreamWriter(new FileStream("logs/fake.log", FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
var quit = new CancellationTokenSource();
void Out(string s) { Console.WriteLine(s); log.WriteLine("[file] " + s); }

Console.CancelKeyPress += (_, e) => { e.Cancel = true; Out("Ctrl+C received: saving world..."); Thread.Sleep(500); Out("World saved. Bye."); quit.Cancel(); };
Out($"FakeServer starting (pid {Environment.ProcessId}) args: {string.Join(' ', args)}");
Out("WARNING: this is a test warning");

if (rconPort > 0)
{
    var listener = new TcpListener(IPAddress.Loopback, rconPort);
    listener.Start();
    Out($"RCON listening on {rconPort}");
    _ = Task.Run(async () =>
    {
        while (!quit.IsCancellationRequested)
        {
            var c = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using var _c = c; var s = c.GetStream();
                bool authed = false;
                while (true)
                {
                    var hdr = new byte[4]; if (await s.ReadAsync(hdr) < 4) return;
                    var data = new byte[BitConverter.ToInt32(hdr)]; int r = 0; while (r < data.Length) r += await s.ReadAsync(data.AsMemory(r));
                    int id = BitConverter.ToInt32(data, 0), type = BitConverter.ToInt32(data, 4);
                    var body = Encoding.UTF8.GetString(data, 8, data.Length - 10);
                    async Task Send(int pid, int ptype, string b)
                    {
                        var bb = Encoding.UTF8.GetBytes(b); var p = new byte[14 + bb.Length];
                        BitConverter.GetBytes(10 + bb.Length).CopyTo(p, 0); BitConverter.GetBytes(pid).CopyTo(p, 4); BitConverter.GetBytes(ptype).CopyTo(p, 8); bb.CopyTo(p, 12);
                        await s.WriteAsync(p);
                    }
                    if (type == 3) { authed = body == password; await Send(authed ? id : -1, 2, ""); continue; }
                    if (!authed) return;
                    Out($"RCON command: {body}");
                    if (body == "DoExit") { await Send(id, 0, "Shutting down"); quit.Cancel(); return; }
                    await Send(id, 0, body == "ShowPlayers" ? "name,playeruid,steamid\nAlice,1,765\nBob,2,766" : $"ok: {body}");
                }
            });
        }
    });
}

_ = Task.Run(() =>
{
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        Out($"Command received: {line}");
        if (line == "stop") { Out("Stopping via command"); quit.Cancel(); break; }
        if (line == "crash") Environment.Exit(3);
    }
});

int tick = 0;
while (!quit.IsCancellationRequested)
{
    try { await Task.Delay(1000, quit.Token); } catch { break; }
    Out($"tick {++tick} players=2");
}
log.Flush();
return 0;
