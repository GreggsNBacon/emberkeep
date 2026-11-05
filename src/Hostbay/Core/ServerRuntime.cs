using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hostbay.Core;

/// <summary>Live state of one server: its process, logs, and start/stop/command logic.</summary>
public sealed class ServerRuntime
{
    private readonly object _lock = new();
    private Process? _proc;
    private bool _adopted;
    private bool _stopRequested;
    private CancellationTokenSource? _tailCts;
    private readonly Queue<DateTime> _crashTimes = new();
    private readonly HashSet<string> _recentStdout = new();
    private readonly Queue<string> _recentStdoutOrder = new();

    private TimeSpan _lastCpu;
    private DateTime _lastSample;

    public ServerRuntime(ServerInstance config) { Config = config; }

    public ServerInstance Config { get; set; }
    public GameDefinition Game => GameCatalog.Get(Config.GameId);
    public LogBuffer Logs { get; } = new();
    public ServerState State { get; private set; } = ServerState.Stopped;
    public DateTime? StartedAt { get; private set; }
    public int? Pid => _proc is { } p && !_adopted ? SafePid(p) : _adoptedPid;
    private int? _adoptedPid;
    public int? LastExitCode { get; private set; }
    public bool Adopted => _adopted;
    public bool StdinAvailable => _proc != null && !_adopted && State is ServerState.Running or ServerState.Stopping;
    public double CpuPercent { get; private set; }
    public long MemoryBytes { get; private set; }
    public string? LastError { get; private set; }

    public event Action? Changed;

    private void SetState(ServerState s)
    {
        State = s;
        Changed?.Invoke();
    }

    // ---------- path helpers ----------

    public string ResolveExecutable()
    {
        var exe = Environment.ExpandEnvironmentVariables(Config.Executable.Trim().Trim('"'));
        if (string.IsNullOrEmpty(exe)) return "";
        if (Path.IsPathRooted(exe)) return exe;
        var local = Path.Combine(Config.InstallDir, exe);
        if (File.Exists(local) || exe.Contains('\\') || exe.Contains('/')) return Path.GetFullPath(local);
        return exe; // bare name such as "java": let Windows search PATH
    }

    public string ResolveWorkingDirectory()
    {
        var wd = Config.WorkingDirectory?.Trim();
        if (string.IsNullOrEmpty(wd)) return Config.InstallDir;
        wd = Environment.ExpandEnvironmentVariables(wd);
        return Path.IsPathRooted(wd) ? wd : Path.GetFullPath(Path.Combine(Config.InstallDir, wd));
    }

    public RemoteConsoleSettings? ResolveRemoteConsole()
    {
        if (Config.RconFromConfig && Game.ReadRemoteConsole != null && Directory.Exists(Config.InstallDir))
        {
            var fromConfig = Game.ReadRemoteConsole(Config.InstallDir);
            if (fromConfig != null)
            {
                fromConfig.Host = string.IsNullOrWhiteSpace(Config.Rcon.Host) ? "127.0.0.1" : Config.Rcon.Host;
                return fromConfig;
            }
        }
        return Config.Rcon.Port > 0 ? Config.Rcon : null;
    }

    // ---------- start ----------

    public Task StartAsync() => Task.Run(Start);

