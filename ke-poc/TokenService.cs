using System.Net.Http.Headers;
using System.Text.Json;

namespace KePoc;

/// Acquires and caches a client-credentials access token for the KE APIs.
public sealed class TokenService(HttpClient http, KeOptions options)
{
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        // Reuse until ~60s before expiry.
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-60))
            return _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["scope"] = options.Scope,
            }),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _token = root.GetProperty("access_token").GetString()
                 ?? throw new InvalidOperationException("Token response had no access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3600;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return _token;
    }
}
