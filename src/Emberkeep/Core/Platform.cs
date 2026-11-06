using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Emberkeep.Core;

/// <summary>
/// The few things that differ between the Windows app and the Linux service: paths written Windows-style in the game
/// catalog and in settings (backslashes, %USERPROFILE%…), and how secrets are kept.
/// </summary>
public static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static string Name => IsWindows ? "windows" : IsLinux ? "linux" : "other";

    /// <summary>An example full folder path for messages.</summary>
    public static string ExampleFolder(string? sub = null) =>
        IsWindows ? @"D:\Game Servers" + (sub != null ? @"\" + sub : "") : "/srv/game-servers" + (sub != null ? "/" + sub.ToLowerInvariant() : "");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// A path as this system writes it: on Linux, backslashes become slashes and Windows variables become their Linux
    /// equivalents (%USERPROFILE% → home, %APPDATA% → ~/.config, %LOCALAPPDATA% → ~/.local/share). "~/" is the home folder.
    /// </summary>
    public static string Path(string? p)
    {
        if (string.IsNullOrEmpty(p)) return p ?? "";
        if (IsWindows) return Environment.ExpandEnvironmentVariables(p);
        p = p.Replace('\\', '/');
        p = Replace(p, "%USERPROFILE%", Home);
        p = Replace(p, "%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        p = Replace(p, "%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (p == "~" || p.StartsWith("~/")) p = Home + p[1..];
        return Environment.ExpandEnvironmentVariables(p);
    }

    private static string Replace(string s, string var, string value) => s.Replace(var, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Linux: makes a file executable (server scripts and programs downloaded by SteamCMD usually are, but not always).</summary>
    public static void MakeExecutable(string file)
    {
        if (IsWindows || !File.Exists(file)) return;
        try
        {
            var m = File.GetUnixFileMode(file);
            const UnixFileMode x = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((m & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(file, m | x);
        }
        catch { }
    }

    // ---------- secrets ----------

    /// <summary>
    /// Encrypts a secret (the Discord bot token) for this machine and user: DPAPI on Windows; on Linux AES-GCM with a
    /// random key kept in the data folder, readable only by the user Emberkeep runs as.
    /// </summary>
    public static string Protect(string secret, byte[] entropy)
    {
        var data = Encoding.UTF8.GetBytes(secret);
        if (IsWindows)
            return Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, data, cipher, tag, entropy);
        var all = new byte[1 + 12 + 16 + cipher.Length];
        all[0] = 1; // format version
        nonce.CopyTo(all, 1); tag.CopyTo(all, 13); cipher.CopyTo(all, 29);
        return Convert.ToBase64String(all);
    }

    public static string? Unprotect(string protectedText, byte[] entropy)
    {
        try
        {
            var all = Convert.FromBase64String(protectedText);
            if (IsWindows) return Encoding.UTF8.GetString(ProtectedData.Unprotect(all, entropy, DataProtectionScope.CurrentUser));
            if (all.Length < 29 || all[0] != 1) return null;
            var plain = new byte[all.Length - 29];
            using (var aes = new AesGcm(Key(), 16)) aes.Decrypt(all.AsSpan(1, 12), all.AsSpan(29), all.AsSpan(13, 16), plain, entropy);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    private static readonly object KeyLock = new();
    private static byte[]? _key;

    private static byte[] Key()
    {
        lock (KeyLock)
        {
            if (_key != null) return _key;
            var path = System.IO.Path.Combine(ServerManager.DataDir, "secret.key");
            if (File.Exists(path) && File.ReadAllBytes(path) is { Length: 32 } k) return _key = k;
            Directory.CreateDirectory(ServerManager.DataDir);
            var key = RandomNumberGenerator.GetBytes(32);
            using (var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, // 0600
            }))
                fs.Write(key);
            return _key = key;
        }
    }
}