    private void Start()
    {
        lock (_lock)
        {
            if (State is ServerState.Running or ServerState.Stopping) return;
            LastError = null;
            var exe = ResolveExecutable();
            if (string.IsNullOrEmpty(exe)) { Fail("No executable is set. Choose one in the server's Settings tab."); return; }
            if (Path.IsPathRooted(exe) && !File.Exists(exe)) { Fail($"Executable not found: {exe}"); return; }
            var wd = ResolveWorkingDirectory();
            if (!Directory.Exists(wd)) { Fail($"Working directory not found: {wd}"); return; }

            // Settings kept in files and passed encoded on the command line (see ArgBlobs), e.g. Assetto Corsa EVO.
            string args;
            try { args = ArgBlobs.Expand(Config.Arguments ?? "", Config.InstallDir); }
            catch (InvalidOperationException ex) { Fail(ex.Message); return; }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = wd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var (k, v) in Config.Environment) psi.Environment[k] = v;

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnStdout("out", e.Data);
            p.ErrorDataReceived += (_, e) => OnStdout("err", e.Data);
            p.Exited += (_, _) => OnExited(p);

            Logs.Add("system", $"Starting {Config.Name}: \"{exe}\" {Config.Arguments}");
            try
            {
                bool ok;
                lock (Native.ConsoleLock)
                {
                    Native.AllowCtrlCInChildren();
                    ok = p.Start();
                }
                if (!ok) { Fail("The process failed to start."); return; }
            }
            catch (Exception ex)
            {
                Fail(exe.Equals("java", StringComparison.OrdinalIgnoreCase)
                    ? "Java was not found. Install Java (for example Adoptium Temurin 21) or set the full path to java.exe in Settings."
                    : $"Failed to start: {ex.Message}");
                p.Dispose();
                return;
            }
            _proc = p; _adopted = false; _adoptedPid = null; _stopRequested = false;
            StartedAt = DateTime.Now;
            _lastCpu = TimeSpan.Zero; _lastSample = DateTime.UtcNow;
            try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
            Logs.Add("system", $"Process started (PID {p.Id}).");
            StartTailing(onlyNewContent: true);
            SetState(ServerState.Running);
        }
    }

    private void Fail(string msg)
    {
        LastError = msg;
        Logs.Add("error", msg);
        SetState(ServerState.Stopped);
    }

    /// <summary>Takes over a server process that was already running when Hostbay started.</summary>
    public void Adopt(int pid)
    {
        lock (_lock)
        {
            if (State != ServerState.Stopped) return;
            Process p;
            try { p = Process.GetProcessById(pid); p.EnableRaisingEvents = true; }
            catch { return; }
            _proc = p; _adopted = true; _adoptedPid = pid; _stopRequested = false;
            try { StartedAt = p.StartTime; } catch { StartedAt = DateTime.Now; }
            p.Exited += (_, _) => OnExited(p);
            Logs.Add("system", $"Found this server already running (PID {pid}). Hostbay can stop it and show its log files, but can't read its console window.");
            StartTailing(onlyNewContent: false);
            SetState(ServerState.Running);
            if (p.HasExited) OnExited(p);
        }
    }

    private void OnStdout(string source, string? line)
    {
        if (line == null) return;
        lock (_recentStdout)
        {
            var key = line.Trim();
            if (key.Length > 0 && _recentStdout.Add(key))
            {
                _recentStdoutOrder.Enqueue(key);
                while (_recentStdoutOrder.Count > 3000) _recentStdout.Remove(_recentStdoutOrder.Dequeue());
            }
        }
        Logs.Add(source, line);
    }

    private void OnExited(Process p)
    {
        int? code = null;
        try { code = p.ExitCode; } catch { }
        bool wasRequested;
        lock (_lock)
        {
            if (_proc != p) return;
            // Let the async readers flush the last lines.
            try { if (!_adopted) p.WaitForExit(); } catch { }
            _proc = null;
            wasRequested = _stopRequested;
            LastExitCode = code;
            StartedAt = null;
            CpuPercent = 0; MemoryBytes = 0;
            _tailCts?.Cancel();
            Logs.Add(wasRequested ? "system" : "error", code is int c ? $"Server exited with code {c}." : "Server exited.");
            SetState(ServerState.Stopped);
        }
        try { p.Dispose(); } catch { }

        if (!wasRequested && Config.AutoRestart)
        {
            var now = DateTime.UtcNow;
            _crashTimes.Enqueue(now);
            while (_crashTimes.Count > 0 && now - _crashTimes.Peek() > TimeSpan.FromMinutes(10)) _crashTimes.Dequeue();
            if (_crashTimes.Count > 5)
            {
                Logs.Add("error", "Auto-restart gave up: the server crashed more than 5 times in 10 minutes.");
                return;
            }
            Logs.Add("system", "Auto-restart is on. Restarting in 10 seconds…");
            SetState(ServerState.Starting);
            _ = Task.Delay(10_000).ContinueWith(_ =>
            {
                if (State == ServerState.Starting) { State = ServerState.Stopped; Start(); }
            });
        }
    }

    // ---------- stop ----------

    public async Task StopAsync()
    {
        Process? p;
        lock (_lock)
        {
            if (State == ServerState.Starting) { SetState(ServerState.Stopped); Logs.Add("system", "Pending restart cancelled."); return; }
            if (State != ServerState.Running || _proc == null) return;
            p = _proc;
            _stopRequested = true;
            SetState(ServerState.Stopping);
        }
        int pid = SafePid(p) ?? 0;
        Logs.Add("system", "Stopping server…");

        bool graceful = false;
        if (Config.StopMethod == StopMethod.Commands && Config.StopCommands.Count > 0)
        {
            try
            {
                foreach (var cmd in Config.StopCommands)
                {
                    await SendCommandAsync(cmd);
                    await Task.Delay(1500);
                }
                graceful = true;
            }
            catch (Exception ex)
            {
                Logs.Add("error", $"Couldn't send the stop commands ({ex.Message}). Trying Ctrl+C instead.");
            }
        }
        if (!graceful && Config.StopMethod != StopMethod.Kill && pid != 0)
        {
            bool sent = await Task.Run(() => Native.SendCtrlC(pid));
            Logs.Add("system", sent ? "Sent Ctrl+C." : "Couldn't send Ctrl+C to the process.");
        }

        if (Config.StopMethod != StopMethod.Kill)
        {
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, Config.StopTimeoutSeconds));
            while (DateTime.UtcNow < deadline && !HasExited(p)) await Task.Delay(250);
        }
        if (!HasExited(p))
        {
            if (Config.StopMethod != StopMethod.Kill)
                Logs.Add("error", $"The server didn't exit within {Config.StopTimeoutSeconds}s, so Hostbay is force-closing it.");
            Native.KillTree(pid);
        }
        if (_adopted) OnExited(p);
    }

    public void Kill()
    {
        Process? p;
        lock (_lock)
        {
            if (State == ServerState.Starting) { SetState(ServerState.Stopped); return; }
            p = _proc;
            if (p == null) return;
            _stopRequested = true;
        }
        Logs.Add("system", "Force-killing server process tree.");
        if (SafePid(p) is int pid) Native.KillTree(pid);
    }

    public async Task RestartAsync()
    {
        Logs.Add("system", "Restarting…");
        await StopAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (State != ServerState.Stopped && DateTime.UtcNow < deadline) await Task.Delay(200);
        await StartAsync();
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    // ---------- commands ----------

    public async Task<string?> SendCommandAsync(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;
        switch (Config.CommandChannel)
        {
            case CommandChannel.Stdin:
                {
                    var p = _proc;
                    if (p == null || State is not (ServerState.Running or ServerState.Stopping)) throw new InvalidOperationException("The server isn't running.");
                    if (_adopted) throw new InvalidOperationException("This server was started outside Hostbay, so its console can't be reached. Restart it from Hostbay to send commands.");
                    Logs.Add("cmd", "> " + command);
                    await p.StandardInput.WriteLineAsync(command);
                    await p.StandardInput.FlushAsync();
                    return null;
                }
            case CommandChannel.Rcon:
            case CommandChannel.Telnet:
                {
                    var rc = ResolveRemoteConsole() ?? throw new InvalidOperationException(
                        Config.CommandChannel == CommandChannel.Rcon
                            ? "RCON isn't set up. Enable it in the server's config (or set a port and password in Settings → Console)."
                            : "Telnet isn't set up. Enable it in serverconfig.xml (or set a port and password in Settings → Console).");
                    Logs.Add("cmd", "> " + command);
                    string reply = Config.CommandChannel == CommandChannel.Rcon
                        ? await RconClient.ExecuteAsync(rc, command)
                        : await TelnetClient.ExecuteAsync(rc, command);
                    foreach (var l in reply.Replace("\r", "").Split('\n'))
                        if (l.Length > 0) Logs.Add("reply", l);
                    return reply;
                }
            default:
                throw new InvalidOperationException($"{Game.Name} doesn't support console commands.");
        }
    }

    // ---------- log file tailing ----------

    public IEnumerable<string> LogFilePatterns() => Game.LogFiles.Concat(Config.ExtraLogFiles).Where(p => !string.IsNullOrWhiteSpace(p));

    private void StartTailing(bool onlyNewContent)
    {
        _tailCts?.Cancel();
        var patterns = LogFilePatterns().ToList();
        if (patterns.Count == 0) return;
        var cts = _tailCts = new CancellationTokenSource();
        var started = DateTime.Now;
        _ = Task.Run(async () =>
        {
            var positions = new Dictionary<string, (long Pos, DateTime Created)>(StringComparer.OrdinalIgnoreCase);
            bool first = true;
            while (!cts.IsCancellationRequested)
            {
                foreach (var pattern in patterns)
                {
                    foreach (var file in ConfigDiscovery.ExpandPattern(pattern, Config.InstallDir))
                    {
                        try { TailOnce(file, positions, first, onlyNewContent, started); } catch { }
                    }
                }
                first = false;
                try { await Task.Delay(500, cts.Token); } catch { break; }
            }
        });
    }

    private void TailOnce(string file, Dictionary<string, (long Pos, DateTime Created)> positions, bool firstPass, bool onlyNewContent, DateTime started)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) return;
        if (!positions.TryGetValue(file, out var st))
        {
            // A log that predates this run belongs to an old session, so skip its contents.
            long startPos = 0;
            if (firstPass && (onlyNewContent ? fi.LastWriteTime < started.AddSeconds(-2) : true))
                startPos = onlyNewContent ? fi.Length : Math.Max(0, fi.Length - 64 * 1024);
            st = (startPos, fi.CreationTimeUtc);
        }
        if (fi.Length < st.Pos || fi.CreationTimeUtc != st.Created) st = (0, fi.CreationTimeUtc); // rotated or truncated
        if (fi.Length > st.Pos)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(st.Pos, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            int lastNl = text.LastIndexOf('\n');
            if (lastNl >= 0)
            {
                var complete = text[..(lastNl + 1)];
                st.Pos += Encoding.UTF8.GetByteCount(complete);
                foreach (var raw in complete.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    bool dup;
                    lock (_recentStdout) dup = _recentStdout.Contains(line.Trim());
                    if (!dup) Logs.Add("file", line);
                }
            }
        }
        positions[file] = st;
    }

    // ---------- stats ----------

    public void SampleStats(Dictionary<int, int> parents)
    {
        var p = _proc;
        int? root = p != null ? SafePid(p) : null;
        if (root == null || State == ServerState.Stopped) { CpuPercent = 0; MemoryBytes = 0; return; }
        TimeSpan cpu = TimeSpan.Zero; long mem = 0;
        foreach (var pid in Native.ProcessTree(root.Value, parents))
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                cpu += proc.TotalProcessorTime;
                mem += proc.WorkingSet64;
            }
            catch { }
        }
        var now = DateTime.UtcNow;
        if (_lastCpu != TimeSpan.Zero)
        {
            var wall = (now - _lastSample).TotalMilliseconds;
            if (wall > 0) CpuPercent = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 0, 100);
        }
        _lastCpu = cpu; _lastSample = now; MemoryBytes = mem;
    }
}
