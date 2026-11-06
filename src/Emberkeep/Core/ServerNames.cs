using System;
using System.Text.RegularExpressions;

namespace Emberkeep.Core;

/// <summary>Tidies server names: Steam calls its tools "Palworld Dedicated Server", "Abiotic Factor - Dedicated Server" and so on.</summary>
public static class ServerNames
{
    // Trailing "Dedicated Server", "- Server", "(Dedicated)", "DS", "Server Tool", "for Windows"…, with any separator before them.
    private static readonly Regex Suffix = new(
        @"[\s\-–—:|_,]*[\(\[]?\s*\b(?:dedicated\s+server(?:\s+tool)?|dedicated|server\s+tool|server|ds|for\s+windows|windows\s+x64)\s*[\)\]]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Only Steam's phrasing, for renaming servers that are already added: leaves names like "Friends Server" alone.
    private static readonly Regex SteamSuffix = new(
        @"(?:[\s\-–—:|_,]*[\(\[]?\s*\bdedicated(?:\s+server)?(?:\s+tool)?\s*[\)\]]?|\s+[\-–—:|]\s*server|\s+ds|\s+server\s+tool)(?:\s+for\s+windows)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Abiotic Factor - Dedicated Server" → "Abiotic Factor". Returns the name unchanged if nothing would be left.</summary>
    public static string Clean(string name) => Strip(name, Suffix);

    /// <summary>The gentler clean used on servers already in the list.</summary>
    public static string CleanSteamSuffix(string name) => Strip(name, SteamSuffix);

    private static string Strip(string name, Regex re)
    {
        var s = name.Trim();
        for (int i = 0; i < 4; i++)
        {
            var next = re.Replace(s, "").TrimEnd(' ', '-', '–', '—', ':', '|', '_', ',');
            if (next == s) break;
            if (next.Length == 0) return name.Trim();
            s = next;
        }
        return s;
    }
}
