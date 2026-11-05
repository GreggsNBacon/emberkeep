using System;
using System.Collections.Generic;
using System.Linq;

namespace Hostbay.Core;

/// <summary>A remote sign-in. Passwords are stored as salted PBKDF2 hashes only.</summary>
public sealed class UserAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string GroupId { get; set; } = "";
    public bool Disabled { get; set; }
    /// <summary>Password is 10+ characters, long enough for internet access.</summary>
    public bool LongPassword { get; set; }
    /// <summary>Changes whenever the password changes or the account is disabled; sessions holding an old stamp end.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSignIn { get; set; }
}

/// <summary>What a group's members may do, and on which servers.</summary>
public sealed class UserGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>Everything, including accounts, app settings and every server.</summary>
    public bool FullAccess { get; set; }
    public List<string> Permissions { get; set; } = new();
    public bool AllServers { get; set; } = true;
    public List<string> ServerIds { get; set; } = new();
    /// <summary>The Owners group can't be deleted or lose full access, so the host can never lock themselves out.</summary>
    public bool BuiltIn { get; set; }
}

/// <summary>Per-server abilities a group can be given. Seeing a server (status, logs, join address) comes with access to it.</summary>
public static class Permissions
{
    public const string Control = "control";   // start, stop, restart
    public const string Kill = "kill";         // force-close
    public const string Console = "console";   // send console commands, clear the log
    public const string Configs = "configs";   // read and edit config files (they can hold passwords)
    public const string Settings = "settings"; // name, auto-restart, stopping, join port, router ports list, artwork, RCON
    public const string Ports = "ports";       // open/close router ports
    public const string Launch = "launch";     // executable, arguments, folders, environment, add/remove servers: can run programs on the host

    public static readonly IReadOnlyList<(string Id, string Label, string Help)> All =
    [
        (Control, "Start, stop and restart", "Use the Start, Stop and Restart buttons."),
        (Kill, "Force-close", "Close a server immediately without saving."),
        (Console, "Send console commands", "Type commands into the server console and clear its log."),
        (Configs, "Edit config files", "Read and change the server's config files. These often contain admin passwords."),
        (Settings, "Change server settings", "Name, auto-restart, stopping, join port, router port list, artwork and remote console details."),
        (Ports, "Open and close router ports", "Use the Router ports button."),
        (Launch, "Change launch settings and add or remove servers", "Executable, arguments, folders and environment. This lets someone run any program on this PC, so only give it to people you trust completely."),
    ];

    public static bool IsKnown(string p) => All.Any(a => a.Id == p);
}

/// <summary>Who is making a request and what they may do. Built fresh for every request from the current settings.</summary>
public sealed record Principal(string? UserId, string Username, bool IsLocal, bool Full, IReadOnlySet<string> Perms, bool AllServers, IReadOnlySet<string> ServerIds)
{
    public static Principal LocalOwner { get; } = new(null, "This PC", true, true, new HashSet<string>(), true, new HashSet<string>());

    public bool CanSee(string serverId) => Full || AllServers || ServerIds.Contains(serverId);
    public bool Can(string perm, string serverId) => CanSee(serverId) && (Full || Perms.Contains(perm));
    /// <summary>A permission on at least one server (for app-wide actions such as adding a server).</summary>
    public bool CanAnywhere(string perm) => Full || Perms.Contains(perm);
}
