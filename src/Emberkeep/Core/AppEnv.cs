using System;
using System.IO;

namespace Emberkeep.Core;

/// <summary>
/// Settings read from environment variables (mostly test hooks). Emberkeep was called Hostbay, so the old
/// HOSTBAY_* names still work.
/// </summary>
public static class AppEnv
{
    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable("EMBERKEEP_" + name) is { Length: > 0 } v ? v
        : Environment.GetEnvironmentVariable("HOSTBAY_" + name) is { Length: > 0 } old ? old : null;

    /// <summary>
    /// Emberkeep's data folder (%APPDATA%\Emberkeep). The first run after the rename moves Hostbay's folder
    /// (%APPDATA%\Hostbay) across, so servers, accounts and settings carry over.
    /// </summary>
    public static string ResolveDataDir()
    {
        if (Get("DATA_DIR") is { } custom) return custom;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Emberkeep");
        var old = Path.Combine(appData, "Hostbay");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { Directory.Move(old, dir); }
            catch
            {
                // Something still has a file open: copy instead, leaving the old folder as it was.
                try { CopyTree(old, dir); } catch { return old; }
            }
        }
        return dir;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
