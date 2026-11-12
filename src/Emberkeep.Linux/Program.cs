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
///   emberkeep link     print the sign-in link of the running Emberkeep
///   emberkeep open     open Emberkeep in an app window (or the browser), signed in as the host
///   emberkeep version  print the version
/// </summary>
internal static class Program
{
    private static string LinkFile => Path.Combine(ServerManager.DataDir, "sign-in-link.txt");
    /// <summary>Where the emberkeep service (install.sh or the .deb) keeps its link: readable by the emberkeep group.</summary>
    private const string ServiceLinkFile = "/var/lib/emberkeep/.config/Emberkeep/sign-in-link.txt";

    /// <summary>The running Emberkeep's sign-in link: this user's own, else the service's. denied: the service's exists but isn't readable.</summary>
    private static string? FindLink(out bool denied)
    {
        denied = false;
        foreach (var f in new[] { LinkFile, ServiceLinkFile }.Distinct())
        {
            try { if (File.Exists(f)) return File.ReadAllText(f).Trim(); }
            catch (UnauthorizedAccessException) { denied = true; }
            catch (IOException) { }
        }
        // /var/lib/emberkeep itself isn't readable: not in the emberkeep group (yet; it takes a new login).
        if (!denied && Directory.Exists("/var/lib/emberkeep"))
            try { Directory.EnumerateFileSystemEntries("/var/lib/emberkeep").Any(); } catch (UnauthorizedAccessException) { denied = true; }
        return null;
    }

    private static async Task<int> Main(string[] args)
    {
        var cmd = args.FirstOrDefault()?.TrimStart('-').ToLowerInvariant();
        if (cmd is "version" or "v") { Console.WriteLine(UpdateChecker.CurrentVersion); return 0; }
        if (cmd is "help" or "h" or "?") { Help(); return 0; }
        if (cmd == "link")
        {
            if (FindLink(out var denied) is { } link) { Console.WriteLine(link); return 0; }
            Console.Error.WriteLine(denied
                ? "Only members of the emberkeep group can read the sign-in link. Use: sudo emberkeep link   (or log out and back in if you were just added to the group)"
                : "Emberkeep isn't running (no sign-in link found). Start it with: sudo systemctl start emberkeep");
            return 1;
        }
        if (cmd == "open") return await OpenAsync();
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

        // The host's sign-in link changes every start. Keep it for "emberkeep link" and "emberkeep open": readable by this
        // user and its group only (the service's group, emberkeep, holds the people allowed to manage it).
        try
        {
            File.Delete(LinkFile); // so the permissions below apply even after a crash left an old one
            using var fs = new FileStream(LinkFile, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead });
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

    /// <summary>
    /// The desktop menu entry: opens Emberkeep signed in as the host. Reading the link needs the emberkeep group; right
    /// after installing, that only applies from the next login, so then it asks for the password once (pkexec) instead.
    /// </summary>
    private static async Task<int> OpenAsync()
    {
        var link = FindLink(out var denied);
        if (link == null && denied && Which("pkexec") is { } pkexec && Environment.ProcessPath is { } self)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(pkexec) { RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add(self); psi.ArgumentList.Add("link");
                using var p = System.Diagnostics.Process.Start(psi)!;
                var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0 && output.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)) link = output;
            }
            catch { }
        }
        // Without the link, the sign-in page (accounts still work, and it explains how to get the link).
        var url = link ?? "http://127.0.0.1:8765/";
        if (link == null) Console.Error.WriteLine(denied ? "Couldn't read the sign-in link: opening the sign-in page." : "Emberkeep doesn't seem to be running: sudo systemctl start emberkeep");
        // A Chromium-family browser gives Emberkeep its own window; anything else opens a tab in the default browser.
        var app = new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable", "brave-browser", "brave", "vivaldi" }
            .Select(Which).FirstOrDefault(p => p != null);
        try
        {
            var psi = app != null ? new System.Diagnostics.ProcessStartInfo(app) : new System.Diagnostics.ProcessStartInfo(Which("xdg-open") ?? "xdg-open");
            if (app != null) { psi.ArgumentList.Add("--app=" + url); psi.ArgumentList.Add("--window-size=1280,860"); psi.ArgumentList.Add("--class=Emberkeep"); }
            else psi.ArgumentList.Add(url);
            psi.UseShellExecute = false;
            System.Diagnostics.Process.Start(psi);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("Couldn't open a browser: " + ex.Message + ". Open " + url + " yourself."); return 1; }
    }

    private static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    private static void Help() => Console.WriteLine("""
        Emberkeep: game server manager.
          emberkeep          run Emberkeep (the web interface listens on 127.0.0.1:8765)
          emberkeep open     open Emberkeep in its own window, signed in as the host
          emberkeep link     print the host's sign-in link while Emberkeep is running
          emberkeep version  print the version
        """);
}
