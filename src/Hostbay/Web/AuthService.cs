using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hostbay.Core;

namespace Hostbay.Web;

/// <summary>
/// Accounts, groups and sessions. The desktop window signs in with a per-launch token and is always the owner;
/// remote browsers sign in with a username and password. Every request re-reads the account and its group, so
/// changes take effect at once; changing a password or disabling an account ends that account's sessions.
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "hb_session";
    public const int MinPasswordLength = 8;
    /// <summary>Exposing Hostbay to the internet needs longer passwords than the home network does.</summary>
    public const int MinInternetPasswordLength = 10;
    private const string LocalUser = "\0local";

    private sealed record Session(string UserId, string Stamp, DateTime Expires);

    private readonly ServerManager _mgr;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _ipFailures = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _userFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AuthService(ServerManager mgr)
    {
        _mgr = mgr;
        LocalToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        EnsureDefaults();
    }

    /// <summary>One-time-per-launch secret the desktop window uses to sign in.</summary>
    public string LocalToken { get; }

    private AppSettings S => _mgr.Settings;

    // ---------- setup & migration ----------

    /// <summary>Creates the built-in groups, and turns the old single remote password into an "owner" account.</summary>
    private void EnsureDefaults()
    {
        lock (_lock)
        {
            bool changed = false;
            if (!S.Groups.Any(g => g.BuiltIn))
            {
                S.Groups.Insert(0, new UserGroup { Name = "Owners", FullAccess = true, BuiltIn = true });
                if (S.Groups.Count == 1)
                {
                    S.Groups.Add(new UserGroup { Name = "Moderators", Permissions = [Permissions.Control, Permissions.Kill, Permissions.Console, Permissions.Configs, Permissions.Settings, Permissions.Ports] });
                    S.Groups.Add(new UserGroup { Name = "Players", Permissions = [Permissions.Control] });
                }
                changed = true;
            }
            var owners = S.Groups.First(g => g.BuiltIn);
            owners.FullAccess = true;
            if (S.Users.Count == 0 && !string.IsNullOrEmpty(S.PasswordHash))
            {
                S.Users.Add(new UserAccount
                {
                    Username = "owner", PasswordHash = S.PasswordHash, PasswordSalt = S.PasswordSalt,
                    GroupId = owners.Id, LongPassword = S.LongPassword,
                });
                S.PasswordHash = ""; S.PasswordSalt = ""; // the account holds it now
                changed = true;
            }
            if (changed) _mgr.Save();
        }
    }

    public bool HasAccounts => S.Users.Any(u => !u.Disabled);

    /// <summary>Internet access is only allowed while every enabled account has a long password.</summary>
    public bool AllPasswordsLong => S.Users.Where(u => !u.Disabled).All(u => u.LongPassword) && HasAccounts;

    // ---------- passwords ----------

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);

    private void SetPassword(UserAccount u, string password)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException($"Use at least {MinPasswordLength} characters.");
        if (password.Length > 200) throw new ArgumentException("That password is too long.");
        var salt = RandomNumberGenerator.GetBytes(16);
        u.PasswordSalt = Convert.ToBase64String(salt);
        u.PasswordHash = Convert.ToBase64String(Hash(password, salt));
        u.LongPassword = password.Length >= MinInternetPasswordLength;
        u.SecurityStamp = Guid.NewGuid().ToString("N"); // signs the account out everywhere else
        if (!AllPasswordsLong) S.InternetAccess = false; // a short password can't face the internet
    }

    private static bool Verify(UserAccount u, string password)
    {
        if (string.IsNullOrEmpty(u.PasswordHash)) return false;
        var expected = Convert.FromBase64String(u.PasswordHash);
        var actual = Hash(password, Convert.FromBase64String(u.PasswordSalt));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // ---------- sign-in ----------

    /// <summary>Checks a username and password, with lockouts per address and per account after repeated failures.</summary>
    public (string? SessionId, string? Error) Login(string username, string password, IPAddress? ip)
    {
        var ipKey = ip?.ToString() ?? "?";
        var userKey = username.Trim();
        foreach (var (map, key) in new[] { (_ipFailures, ipKey), (_userFailures, userKey) })
            if (map.TryGetValue(key, out var f) && f.Count >= 5 && f.Until > DateTime.UtcNow)
                return (null, $"Too many attempts. Try again in {(int)(f.Until - DateTime.UtcNow).TotalSeconds + 1}s.");
        if (!HasAccounts) return (null, "Remote access has no accounts yet. Create one in Hostbay on the host PC.");

        UserAccount? user;
        lock (_lock) user = S.Users.FirstOrDefault(u => string.Equals(u.Username, userKey, StringComparison.OrdinalIgnoreCase));
        // Hash even for unknown names so the response time doesn't reveal which usernames exist.
        bool ok = user != null ? Verify(user, password) : Verify(Dummy, password) && false;
        if (ok && !user!.Disabled)
        {
            _ipFailures.TryRemove(ipKey, out _);
            _userFailures.TryRemove(userKey, out _);
            lock (_lock) { user.LastSignIn = DateTime.UtcNow; }
            _mgr.Save();
            return (NewSession(user.Id, user.SecurityStamp, TimeSpan.FromDays(30)), null);
        }
        Fail(_ipFailures, ipKey, TimeSpan.FromMinutes(1));
        Fail(_userFailures, userKey, TimeSpan.FromMinutes(5));
        return (null, "Wrong username or password.");
    }

    private static readonly UserAccount Dummy = new() { PasswordHash = Convert.ToBase64String(new byte[32]), PasswordSalt = Convert.ToBase64String(new byte[16]) };

    private static void Fail(ConcurrentDictionary<string, (int Count, DateTime Until)> map, string key, TimeSpan lockFor)
    {
        map.AddOrUpdate(key, _ => (1, DateTime.UtcNow.AddMinutes(10)), (_, f) =>
        {
            int count = (f.Until > DateTime.UtcNow ? f.Count : 0) + 1;
            return (count, DateTime.UtcNow + (count >= 5 ? lockFor : TimeSpan.FromMinutes(10)));
        });
    }

    public bool CheckLocalToken(string? token) =>
        token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(LocalToken));

    public string CreateLocalSession() => NewSession(LocalUser, "", TimeSpan.FromDays(30));

    private string NewSession(string userId, string stamp, TimeSpan life)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[id] = new Session(userId, stamp, DateTime.UtcNow + life);
        return id;
    }

    public void EndSession(string? session)
    {
        if (session != null) _sessions.TryRemove(session, out _);
    }

    /// <summary>Who holds this session, with their current rights. Null if it has expired or the account changed.</summary>
    public Principal? Resolve(string? sessionId)
    {
        if (sessionId == null || !_sessions.TryGetValue(sessionId, out var s)) return null;
        if (s.Expires <= DateTime.UtcNow) { _sessions.TryRemove(sessionId, out _); return null; }
        if (s.UserId == LocalUser) return Principal.LocalOwner;
        lock (_lock)
        {
            var u = S.Users.FirstOrDefault(x => x.Id == s.UserId);
            if (u == null || u.Disabled || u.SecurityStamp != s.Stamp) { _sessions.TryRemove(sessionId, out _); return null; }
            var g = S.Groups.FirstOrDefault(x => x.Id == u.GroupId);
            if (g == null) return new Principal(u.Id, u.Username, false, false, new HashSet<string>(), false, new HashSet<string>());
            return new Principal(u.Id, u.Username, false, g.FullAccess,
                new HashSet<string>(g.Permissions.Where(Permissions.IsKnown)), g.AllServers || g.FullAccess, new HashSet<string>(g.ServerIds));
        }
    }

    // ---------- your own account ----------

    public void ChangeOwnPassword(Principal p, string current, string next)
    {
        if (p.UserId == null) throw new InvalidOperationException("The desktop window doesn't have a password. Manage accounts under Users.");
        lock (_lock)
        {
            var u = S.Users.First(x => x.Id == p.UserId);
            if (!Verify(u, current)) throw new UnauthorizedAccessException("Your current password isn't right.");
            SetPassword(u, next);
        }
        _mgr.Save();
    }

    /// <summary>A new session for someone who just changed their password (their old ones ended with it).</summary>
    public string? RenewSession(Principal p)
    {
        lock (_lock)
        {
            var u = S.Users.FirstOrDefault(x => x.Id == p.UserId);
            return u == null ? null : NewSession(u.Id, u.SecurityStamp, TimeSpan.FromDays(30));
        }
    }

    // ---------- accounts (owner only) ----------

    private static readonly Regex UsernameRule = new("^[A-Za-z0-9_.-]{2,32}$");

    public UserAccount CreateUser(string username, string password, string groupId)
    {
        username = username.Trim();
        if (!UsernameRule.IsMatch(username)) throw new ArgumentException("Usernames are 2-32 letters, numbers, dots, dashes or underscores.");
        lock (_lock)
        {
            if (S.Users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That username is taken.");
            if (!S.Groups.Any(g => g.Id == groupId)) throw new ArgumentException("Pick a group.");
            var user = new UserAccount { Username = username, GroupId = groupId };
            SetPassword(user, password);
            S.Users.Add(user);
            _mgr.Save();
            return user;
        }
    }

    public void UpdateUser(string id, string? groupId, bool? disabled, string? newPassword)
    {
        lock (_lock)
        {
            var u = S.Users.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("No such account.");
            if (groupId != null)
            {
                if (!S.Groups.Any(g => g.Id == groupId)) throw new ArgumentException("Pick a group.");
                if (WouldLoseLastOwner(u, groupId, disabled ?? u.Disabled)) throw new InvalidOperationException("Keep at least one enabled account with full access.");
                u.GroupId = groupId;
            }
            if (disabled is bool d)
            {
                if (d && WouldLoseLastOwner(u, u.GroupId, true)) throw new InvalidOperationException("Keep at least one enabled account with full access.");
                if (d != u.Disabled) { u.Disabled = d; u.SecurityStamp = Guid.NewGuid().ToString("N"); }
            }
            if (!string.IsNullOrEmpty(newPassword)) SetPassword(u, newPassword);
            if (!AllPasswordsLong) S.InternetAccess = false;
            _mgr.Save();
        }
    }

    public void DeleteUser(string id)
    {
        lock (_lock)
        {
            var u = S.Users.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("No such account.");
            if (WouldLoseLastOwner(u, null, true)) throw new InvalidOperationException("Keep at least one enabled account with full access.");
            S.Users.Remove(u);
            if (!HasAccounts) { S.RemoteAccess = false; S.InternetAccess = false; }
            _mgr.Save();
        }
    }

    /// <summary>
    /// Remote access stays reachable for the owner only while some enabled account has full access. (The desktop
    /// window always has full access, so this only guards remote use, and only once accounts exist.)
    /// </summary>
    private bool WouldLoseLastOwner(UserAccount changing, string? newGroupId, bool newDisabled)
    {
        bool IsFull(string gid) => S.Groups.FirstOrDefault(g => g.Id == gid)?.FullAccess == true;
        bool stillFull = newGroupId != null && !newDisabled && IsFull(newGroupId);
        if (stillFull) return false;
        return !S.Users.Any(u => u.Id != changing.Id && !u.Disabled && IsFull(u.GroupId)) && IsFull(changing.GroupId) && !changing.Disabled;
    }

    // ---------- groups (owner only) ----------

    public UserGroup SaveGroup(UserGroup input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > 40) throw new ArgumentException("Give the group a name (up to 40 characters).");
        lock (_lock)
        {
            if (S.Groups.Any(g => g.Id != input.Id && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("There's already a group with that name.");
            var existing = S.Groups.FirstOrDefault(g => g.Id == input.Id);
            var g = existing ?? new UserGroup();
            g.Name = name;
            g.Permissions = (input.Permissions ?? new()).Where(Permissions.IsKnown).Distinct().ToList();
            g.AllServers = input.AllServers;
            g.ServerIds = (input.ServerIds ?? new()).Where(id => S.Servers.Any(s => s.Id == id)).Distinct().ToList();
            if (g.BuiltIn) g.FullAccess = true; // Owners always keep everything
            else
            {
                if (existing != null && existing.FullAccess && !input.FullAccess
                    && !S.Users.Any(u => !u.Disabled && u.GroupId != g.Id && S.Groups.FirstOrDefault(x => x.Id == u.GroupId)?.FullAccess == true))
                    throw new InvalidOperationException("Keep at least one enabled account with full access.");
                g.FullAccess = input.FullAccess;
            }
            if (existing == null) S.Groups.Add(g);
            _mgr.Save();
            return g;
        }
    }

    public void DeleteGroup(string id)
    {
        lock (_lock)
        {
            var g = S.Groups.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("No such group.");
            if (g.BuiltIn) throw new InvalidOperationException("The Owners group can't be deleted.");
            if (S.Users.Any(u => u.GroupId == id)) throw new InvalidOperationException("Move this group's members to another group first.");
            S.Groups.Remove(g);
            _mgr.Save();
        }
    }

    /// <summary>When a server is removed, drop it from groups' server lists.</summary>
    public void ForgetServer(string serverId)
    {
        lock (_lock) foreach (var g in S.Groups) g.ServerIds.Remove(serverId);
    }
}
