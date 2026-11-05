using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hostbay.Core;

namespace Hostbay.Web;

/// <summary>Session cookies for the UI. The desktop window logs in with a per-launch token; remote browsers use the password.</summary>
public sealed class AuthService
{
    public const string CookieName = "hb_session";
    private readonly ServerManager _mgr;
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTime Until)> _failures = new();

    public AuthService(ServerManager mgr)
    {
        _mgr = mgr;
        LocalToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    /// <summary>One-time-per-launch secret the desktop window uses to sign in.</summary>
    public string LocalToken { get; }

    /// <summary>Exposing Hostbay to the internet needs a longer password than the home network does.</summary>
    public const int MinInternetPasswordLength = 10;

    public bool HasPassword => !string.IsNullOrEmpty(_mgr.Settings.PasswordHash);

    public void SetPassword(string password, string? keepSession = null)
    {
        if (password.Length < 6) throw new ArgumentException("Use at least 6 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        _mgr.Settings.PasswordSalt = Convert.ToBase64String(salt);
        _mgr.Settings.PasswordHash = Convert.ToBase64String(Hash(password, salt));
        _mgr.Settings.LongPassword = password.Length >= MinInternetPasswordLength;
        if (!_mgr.Settings.LongPassword) _mgr.Settings.InternetAccess = false; // too short to face the internet
        _mgr.Save();
        // Sign out every other browser when the password changes, but keep whoever changed it.
        foreach (var id in _sessions.Keys) if (id != keepSession) _sessions.TryRemove(id, out _);
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);

    /// <summary>Checks the password, with a lockout after repeated failures from one address.</summary>
    public (bool Ok, string? Error) TryLogin(string password, IPAddress? ip)
    {
        var key = ip?.ToString() ?? "?";
        if (_failures.TryGetValue(key, out var f) && f.Count >= 5 && f.Until > DateTime.UtcNow)
            return (false, $"Too many attempts. Try again in {(int)(f.Until - DateTime.UtcNow).TotalSeconds + 1}s.");
        if (!HasPassword) return (false, "Remote access has no password set. Set one in Hostbay on the host PC.");

        var expected = Convert.FromBase64String(_mgr.Settings.PasswordHash);
        var actual = Hash(password, Convert.FromBase64String(_mgr.Settings.PasswordSalt));
        if (CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            _failures.TryRemove(key, out _);
            return (true, null);
        }
        var count = (f.Until > DateTime.UtcNow ? f.Count : 0) + 1;
        _failures[key] = (count, DateTime.UtcNow.AddMinutes(count >= 5 ? 1 : 10));
        return (false, "Wrong password.");
    }

    public bool CheckLocalToken(string? token) =>
        token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(LocalToken));

    public string CreateSession()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[id] = DateTime.UtcNow.AddDays(30);
        return id;
    }

    public bool IsValid(string? session) =>
        session != null && _sessions.TryGetValue(session, out var exp) && exp > DateTime.UtcNow;

    public void EndSession(string? session)
    {
        if (session != null) _sessions.TryRemove(session, out _);
    }
}
