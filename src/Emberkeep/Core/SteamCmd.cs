using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emberkeep.Core;

/// <summary>
/// Valve's SteamCMD, which downloads and updates dedicated servers. Emberkeep sets it up by itself the first time it's needed:
/// it downloads Valve's zip into %LOCALAPPDATA%\Emberkeep\SteamCMD and only runs steamcmd.exe once Windows confirms it's
/// signed by Valve. Servers are downloaded with an anonymous login, which most dedicated servers allow.
/// </summary>
public static class SteamCmd
{
    private const string ZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const string TarUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
    /// <summary>The program in Emberkeep's own copy: steamcmd.exe on Windows, the steamcmd.sh launcher on Linux.</summary>
    private static string OwnExe => Platform.IsWindows ? "steamcmd.exe" : "steamcmd.sh";
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Where Emberkeep keeps its own copy.</summary>
    /// <summary>EMBERKEEP_STEAMCMD_DIR moves it (tests).</summary>
    public static string OwnDir => AppEnv.Get("STEAMCMD_DIR") is { Length: > 0 } d ? d : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emberkeep", "SteamCMD");

    /// <summary>The steamcmd.exe in use: EMBERKEEP_STEAMCMD, the path chosen in App settings, or Emberkeep's own copy.</summary>
    public static string ExePath(AppSettings s) =>
        AppEnv.Get("STEAMCMD") is { Length: > 0 } env ? env
        : !string.IsNullOrWhiteSpace(s.SteamCmdPath) ? s.SteamCmdPath
        : Path.Combine(OwnDir, OwnExe);

    public static bool IsInstalled(AppSettings s) => File.Exists(ExePath(s));

