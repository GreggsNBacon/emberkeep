using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Emberkeep.Core;

namespace Emberkeep.Web;

/// <summary>Reads and writes config files while preserving their encoding and line endings, with backups.</summary>
public static class ConfigFiles
{
    private const int KeepBackups = 25;

    public static (string Text, Encoding Encoding, string Newline) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding enc;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) enc = new UTF8Encoding(true);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) enc = new UnicodeEncoding(false, true);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) enc = new UnicodeEncoding(true, true);
        else enc = new UTF8Encoding(false);
        var text = enc.GetString(bytes, enc.GetPreamble().Length, bytes.Length - enc.GetPreamble().Length);
        var newline = text.Contains("\r\n") || !text.Contains('\n') ? "\r\n" : "\n";
        return (text, enc, newline);
    }

    /// <summary>Saves new content, keeping the file's encoding and line endings. Returns the backup file name, if any.</summary>
    public static string? Write(ServerInstance s, string path, string content)
    {
        Encoding enc = new UTF8Encoding(false);
        string newline = Platform.IsWindows ? "\r\n" : "\n"; // new files: this system's line endings
        string? backup = null;
        if (File.Exists(path))
        {
            (_, enc, newline) = Read(path);
            backup = Backup(s, path);
        }
        var normalized = content.Replace("\r\n", "\n");
        if (newline == "\r\n") normalized = normalized.Replace("\n", "\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".emberkeep-tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var pre = enc.GetPreamble();
            fs.Write(pre);
            fs.Write(enc.GetBytes(normalized));
        }
        File.Move(tmp, path, overwrite: true);
        return backup;
    }

    private static string BackupFolder(ServerInstance s, string path)
    {
        // One folder per server and file, keyed by a short hash of the full path so same-named files don't collide.
        path = Path.GetFullPath(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..8];
        return Path.Combine(ServerManager.BackupDir, s.Id, Path.GetFileName(path) + "-" + hash);
    }

    public static string Backup(ServerInstance s, string path)
    {
        var dir = BackupFolder(s, path);
        Directory.CreateDirectory(dir);
        // Millisecond stamps keep names unique and sortable.
        var name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + Path.GetExtension(path);
        File.Copy(path, Path.Combine(dir, name), overwrite: true);
        foreach (var old in new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.Name).Skip(KeepBackups)) old.Delete();
        return name;
    }

    public sealed record BackupInfo(string Name, string FullPath, DateTime Created, long Size);

    public static List<BackupInfo> ListBackups(ServerInstance s, string path)
    {
        var dir = new DirectoryInfo(BackupFolder(s, Path.GetFullPath(path)));
        if (!dir.Exists) return new();
        return dir.GetFiles().OrderByDescending(f => f.Name).Select(f => new BackupInfo(f.Name, f.FullName, f.LastWriteTimeUtc, f.Length)).ToList();
    }
}
