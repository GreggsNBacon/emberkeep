using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Emberkeep.Core;

/// <summary>
/// Process helpers: Ctrl+C delivery, process tree enumeration and image paths. Win32 on Windows; /proc and signals on Linux.
/// </summary>
public static class Native
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int sys_kill(int pid, int sig);
    private const int SIGINT = 2;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint groupId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    public static readonly object ConsoleLock = new();

    /// <summary>
    /// Clears the "ignore Ctrl+C" flag, which child processes inherit. Without this, a Emberkeep launched from some
    /// shells would start servers that can never be stopped gracefully. Call while holding <see cref="ConsoleLock"/>.
    /// </summary>
    public static void AllowCtrlCInChildren() { if (OperatingSystem.IsWindows()) SetConsoleCtrlHandler(IntPtr.Zero, false); }

    /// <summary>Sends Ctrl+C to a console process, the same as pressing it in the server window.</summary>
    public static bool SendCtrlC(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux: SIGINT to the server and everything it started, as Ctrl+C in a terminal does to the foreground
            // process group (a launch script such as PalServer.sh doesn't pass the signal on to the game itself).
            bool any = false;
            foreach (var p in ProcessTree(pid)) any |= sys_kill(p, SIGINT) == 0;
            return any;
        }
        lock (ConsoleLock)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) return false;
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true); // don't kill ourselves
                return GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
            }
            finally
            {
                // Give the event time to be delivered before detaching and re-enabling our own handler.
                Thread.Sleep(300);
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    /// <summary>Returns pid → parent pid for every process on the system.</summary>
    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows())
        {
            // /proc/<pid>/stat: "pid (name) state ppid …"; the name can contain spaces and brackets, so read after the last ')'.
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (rest.Length > 1 && int.TryParse(rest[1], out int ppid)) map[pid] = ppid;
                }
                catch { }
            }
            return map;
        }
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
            {
                do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; } while (Process32NextW(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>The root pid plus all descendants.</summary>
    public static List<int> ProcessTree(int rootPid, Dictionary<int, int>? parents = null)
    {
        parents ??= ParentMap();
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, ppid) in parents)
        {
            if (pid == ppid) continue;
            if (!children.TryGetValue(ppid, out var list)) children[ppid] = list = new();
            list.Add(pid);
        }
        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(rootPid);
        var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            int p = stack.Pop();
            if (!seen.Add(p)) continue;
            result.Add(p);
            if (children.TryGetValue(p, out var kids)) foreach (var k in kids) stack.Push(k);
        }
        return result;
    }

    public static string? ImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { return new FileInfo($"/proc/{pid}/exe").LinkTarget; } catch { return null; }
        }
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr buffer, int length, out int returnLength);

    /// <summary>The full command line another process was started with (Windows 8.1+), or null if it can't be read.</summary>
    public static string? CommandLine(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            // NUL-separated arguments; quote the ones with spaces so it reads like a command line.
            try
            {
                var parts = File.ReadAllText($"/proc/{pid}/cmdline").TrimEnd('\0').Split('\0');
                return parts.Length == 0 || parts[0].Length == 0 ? null : string.Join(' ', parts.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            }
            catch { return null; }
        }
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(h, 60 /* ProcessCommandLineInformation */, IntPtr.Zero, 0, out int needed);
            if (needed <= 0 || needed > 1 << 20) return null;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(h, 60, buf, needed, out _) != 0) return null;
                // UNICODE_STRING { ushort Length; ushort MaximumLength; (pad) IntPtr Buffer; }
                int bytes = Marshal.ReadInt16(buf) & 0xFFFF;
                var text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True if the exe is a windowed (GUI) program rather than a console one, read from its PE header.</summary>
    public static bool? IsGuiExe(string path)
    {
        if (!OperatingSystem.IsWindows()) return null; // Linux programs have no console/windowed flag
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt16() != 0x5A4D) return null; // "MZ"
            fs.Position = 0x3C;
            int pe = r.ReadInt32();
            fs.Position = pe;
            if (r.ReadUInt32() != 0x00004550) return null; // "PE\0\0"
            fs.Position = pe + 24 + 68; // optional header → Subsystem (same offset for PE32 and PE32+)
            return r.ReadUInt16() switch { 2 => true, 3 => false, _ => null };
        }
        catch { return null; }
    }

    public static void KillTree(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
    }
}