    /// <summary>Downloads and unpacks SteamCMD if it isn't there yet, and checks Valve's signature either way.</summary>
    public static async Task EnsureAsync(AppSettings s, Action<string> status, CancellationToken ct)
    {
        await SetupGate.WaitAsync(ct);
        try
        {
            var exe = ExePath(s);
            if (!File.Exists(exe))
            {
                if (!exe.Equals(Path.Combine(OwnDir, OwnExe), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SteamCMD isn't at {exe}. Fix the path in App settings, or clear it to let Emberkeep set SteamCMD up itself.");
                status("Downloading SteamCMD from Valve…");
                Directory.CreateDirectory(OwnDir);
                if (!Platform.IsWindows)
                {
                    // Linux: Valve's tarball (steamcmd.sh plus linux32/), over HTTPS from Valve's CDN. Execute bits come with it.
                    using var res = await Http.GetAsync(TarUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync(ct);
                    await using var gz = new GZipStream(src, CompressionMode.Decompress);
                    await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gz, OwnDir, overwriteFiles: true, ct);
                    Platform.MakeExecutable(exe);
                    Platform.MakeExecutable(Path.Combine(OwnDir, "linux32", "steamcmd"));
                    // Its first run downloads the rest of SteamCMD. Installing a game in that same run can fail with
                    // "Missing configuration", so let it finish setting itself up on its own first.
                    status("Setting up SteamCMD…");
                    await RunBareAsync(exe, ct);
                    return;
                }
                var zip = Path.Combine(OwnDir, "steamcmd.zip");
                using (var res = await Http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(zip);
                    await src.CopyToAsync(dst, ct);
                }
                ZipFile.ExtractToDirectory(zip, OwnDir, overwriteFiles: true);
                File.Delete(zip);
            }
            if (Platform.IsWindows && !SignedByValve(exe))
            {
                throw new InvalidOperationException($"{exe} isn't signed by Valve, so Emberkeep won't run it. Delete it and try again.");
            }
        }
        finally { SetupGate.Release(); }
    }

    /// <summary>Runs SteamCMD with just +quit (its self-update), up to twice, ignoring the result.</summary>
    private static async Task RunBareAsync(string exe, CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            var psi = new ProcessStartInfo(exe, "+quit") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
            using var p = Process.Start(psi);
            if (p == null) return;
            var o = p.StandardOutput.ReadToEndAsync(ct); var e = p.StandardError.ReadToEndAsync(ct);
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
                await p.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 7) return; // 7: it updated itself and wants to run again
        }
    }

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]");

    /// <summary>Windows trusts the file's signature, and the signer is Valve.</summary>
    public static bool SignedByValve(string path)
    {
        if (!Trusted(path)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // reading the signer of an Authenticode-signed exe
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Valve", StringComparison.OrdinalIgnoreCase)
                || cert.Subject.Contains("CN=Valve", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs SteamCMD with the given commands (after an anonymous login) and reports each output line. SteamCMD updates itself
    /// on its first run and then exits; that run is retried once.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(AppSettings s, string commands, Action<string> line, CancellationToken ct, string beforeLogin = "")
    {
        for (int attempt = 0; ; attempt++)
        {
            var psi = new ProcessStartInfo(ExePath(s), $"{beforeLogin} +login anonymous {commands} +quit".Trim())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath(s))!,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("SteamCMD didn't start.");
            var all = new StringBuilder();
            void Pump(StreamReader r) => _ = Task.Run(async () =>
            {
                // SteamCMD ends progress lines with \r or \n; split on either.
                var buf = new char[4096]; var cur = new StringBuilder();
                int n;
                while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i < n; i++)
                    {
                        char c = buf[i];
                        if (c == '\r' || c == '\n') { if (cur.Length > 0) { var t = Ansi.Replace(cur.ToString(), ""); cur.Clear(); if (t.Length == 0) continue; lock (all) all.AppendLine(t); line(t); } }
                        else cur.Append(c);
                    }
                if (cur.Length > 0) { var t = Ansi.Replace(cur.ToString(), ""); lock (all) all.AppendLine(t); line(t); }
            });
            Pump(p.StandardOutput); Pump(p.StandardError);
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
                await p.WaitForExitAsync(CancellationToken.None);
            await Task.Delay(200, CancellationToken.None); // let the readers finish
            ct.ThrowIfCancellationRequested();
            string output; lock (all) output = all.ToString();
            // 7: SteamCMD restarted after updating itself.
            if (p.ExitCode == 7 && attempt == 0) { line("SteamCMD updated itself. Running it again…"); continue; }
            // Right after SteamCMD set itself up (mostly on Linux), the first install can fail like this; the next works.
            if (attempt == 0 && output.Contains("Missing configuration", StringComparison.OrdinalIgnoreCase)) { line("SteamCMD wasn't ready yet. Trying again…"); continue; }
            return (p.ExitCode, output);
        }
    }

    /// <summary>
    /// Where SteamCMD writes its console log (with live progress lines). Windows: next to steamcmd.exe. Linux: it depends
    /// on the version and how it was installed, so the newest of the usual places.
    /// </summary>
    public static string ConsoleLogPath(AppSettings s)
    {
        var own = Path.Combine(Path.GetDirectoryName(ExePath(s))!, "logs", "console_log.txt");
        if (Platform.IsWindows) return own;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { own, Path.Combine(home, "Steam", "logs", "console_log.txt"), Path.Combine(home, ".steam", "steam", "logs", "console_log.txt"),
                Path.Combine(home, ".local", "share", "Steam", "logs", "console_log.txt") }
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? own;
    }

    private static readonly Regex Progress = new(@"Update state \(0x[0-9a-f]+\) (\w[\w ]*?), progress: ([\d.]+) \((\d+) / (\d+)\)", RegexOptions.IgnoreCase);

    /// <summary>Reads a SteamCMD progress line: what it's doing, percent, bytes done and total.</summary>
    public static (string Phase, double Percent, long Done, long Total)? ParseProgress(string line)
    {
        var m = Progress.Match(line);
        if (!m.Success) return null;
        return (m.Groups[1].Value.Trim(), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(m.Groups[3].Value), long.Parse(m.Groups[4].Value));
    }

    /// <summary>
    /// Progress from an appmanifest_*.acf while Steam downloads: bytes downloaded of the total, or, once downloading is done,
    /// bytes unpacked ("staged") of the total. Null when the manifest has no download in progress.
    /// </summary>
    public static (long Done, long Total, bool Staging)? ParseManifestProgress(string acf)
    {
        long Field(string name)
        {
            var m = Regex.Match(acf, "\"" + name + @"""\s+""(\d+)""");
            return m.Success ? long.Parse(m.Groups[1].Value) : 0;
        }
        long toDownload = Field("BytesToDownload"), downloaded = Field("BytesDownloaded");
        long toStage = Field("BytesToStage"), staged = Field("BytesStaged");
        if (toDownload > 0 && downloaded < toDownload) return (downloaded, toDownload, false);
        if (toStage > 0 && staged < toStage) return (staged, toStage, true);
        return null;
    }

    /// <summary>The app's name from Steam (via SteamCMD's app_info_print), or null when Steam doesn't know the app.</summary>
    public static async Task<string?> AppNameAsync(AppSettings s, int appId, CancellationToken ct)
    {
        var (_, output) = await RunAsync(s, $"+app_info_update 1 +app_info_print {appId}", _ => { }, ct);
        // The VDF prints "common" { "name" "Palworld Dedicated Server" ... }
        var i = output.IndexOf("\"common\"", StringComparison.Ordinal);
        if (i < 0) return null;
        var m = Regex.Match(output[i..], "\"name\"\\s+\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>A short reason from SteamCMD's output when it failed.</summary>
    public static string FailureReason(string output, int exitCode)
    {
        // Linux: SteamCMD is a 32-bit program; without 32-bit libraries it can't even start.
        if (!Platform.IsWindows && (exitCode == 127 || output.Contains("linux32/steamcmd: No such file", StringComparison.Ordinal) || output.Contains("libstdc++.so.6", StringComparison.Ordinal)))
            return "SteamCMD needs 32-bit libraries. Install them, for example: sudo apt install lib32gcc-s1 (Debian/Ubuntu) or sudo dnf install glibc.i686 libstdc++.i686 (Fedora).";
        var err = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Failed to install app", StringComparison.OrdinalIgnoreCase));
        if (err != null)
        {
            if (err.Contains("No subscription", StringComparison.OrdinalIgnoreCase))
                return "Steam won't let anonymous users download this one. Install it through Steam with an account that owns the game, then add its folder.";
            if (err.Contains("Disk", StringComparison.OrdinalIgnoreCase) && err.Contains("space", StringComparison.OrdinalIgnoreCase))
                return "There isn't enough free disk space for this server.";
            return err;
        }
        return $"SteamCMD stopped with exit code {exitCode}.";
    }

    // ---------- Authenticode ----------

    private static bool Trusted(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0,
                dwUnionChoice = 1 /* file */, pFile = pFile, dwStateAction = 0, dwProvFlags = 0x40, /* cache only url retrieval */
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
