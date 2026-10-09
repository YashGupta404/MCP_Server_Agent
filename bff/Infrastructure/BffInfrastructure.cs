using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

// PKCE helpers + in-memory stores used by the BFF: user session tokens and the Workday access-token
// cache. Moved out of Program.cs; behavior is unchanged.

static class Pkce
{
    public static string RandomToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();

    public UserSession Save(string id, TokenResponse token)
    {
        var session = new UserSession
        {
            AccessToken = token.access_token,
            RefreshToken = token.refresh_token,
            // 60s safety buffer so we refresh slightly early.
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.expires_in - 60)),
        };
        _sessions[id] = session;
        return session;
    }

    public bool TryGet(string id, out UserSession? session) => _sessions.TryGetValue(id, out session);
    public void Remove(string id) => _sessions.TryRemove(id, out _);
}

sealed class UserSession
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

// Small thread-safe cache for the Workday access token so we don't re-auth on every lookup.
sealed class WorkdayTokenCache
{
    private readonly object _lock = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public bool TryGet(out string? token)
    {
        lock (_lock)
        {
            token = _token;
            return _token is not null && DateTimeOffset.UtcNow < _expiresAt;
        }
    }

    public void Set(string token, int expiresInSeconds)
    {
        lock (_lock)
        {
            _token = token;
            // 60s safety buffer; never cache for less than 30s.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
        }
    }
}
