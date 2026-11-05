using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hostbay.Core;
using Hostbay.Desktop;
using Hostbay.Web;

namespace Hostbay;

internal static class Program
{
    private const string MutexName = "Hostbay.SingleInstance.v1";
    public const string ShowEventName = "Hostbay.ShowWindow.v1";

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
            MessageBox.Show("Hostbay couldn't start:\n\n" + ex.Message, "Hostbay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool startHidden = Array.Exists(args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        using var form = new MainForm(mgr, host, startHidden);
        Application.Run(form);

        try { Task.Run(host.StopAsync).Wait(TimeSpan.FromSeconds(3)); } catch { }
        mgr.Dispose();
    }
}
