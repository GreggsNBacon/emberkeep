using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hostbay.Core;

/// <summary>A launch argument that turned out to be encoded JSON, e.g. Assetto Corsa EVO's "-serverconfig &lt;base64&gt;".</summary>
public sealed record ArgBlob(int Index, string? Flag, string Encoding, string Json);

/// <summary>
/// Some games' launchers pass the whole configuration inside the command line as encoded JSON. Hostbay keeps that JSON in a
/// real file (so it shows up in the config editor) and writes <c>{file:path|encoding}</c> in the arguments; the file is
/// encoded again every time the server starts.
/// Encodings: base64 (plain JSON), zlib, gzip, and zlib-len (a 4-byte big-endian length, then zlib: Assetto Corsa EVO).
/// </summary>
public static class ArgBlobs
{
    public const string Folder = "HostbayConfig";
    private static readonly Regex Base64Token = new(@"^[A-Za-z0-9+/_-]{24,}={0,2}$", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{file:([^|{}""]+)\|(base64|zlib|zlib-len|gzip)\}", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------------- command lines ----------------

    /// <summary>Splits a Windows command line the way programs see it (quotes group, backslashes before quotes escape).</summary>
    public static List<string> Split(string args)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '\\')
            {
                int n = 0; while (i < args.Length && args[i] == '\\') { n++; i++; }
                if (i < args.Length && args[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) { cur.Append('"'); } else { quoted = !quoted; } any = true; }
                else { cur.Append('\\', n); i--; any = true; }
                continue;
            }
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c); any = true;
        }
        if (any) list.Add(cur.ToString());
        return list;
    }

    /// <summary>Joins arguments back into a command line, quoting where needed.</summary>
    public static string Join(IEnumerable<string> parts) => string.Join(" ", parts.Select(Quote));

    private static string Quote(string a)
    {
        if (a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"')) return a;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < a.Length; i++)
        {
            int n = 0; while (i < a.Length && a[i] == '\\') { n++; i++; }
            if (i == a.Length) { sb.Append('\\', n * 2); break; }
            if (a[i] == '"') sb.Append('\\', n * 2 + 1).Append('"');
            else sb.Append('\\', n).Append(a[i]);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Splits a full command line (as read from a running process) into the program and its arguments.</summary>
    public static (string Exe, string Args) SplitProgram(string commandLine)
    {
        var s = commandLine.TrimStart();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end < 0 ? (s.Trim('"'), "") : (s[1..end], s[(end + 1)..].Trim());
        }
        int sp = s.IndexOfAny([' ', '\t']);
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }

    // ---------------- finding and decoding ----------------

    public static List<ArgBlob> Find(string args)
    {
        var parts = Split(args);
        var found = new List<ArgBlob>();
        for (int i = 0; i < parts.Count; i++)
        {
            // Also handles "-flag=<blob>".
            var tok = parts[i];
            string? flag = i > 0 && parts[i - 1].StartsWith('-') || i > 0 && parts[i - 1].StartsWith('+') ? parts[i - 1] : null;
            int eq = tok.IndexOf('=');
            if (eq > 0 && (tok[0] == '-' || tok[0] == '+') && eq < tok.Length - 1) { flag = tok[..eq]; tok = tok[(eq + 1)..]; }
            if (TryDecode(tok, out var enc, out var json)) found.Add(new ArgBlob(i, flag, enc, json));
        }
        return found;
    }

    public static bool TryDecode(string token, out string encoding, out string json)
    {
        encoding = ""; json = "";
        if (!Base64Token.IsMatch(token)) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/').PadRight((token.Length + 3) / 4 * 4, '=')); }
        catch { return false; }

        if (bytes.Length > 6)
        {
            int len = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
            if (len > 0 && len < 16 << 20 && Inflate(bytes.AsSpan(4).ToArray(), gzip: false) is { } a && Encoding.UTF8.GetByteCount(a) == len && IsJson(a))
            { encoding = "zlib-len"; json = a; return true; }
        }
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b && Inflate(bytes, gzip: true) is { } g && IsJson(g)) { encoding = "gzip"; json = g; return true; }
        if (Inflate(bytes, gzip: false) is { } z && IsJson(z)) { encoding = "zlib"; json = z; return true; }
        try
        {
            var plain = new UTF8Encoding(false, true).GetString(bytes);
            if (IsJson(plain)) { encoding = "base64"; json = plain; return true; }
        }
        catch { }
        return false;
    }

    private static string? Inflate(byte[] data, bool gzip)
    {
        try
        {
            using var input = new MemoryStream(data);
            using Stream z = gzip ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buf = new byte[81920]; int n; long total = 0;
            while ((n = z.Read(buf, 0, buf.Length)) > 0) { output.Write(buf, 0, n); if ((total += n) > 16 << 20) return null; }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch { return null; }
    }

    private static bool IsJson(string s)
    {
        var t = s.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (t.Length < 2 || (t[0] != '{' && t[0] != '[')) return false;
        try { using var _ = JsonDocument.Parse(t); return true; } catch { return false; }
    }

    public static string Encode(string encoding, string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        byte[] Compress(bool gzip)
        {
            using var ms = new MemoryStream();
            using (Stream z = gzip ? new GZipStream(ms, CompressionLevel.Optimal) : new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }
        return Convert.ToBase64String(encoding switch
        {
            "base64" => raw,
            "zlib" => Compress(false),
            "gzip" => Compress(true),
            "zlib-len" => [(byte)(raw.Length >> 24), (byte)(raw.Length >> 16), (byte)(raw.Length >> 8), (byte)raw.Length, .. Compress(false)],
            _ => throw new ArgumentException($"Unknown encoding {encoding}"),
        });
    }

    // ---------------- files and placeholders ----------------

    public static bool HasPlaceholders(string? args) => args != null && Placeholder.IsMatch(args);

    /// <summary>The files the arguments' placeholders point at (absolute paths).</summary>
    public static IEnumerable<string> PlaceholderFiles(string? args, string baseDir) =>
        args == null ? [] : Placeholder.Matches(args).Select(m => Path.GetFullPath(Path.Combine(baseDir, m.Groups[1].Value.Trim())));

    /// <summary>Replaces each {file:…|…} with that file's JSON, encoded. Throws InvalidOperationException with a readable message.</summary>
    public static string Expand(string args, string baseDir) => Placeholder.Replace(args, m =>
    {
        var rel = m.Groups[1].Value.Trim();
        var path = Path.GetFullPath(Path.Combine(baseDir, rel));
        if (!File.Exists(path)) throw new InvalidOperationException($"The launch arguments use {rel}, but that file doesn't exist.");
        var json = File.ReadAllText(path).TrimStart('﻿');
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidOperationException($"{rel} isn't valid JSON ({ex.Message}). Fix it in Config files."); }
        return Encode(m.Groups[2].Value, json);
    });

    /// <summary>
    /// Turns every encoded blob in the arguments into a file under <see cref="Folder"/> and a placeholder.
    /// Returns the new arguments plus the files to write (path relative to the install dir, pretty JSON).
    /// </summary>
    public static (string Args, List<(string RelPath, string Json)> Files) Extract(string args)
    {
        var parts = Split(args);
        var files = new List<(string, string)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Find(args))
        {
            var stem = Regex.Replace((b.Flag ?? "").TrimStart('-', '+', '/'), @"[^A-Za-z0-9_.-]", "");
            if (stem.Length == 0) stem = "config";
            var name = stem + ".json";
            for (int n = 2; !used.Add(name); n++) name = $"{stem}{n}.json";
            var rel = Path.Combine(Folder, name);
            string pretty;
            try { pretty = JsonNode.Parse(b.Json)!.ToJsonString(Pretty); } catch { pretty = b.Json; }
            files.Add((rel, pretty));
            var placeholder = $"{{file:{rel}|{b.Encoding}}}";
            var tok = parts[b.Index];
            int eq = tok.IndexOf('=');
            parts[b.Index] = eq > 0 && (tok[0] == '-' || tok[0] == '+') && b.Flag == tok[..eq] ? tok[..(eq + 1)] + placeholder : placeholder;
        }
        return (Join(parts), files);
    }

    /// <summary>For display: long encoded blobs shortened to "‹JSON, 1.2 KB›".</summary>
    public static string Summarize(string args)
    {
        var parts = Split(args);
        foreach (var b in Find(args)) parts[b.Index] = $"‹{b.Flag?.TrimStart('-', '+') ?? "settings"} JSON, {Encoding.UTF8.GetByteCount(b.Json) / 1024.0:0.#} KB›";
        return string.Join(" ", parts.Select(p => p.Length > 200 ? p[..60] + "…" : p));
    }
}
