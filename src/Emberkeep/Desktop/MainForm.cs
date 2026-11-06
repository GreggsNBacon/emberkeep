using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Emberkeep.Core;
using Emberkeep.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Emberkeep.Desktop;

/// <summary>The native window: hosts the web UI in WebView2 and lives in the tray while servers run.</summary>
public sealed class MainForm : Form
{
    private readonly ServerManager _mgr;
    private readonly WebHost _host;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(11, 13, 18) };
    private readonly NotifyIcon _tray;
    private readonly EventWaitHandle _showSignal;
    private bool _reallyExit;
    private bool _startHidden;
    private bool _trayTipShown;

    public MainForm(ServerManager mgr, WebHost host, bool startHidden)
    {
        _mgr = mgr;
        _host = host;
        _startHidden = startHidden;

        Text = "Emberkeep";
        Icon = LoadIcon();
        BackColor = Color.FromArgb(11, 13, 18);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(LogicalToDeviceUnits(900), LogicalToDeviceUnits(600));
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Min(LogicalToDeviceUnits(1400), wa.Width - 80), Math.Min(LogicalToDeviceUnits(880), wa.Height - 80));
        Controls.Add(_web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Emberkeep", null, (_, _) => ShowFromTray());
        menu.Items.Add("Open in browser", null, (_, _) => OpenUrl(_host.LocalLoginUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray = new NotifyIcon { Icon = Icon, Text = "Emberkeep", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        // Notifications (App settings → Notifications) appear from the tray icon.
        host.Notifier.Windows += (title, text, bad) =>
        {
            try { BeginInvoke(() => _tray.ShowBalloonTip(6000, title, text, bad ? ToolTipIcon.Warning : ToolTipIcon.Info)); } catch { }
        };

        // A second launch of Emberkeep signals this event instead of starting another copy.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        var t = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                try { BeginInvoke(ShowFromTray); } catch { break; }
            }
        }) { IsBackground = true, Name = "Emberkeep show-signal" };
        t.Start();

        // Only a new port needs a new page: on the same port the page simply reconnects, so open dialogs stay put.
        _host.Restarted += portChanged => { if (portChanged) BeginInvoke(() => { if (_web.CoreWebView2 != null) _web.CoreWebView2.Navigate(_host.LocalLoginUrl); }); };
        _mgr.Changed += () => { try { BeginInvoke(UpdateTrayText); } catch { } };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; _ = InitWebViewAsync(); }
        base.SetVisibleCore(value);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UseDarkTitleBar();
        await InitWebViewAsync();
    }

    private bool _webInit;
    private async Task InitWebViewAsync()
    {
        if (_webInit) return;
        _webInit = true;
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emberkeep", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Emberkeep" : core.DocumentTitle;
            // Links that leave the app open in the real browser.
            core.NewWindowRequested += (_, a) => { a.Handled = true; OpenUrl(a.Uri); };
            core.NavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith(_host.LocalUrl, StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenUrl(a.Uri); return; }
                // The window on the host PC never needs a password: if it ever lands on the sign-in page (its session
                // ended, e.g. Emberkeep restarted underneath it), it signs itself in again.
                if (Uri.TryCreate(a.Uri, UriKind.Absolute, out var u) && u.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase))
                {
                    a.Cancel = true;
                    BeginInvoke(() => core.Navigate(_host.LocalLoginUrl));
                }
            };
            core.Navigate(_host.LocalLoginUrl);
        }
        catch (Exception ex)
        {
            var r = MessageBox.Show(
                "Emberkeep needs the Microsoft Edge WebView2 Runtime to draw its window.\n\n" + ex.Message +
                "\n\nOpen Emberkeep in your web browser instead?", "Emberkeep", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) OpenUrl(_host.LocalLoginUrl);
        }
    }

    private void UpdateTrayText()
    {
        int running = _mgr.Runtimes.Count(r => r.State != ServerState.Stopped);
        var text = running == 0 ? "Emberkeep: no servers running" : $"Emberkeep: {running} server{(running == 1 ? "" : "s")} running";
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowFromTray()
    {
        _startHidden = false;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool anyRunning = _mgr.Runtimes.Any(r => r.State != ServerState.Stopped);
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing && (anyRunning || _mgr.Settings.RemoteAccess) && _mgr.Settings.MinimizeToTray)
        {
            // Keep servers (and the remote UI) alive: hide to the tray instead of quitting.
            e.Cancel = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _tray.ShowBalloonTip(4000, "Emberkeep is still running",
                    anyRunning ? "Your servers keep running. Right-click the tray icon to exit." : "Remote access stays available. Right-click the tray icon to exit.",
                    ToolTipIcon.Info);
            }
            return;
        }
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            // Windows is shutting down, or an installer is updating Emberkeep (Restart Manager): no prompts,
            // just save-and-stop the servers within a short time limit so the update isn't held up.
            if (anyRunning)
            {
                foreach (var r in _mgr.Runtimes) r.Config.StopTimeoutSeconds = Math.Min(r.Config.StopTimeoutSeconds, 20);
                try { _mgr.StopAllAsync().Wait(TimeSpan.FromSeconds(25)); } catch { }
            }
            _tray.Visible = false;
            base.OnFormClosing(e);
            return;
        }
        if (!_reallyExit && anyRunning && !ConfirmStopAll()) { e.Cancel = true; return; }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void ExitApp()
    {
        if (_mgr.Runtimes.Any(r => r.State != ServerState.Stopped) && !ConfirmStopAll()) return;
        _reallyExit = true;
        _tray.Visible = false;
        Close();
        Application.Exit();
    }

    /// <summary>Asks before exiting with servers running, then stops them gracefully.</summary>
    private bool ConfirmStopAll()
    {
        var names = string.Join("\n", _mgr.Runtimes.Where(r => r.State != ServerState.Stopped).Select(r => "  • " + r.Config.Name));
        var r = MessageBox.Show(this,
            "These servers are still running:\n\n" + names + "\n\nStop them and exit Emberkeep?",
            "Exit Emberkeep", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return false;

        using var wait = new Form
        {
            Text = "Emberkeep", FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false, Size = new Size(380, 120),
            StartPosition = FormStartPosition.CenterParent, BackColor = Color.FromArgb(20, 24, 32), ForeColor = Color.White,
        };
        wait.Controls.Add(new Label { Text = "Stopping servers safely…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11) });
        var task = _mgr.StopAllAsync();
        task.ContinueWith(_ => { try { wait.BeginInvoke(wait.Close); } catch { } });
        if (!task.IsCompleted) wait.ShowDialog(this);
        _reallyExit = true;
        return true;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("emberkeep.ico");
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            int caption = 0x00120E0B; // COLORREF (BGR) for #0B0E12
            DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _web.Dispose(); _showSignal.Dispose(); }
        base.Dispose(disposing);
    }
}
