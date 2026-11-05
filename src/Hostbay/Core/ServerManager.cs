using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hostbay.Core;

/// <summary>Owns the persisted settings and the live runtime for each server.</summary>
public sealed class ServerManager : IDisposable
{
    public static readonly string DataDir = Environment.GetEnvironmentVariable("HOSTBAY_DATA_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hostbay");
    private static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    public static readonly string BackupDir = Path.Combine(DataDir, "backups");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions JsonCompact = new(Json) { WriteIndented = false };

    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, ServerRuntime> _runtimes = new();
    private readonly Timer _statsTimer;
    private int _tick;

    public AppSettings Settings { get; private set; }

    /// <summary>Raised when any server's state changes (start, stop, crash…).</summary>
    public event Action? Changed;

    public ServerManager()
    {
        Directory.CreateDirectory(DataDir);
        Settings = Load();
        foreach (var s in Settings.Servers) Attach(s);
        DetectRunning();
        _statsTimer = new Timer(_ => Tick(), null, 2000, 2000);
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch
        {
            // Keep the unreadable file for the user instead of overwriting it.
            try { File.Copy(SettingsPath, SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_saveLock)
        {
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Json));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
    }

    private ServerRuntime Attach(ServerInstance s)
    {
        var rt = new ServerRuntime(s);
        rt.Changed += () => Changed?.Invoke();
        _runtimes[s.Id] = rt;
        return rt;
    }

    public IEnumerable<ServerRuntime> Runtimes => Settings.Servers.Select(s => _runtimes[s.Id]);

    public ServerRuntime? Get(string id) => _runtimes.GetValueOrDefault(id);

    public ServerRuntime Add(ServerInstance s)
    {
        lock (_saveLock) Settings.Servers.Add(s);
        Save();
        var rt = Attach(s);
        DetectRunning();
        Changed?.Invoke();
        return rt;
    }

    public void Update(string id, ServerInstance updated)
    {
        var rt = Get(id) ?? throw new KeyNotFoundException();
        updated.Id = id;
        updated.AddedAt = rt.Config.AddedAt;
        lock (_saveLock)
        {
            int i = Settings.Servers.FindIndex(s => s.Id == id);
            Settings.Servers[i] = updated;
        }
        rt.Config = updated;
        Save();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        var rt = Get(id);
        if (rt != null && rt.State != ServerState.Stopped) throw new InvalidOperationException("Stop the server before removing it.");
        lock (_saveLock) Settings.Servers.RemoveAll(s => s.Id == id);
        _runtimes.TryRemove(id, out _);
        Save();
        Changed?.Invoke();
    }

    public void Reorder(IList<string> ids)
    {
        lock (_saveLock)
        {
            Settings.Servers = Settings.Servers.OrderBy(s => { int i = ids.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; }).ToList();
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Finds servers that are already running (e.g. started before Hostbay) and attaches to them.</summary>
    public void DetectRunning()
    {
        var stopped = Runtimes.Where(r => r.State == ServerState.Stopped).ToList();
        if (stopped.Count == 0) return;
        var byPath = new Dictionary<string, ServerRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var rt in stopped)
        {
            var exe = rt.ResolveExecutable();
            if (!Path.IsPathRooted(exe)) continue; // java, cmd… too generic to match safely
            var ext = Path.GetExtension(exe);
            if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) continue;
            byPath.TryAdd(Path.GetFullPath(exe), rt);
        }
        if (byPath.Count == 0) return;
        var names = byPath.Keys.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(p.ProcessName)) continue;
                var path = Native.ImagePath(p.Id);
                if (path != null && byPath.TryGetValue(path, out var rt)) rt.Adopt(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private void Tick()
    {
        try
        {
            var running = Runtimes.Where(r => r.State != ServerState.Stopped).ToList();
            if (running.Count > 0)
            {
                var parents = Native.ParentMap();
                foreach (var r in running) r.SampleStats(parents);
            }
            if (++_tick % 5 == 0) DetectRunning();
        }
        catch { }
    }

    public async Task StopAllAsync()
    {
        await Task.WhenAll(Runtimes.Where(r => r.State is ServerState.Running or ServerState.Starting).Select(r => r.StopAsync()));
    }

    public void Dispose() => _statsTimer.Dispose();
}
