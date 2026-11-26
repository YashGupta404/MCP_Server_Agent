// Data Curation API (Knowledge Enrichment) client: extracts the full text of a document (OCR for images)
// as markdown. Contract from the public OpenAPI spec (<base>/openapi.json): POST /presign -> PUT put_url with
// put_headers -> poll GET /status/{job_id} -> download get_url (markdown.output holds the text).

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ---- Options (bound from the "DataCuration" config section; client id/secret in user-secrets) ----
public sealed class DataCurationOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TokenEndpoint { get; set; } = "https://auth.staging.app.hyland.com/idp/connect/token";
    public string Scope { get; set; } = "environment_authorization";
    public string BaseUrl { get; set; } = "https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/data-curation";
    public string JsonSchema { get; set; } = "FULL";
    public int TimeoutSeconds { get; set; } = 300;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed record DataCurationResult(
    string JobId, string Status, string? FileType, int? PageCount, string Markdown, string Text);

public sealed class DataCurationClient
{
    private static readonly string[] Terminal = { "Done", "Error", "Deleted" };
    private readonly HttpClient _http;
    private readonly DataCurationOptions _o;
    private string? _token;
    private DateTimeOffset _exp = DateTimeOffset.MinValue;

    public DataCurationClient(HttpClient http, DataCurationOptions o) { _http = http; _o = o; }

    private string Base => _o.BaseUrl.TrimEnd('/');

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _exp.AddSeconds(-60)) return _token;
        using var req = new HttpRequestMessage(HttpMethod.Post, _o.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _o.ClientId,
                ["client_secret"] = _o.ClientSecret,
                ["scope"] = _o.Scope,
            }),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Data Curation token failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString()!;
        var secs = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 900;
        _exp = DateTimeOffset.UtcNow.AddSeconds(secs);
        return _token;
    }

    private async Task<HttpRequestMessage> AuthedAsync(HttpMethod m, string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public async Task<DataCurationResult> ExtractAsync(byte[] bytes, CancellationToken ct = default)
    {
        // 1) presign. pii:false keeps names/ids we need for metadata; no chunking/embeddings (text only).
        var options = new Dictionary<string, object>
        {
            ["pii"] = false,
            ["chunking"] = false,
            ["embedding"] = false,
            ["json_schema"] = _o.JsonSchema,
        };
        using var pre = await AuthedAsync(HttpMethod.Post, $"{Base}/presign", ct);
        pre.Content = new StringContent(JsonSerializer.Serialize(options), Encoding.UTF8, "application/json");
        using var preResp = await _http.SendAsync(pre, ct);
        var preBody = await preResp.Content.ReadAsStringAsync(ct);
        if (!preResp.IsSuccessStatusCode) throw new InvalidOperationException($"presign failed ({(int)preResp.StatusCode}): {preBody}");
        using var preDoc = JsonDocument.Parse(preBody);
        var root = preDoc.RootElement;
        var jobId = root.GetProperty("job_id").GetString()!;
        var putUrl = root.GetProperty("put_url").GetString()!;
        var getUrl = root.GetProperty("get_url").GetString()!;

        // 2) PUT the bytes with the headers the service requires (no bearer to storage).
        var contentType = "application/octet-stream";
        var putHeaders = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty("put_headers", out var ph) && ph.ValueKind == JsonValueKind.Object)
            foreach (var h in ph.EnumerateObject())
            {
                var v = h.Value.GetString() ?? "";
                if (h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = v;
                else putHeaders.Add(new(h.Name, v));
            }
        // The storage gateway intermittently returns 502/503; retry transient 5xx a few times.
        for (var attempt = 1; ; attempt++)
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, putUrl) { Content = new ByteArrayContent(bytes) };
            foreach (var h in putHeaders) put.Headers.TryAddWithoutValidation(h.Key, h.Value);
            put.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            using var putResp = await _http.SendAsync(put, ct);
            if (putResp.IsSuccessStatusCode) break;
            if ((int)putResp.StatusCode < 500 || attempt >= 3)
                throw new InvalidOperationException($"upload failed ({(int)putResp.StatusCode}): {await putResp.Content.ReadAsStringAsync(ct)}");
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }

        // 3) poll status until a terminal state.
        string status = "";
        string? fileType = null, error = null;
        int? pageCount = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_o.TimeoutSeconds);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var st = await AuthedAsync(HttpMethod.Get, $"{Base}/status/{Uri.EscapeDataString(jobId)}", ct);
            using var stResp = await _http.SendAsync(st, ct);
            var stBody = await stResp.Content.ReadAsStringAsync(ct);
            if (!stResp.IsSuccessStatusCode) throw new InvalidOperationException($"status failed ({(int)stResp.StatusCode}): {stBody}");
            using var stDoc = JsonDocument.Parse(stBody);
            var s = stDoc.RootElement;
            status = s.TryGetProperty("status", out var sv) ? sv.GetString() ?? "" : "";
            if (Terminal.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                fileType = s.TryGetProperty("fileType", out var ft) && ft.ValueKind == JsonValueKind.String ? ft.GetString() : null;
                pageCount = s.TryGetProperty("page_count", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : null;
                error = s.TryGetProperty("errorMessage", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null;
                break;
            }
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Data Curation job {jobId} did not finish within {_o.TimeoutSeconds}s (last status '{status}').");
        }
        if (!status.Equals("Done", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Data Curation job {jobId} ended with status '{status}': {error}");

        // 4) download the result; markdown.output holds the full text.
        var result = await _http.GetStringAsync(getUrl, ct);
        using var resDoc = JsonDocument.Parse(result);
        var markdown = resDoc.RootElement.TryGetProperty("markdown", out var md)
                       && md.TryGetProperty("output", out var mo) && mo.ValueKind == JsonValueKind.String
            ? mo.GetString() ?? ""
            : "";
        return new DataCurationResult(jobId, status, fileType, pageCount, markdown, CleanMarkdown(markdown));
    }

    // Drops the <!-- LOC: ... --> position comments and image placeholders; keeps the text lines.
    public static string CleanMarkdown(string markdown)
    {
        var noLoc = Regex.Replace(markdown, @"<!--\s*LOC:.*?-->", "", RegexOptions.Singleline);
        var noImg = Regex.Replace(noLoc, @"!\[[^\]]*\]\([^)]*\)", "");
        var unescaped = Regex.Replace(noImg, @"\\([#()\[\]*_`>\-+.!])", "$1");
        return Regex.Replace(unescaped, @"\n{3,}", "\n\n").Trim();
    }
}
