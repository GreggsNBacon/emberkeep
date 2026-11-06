using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberkeep.Core;
using Emberkeep.Web;

namespace Emberkeep;

/// <summary>
/// Emberkeep for Linux: runs the server manager and its web interface until stopped (Ctrl+C, or systemd).
///   emberkeep          run (what the systemd service does)
///   emberkeep link     print the sign-in link of the running Emberkeep (run as the same user)
///   emberkeep version  print the version
/// </summary>
internal static class Program
{
    private static string LinkFile => Path.Combine(ServerManager.DataDir, "sign-in-link.txt");

    private static async Task<int> Main(string[] args)
    {
        var cmd = args.FirstOrDefault()?.TrimStart('-').ToLowerInvariant();
        if (cmd is "version" or "v") { Console.WriteLine(UpdateChecker.CurrentVersion); return 0; }
        if (cmd is "help" or "h" or "?") { Help(); return 0; }
        if (cmd == "link")
        {
            if (!File.Exists(LinkFile)) { Console.Error.WriteLine("Emberkeep isn't running as this user (no sign-in link in " + ServerManager.DataDir + ")."); return 1; }
            Console.WriteLine(File.ReadAllText(LinkFile).Trim());
            return 0;
        }
        if (cmd != null) { Console.Error.WriteLine($"Unknown command \"{args[0]}\"."); Help(); return 2; }

        ServerManager mgr;
        WebHost host;
        try
        {
            mgr = new ServerManager();
            host = new WebHost(mgr);
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Emberkeep couldn't start: " + ex.Message);
            return 1;
        }

        // The host's sign-in link changes every start. Keep it where only this user can read it, for "emberkeep link".
        try
        {
            using var fs = new FileStream(LinkFile, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            using var w = new StreamWriter(fs);
            w.WriteLine(host.LocalLoginUrl);
        }
        catch { }
#if DEBUG
        File.WriteAllText(Path.Combine(ServerManager.DataDir, "dev-token.txt"), $"{host.LocalUrl} {host.Auth.LocalToken}");
#endif
        Console.WriteLine($"Emberkeep {UpdateChecker.CurrentVersion} is running on {host.LocalUrl}{(host.ListeningOnLan ? " and your network" : " (this machine only)")}.");
        Console.WriteLine($"Data: {ServerManager.DataDir}");
        Console.WriteLine("Sign in as the host with the link from \"emberkeep link\". From another computer, forward the port over SSH first:");
        Console.WriteLine($"  ssh -L {host.Port}:127.0.0.1:{host.Port} you@this-server   then open the link on that computer.");

        _ = Task.Run(mgr.StartAutoServersAsync);

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }

        Console.WriteLine("Stopping servers…");
        try { await mgr.StopAllAsync(); } catch { }
        try { await host.StopAsync(); } catch { }
        try { File.Delete(LinkFile); } catch { }
        mgr.Dispose();
        return 0;
    }

    private static void Help() => Console.WriteLine("""
        Emberkeep: game server manager.
          emberkeep          run Emberkeep (the web interface listens on 127.0.0.1:8765)
          emberkeep link     print the host's sign-in link while Emberkeep is running (run as the same user)
          emberkeep version  print the version
        """);
}
