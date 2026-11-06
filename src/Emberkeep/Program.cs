using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Emberkeep.Core;
using Emberkeep.Desktop;
using Emberkeep.Web;

namespace Emberkeep;

internal static class Program
{
    // One Emberkeep per data folder: the normal install shares one name; a copy pointed at another folder (EMBERKEEP_DATA_DIR, used
    // for testing) runs alongside it instead of just showing the installed one.
    private static readonly string InstanceSuffix = AppEnv.Get("DATA_DIR") is { Length: > 0 } d
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..12] : "";
    private static readonly string MutexName = "Emberkeep.SingleInstance.v1" + InstanceSuffix;
    public static readonly string ShowEventName = "Emberkeep.ShowWindow.v1" + InstanceSuffix;

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask the existing instance to show its window.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            return;
        }

        ApplicationConfiguration.Initialize();

        ServerManager mgr;
        WebHost host;
        try
        {
            mgr = new ServerManager();
            host = new WebHost(mgr);
            Task.Run(host.StartAsync).GetAwaiter().GetResult();
#if DEBUG
            // Lets scripts drive the API during development.
            System.IO.File.WriteAllText(System.IO.Path.Combine(ServerManager.DataDir, "dev-token.txt"), $"{host.LocalUrl} {host.Auth.LocalToken}");
#endif
        }
        catch (Exception ex)
        {
            MessageBox.Show("Emberkeep couldn't start:\n\n" + ex.Message, "Emberkeep", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool startHidden = Array.Exists(args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        using var form = new MainForm(mgr, host, startHidden);
        Application.Run(form);

        try { Task.Run(host.StopAsync).Wait(TimeSpan.FromSeconds(3)); } catch { }
        mgr.Dispose();
    }
}
