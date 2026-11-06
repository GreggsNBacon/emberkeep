using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace Emberkeep.Core;

/// <summary>A newer Emberkeep found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Title, string Notes, string PageUrl, string? InstallerUrl, long InstallerSize);

/// <summary>
/// Looks for a newer Emberkeep on GitHub (the latest release of GreggsNBacon/emberkeep) when Emberkeep starts and every
/// 12 hours, unless turned off in App settings. One small request each time, with no account or personal details.
/// "Install update" downloads the release's installer and runs it; the installer closes and restarts Emberkeep.
/// </summary>
public sealed class UpdateChecker
{
    private const string Repo = "GreggsNBacon/emberkeep";
    private static readonly string LatestUrl = AppEnv.Get("UPDATE_URL") ?? $"https://api.github.com/repos/{Repo}/releases/latest";
    // Only ever download an installer published on the project's own GitHub releases (a test URL is trusted as given).
    private static readonly string TrustedDownloadPrefix = $"https://github.com/{Repo}/releases/download/";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly ServerManager _mgr;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _started;

    public UpdateChecker(ServerManager mgr)
    {
        _mgr = mgr;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Emberkeep", CurrentVersion.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>This copy's version (major.minor.build: the 4th part is only a build stamp).</summary>
    public static Version CurrentVersion { get; } = Trim(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public UpdateInfo? Available { get; private set; }
    public string? LatestVersion { get; private set; }
    public DateTime? CheckedAt { get; private set; }
    public string? Error { get; private set; }
    public bool Installing { get; private set; }

    /// <summary>Starts the background checks (once per run).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)); // let Emberkeep and its servers settle first
            while (true)
            {
                if (_mgr.Settings.CheckForUpdates) await CheckAsync();
                await Task.Delay(Interval);
            }
        });
    }

    public async Task CheckAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var res = await _http.GetAsync(LatestUrl);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)res.StatusCode}.");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new FormatException($"Unexpected release name \"{tag}\".");
            latest = Trim(latest);
            string? url = null; long size = 0;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    // Windows: the MSI. Linux: the linux-x64 tarball.
                    if (Platform.IsWindows ? !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                        : !name.EndsWith("-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase)) continue;
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    break;
                }
            LatestVersion = latest.ToString();
            Available = latest > CurrentVersion
                ? new UpdateInfo(latest.ToString(), root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                    root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repo}/releases/latest", url, size)
                : null;
            Error = null;
        }
        catch (Exception ex) { Error = "Couldn't check for updates: " + ex.Message; }
        finally { CheckedAt = DateTime.UtcNow; _gate.Release(); }
    }

    /// <summary>Downloads the new installer and starts it. The installer closes Emberkeep (stopping servers) and can restart it.</summary>
    public async Task InstallAsync()
    {
        var u = Available ?? throw new InvalidOperationException("No update is available.");
        if (u.InstallerUrl == null) throw new InvalidOperationException("This release has no installer. Download it from the release page instead.");
        if (AppEnv.Get("UPDATE_URL") == null && !u.InstallerUrl.StartsWith(TrustedDownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installer isn't on Emberkeep's GitHub releases, so it won't be downloaded.");
        if (Installing) throw new InvalidOperationException("The update is already downloading.");
        Installing = true;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Emberkeep-update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Platform.IsWindows ? $"Emberkeep-{u.Version}-Setup.msi" : $"Emberkeep-{u.Version}-linux-x64.tar.gz");
            var part = path + ".part";
            try
            {
                using (var res = await _http.GetAsync(u.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(part);
                    await src.CopyToAsync(dst);
                }
                if (u.InstallerSize > 0 && new FileInfo(part).Length != u.InstallerSize)
                    throw new IOException("The download was incomplete. Try again.");
                File.Move(part, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(part); } catch { } // don't leave a broken download behind
                throw;
            }
            if (Platform.IsWindows) Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\"") { UseShellExecute = true });
            else await ReplaceBinaryAsync(path);
        }
        finally { Installing = false; }
    }

    /// <summary>
    /// Linux: takes the new emberkeep program out of the release tarball and puts it in place of this one (a running
    /// program can be replaced on Linux). The caller then stops the servers and exits; systemd starts the new version.
    /// </summary>
    private static async Task ReplaceBinaryAsync(string tarball)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Emberkeep is installed.");
        var next = self + ".new";
        await using (var fs = File.OpenRead(tarball))
        await using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
        {
            var reader = new System.Formats.Tar.TarReader(gz);
            System.Formats.Tar.TarEntry? e;
            bool found = false;
            while ((e = await reader.GetNextEntryAsync()) != null)
            {
                if (e.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)) continue;
                if (!Path.GetFileName(e.Name.TrimEnd('/')).Equals("emberkeep", StringComparison.Ordinal)) continue;
                await e.ExtractToFileAsync(next, overwrite: true);
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException("The download doesn't contain the emberkeep program.");
        }
        File.SetUnixFileMode(next, File.GetUnixFileMode(self));
        try { File.Move(next, self, overwrite: true); }
        catch (UnauthorizedAccessException)
        {
            File.Delete(next);
            throw new InvalidOperationException($"Emberkeep can't replace {self}: the user it runs as needs to be able to write there. Install the update with install.sh instead.");
        }
    }

    /// <summary>Linux: whether Emberkeep runs under systemd, which starts it again after an update.</summary>
    public static bool UnderSystemd => !Platform.IsWindows && Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 };

    private static Version Trim(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
