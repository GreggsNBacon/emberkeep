using System;
using Microsoft.Win32;

namespace Emberkeep.Core;

/// <summary>
/// "Start Emberkeep when Windows starts": a per-user entry under HKCU\...\Run (no admin needed) that opens Emberkeep in the
/// tray. Windows itself is the record of whether it's on, so turning it off in Task Manager's Startup apps shows here too.
/// </summary>
public static class WindowsStartup
{
    // EMBERKEEP_STARTUP_KEY points tests at a scratch key instead of the real startup list.
    private static readonly string RunKey = AppEnv.Get("STARTUP_KEY") ?? @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Emberkeep";

    private static string ExePath => Environment.ProcessPath ?? "";
    private static string Command => $"\"{ExePath}\" --minimized";

    /// <summary>The program the entry starts, or null when there is none.</summary>
    public static string? RegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) as string;
    }

    /// <summary>On, and starting this copy of Emberkeep (not, say, a copy that was moved or a test build).</summary>
    public static bool Enabled => string.Equals(RegisteredCommand(), Command, StringComparison.OrdinalIgnoreCase);

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (on) key.SetValue(Name, Command, RegistryValueKind.String);
        else if (key.GetValue(Name) != null) key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
