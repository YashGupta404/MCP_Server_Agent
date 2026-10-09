using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Talks to the Hyland IDP classification REST API. Option C for the demo: the BFF reads a cached IDP
// bearer token from disk (minted by the local authorization_code login in bff/idp-feature-test.ps1).
static class IdpClient
{
    static string TokenPath(IdpOptions idp) =>
        string.IsNullOrWhiteSpace(idp.TokenFile) ? Path.Combine(Path.GetTempPath(), "idp_token.txt") : idp.TokenFile;

    // Reads the cached IDP token and returns it only if present and not expired (JWT exp claim).
    public static string? ReadToken(IdpOptions idp, ILogger log)
    {
        try
        {
            var path = TokenPath(idp);
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(token)) return null;
            var parts = token.Split('.');
            if (parts.Length >= 2)
            {
                var payloadJson = Encoding.UTF8.GetString(Base64UrlBytes(parts[1]));
                var payload = JsonNode.Parse(payloadJson);
                var exp = payload?["exp"]?.GetValue<long>();
                if (exp is long e && DateTimeOffset.FromUnixTimeSeconds(e) <= DateTimeOffset.UtcNow.AddSeconds(30))
                    return null; // expired (or about to)
            }
            return token;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[idp] could not read cached token");
            return null;
        }
    }

    static byte[] Base64UrlBytes(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    // Returns a valid IDP bearer token: the cached one if still valid, otherwise SILENTLY refreshes it
    // using the saved refresh token (no browser, no :5005). Returns null if neither works.
    public static async Task<string?> GetTokenAsync(IHttpClientFactory factory, IdpOptions idp, ILogger log, CancellationToken ct)
    {
        var token = ReadToken(idp, log);
        if (token is not null) return token;
        return await RefreshAsync(factory, idp, log, ct);
    }

    static async Task<string?> RefreshAsync(IHttpClientFactory factory, IdpOptions idp, ILogger log, CancellationToken ct)
    {
        try
        {
            var refreshPath = string.IsNullOrWhiteSpace(idp.RefreshTokenFile)
                ? Path.Combine(Path.GetTempPath(), "idp_refresh.txt") : idp.RefreshTokenFile;
            if (!File.Exists(refreshPath)) return null;
            var refresh = File.ReadAllText(refreshPath).Trim();
            if (string.IsNullOrWhiteSpace(refresh)) return null;
            if (string.IsNullOrWhiteSpace(idp.ClientId) || string.IsNullOrWhiteSpace(idp.ClientSecret))
            {
                log.LogWarning("[idp] cannot auto-refresh: Idp:ClientId / Idp:ClientSecret not configured");
                return null;
            }

            var http = factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(25);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = idp.ClientId,
                ["client_secret"] = idp.ClientSecret,
            });
            using var resp = await http.PostAsync(idp.TokenEndpoint, form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("[idp] auto-refresh failed ({Status})", (int)resp.StatusCode);
                return null;
            }
            var node = JsonNode.Parse(body);
            var access = node?["access_token"]?.ToString();
            if (string.IsNullOrWhiteSpace(access)) return null;
            File.WriteAllText(TokenPath(idp), access);
            var newRefresh = node?["refresh_token"]?.ToString();
            if (!string.IsNullOrWhiteSpace(newRefresh)) File.WriteAllText(refreshPath, newRefresh); // tokens rotate
            log.LogInformation("[idp] access token auto-refreshed silently via refresh_token");
            return access;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[idp] silent auto-refresh error");
            return null;
        }
    }

    // Classifies one document (fetched by IDP from sourceUrl) against the inline candidate classes.
    // Returns the winning className (== the doc type, 1:1), its confidence, and whether review is advised.
    public static async Task<(string? DocType, double Confidence, bool ReviewRequired, string? Error)> ClassifyAsync(
        IHttpClientFactory factory, IdpOptions idp, string token, string sourceUrl,
        IReadOnlyList<(string Name, string Description)> candidateClasses, ILogger log, CancellationToken ct)
    {
        var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        var baseUrl = idp.BaseUrl.TrimEnd('/');

        var classes = candidateClasses
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new
            {
                id = Guid.NewGuid().ToString(),
                name = c.Name,
                description = string.IsNullOrWhiteSpace(c.Description) ? $"A '{c.Name}' document." : c.Description,
                ignoreForAuto = false,
            }).ToArray();

        var body = new
        {
            correlationId = Guid.NewGuid().ToString(),
            configuration = new
            {
                executionProfile = new { profileId = idp.ClassificationProfileId, versionId = idp.ClassificationProfileVersion },
                documentClassDefinitions = classes,
                treatEachFileAsDocument = true,
                includeClassCandidateReasoning = true,
                reviewThreshold = 0.7,
                // Relaxed so a clearly-correct doc with a cryptic class name isn't rejected over a few points.
                classAssignmentThreshold = 0.6,
                classCandidatesMinDistance = 0.05,
                pageLimit = 10,
            },
            contentFileReferences = new[] { new { fileReference = Guid.NewGuid().ToString(), sourceUrl } },
        };

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? payload)
        {
            var msg = new HttpRequestMessage(method, url);
            msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            if (payload is not null)
                msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return await http.SendAsync(msg, ct);
        }

        string? jobId;
        using (var resp = await SendAsync(HttpMethod.Post, $"{baseUrl}/api/classification", body))
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (null, 0, false, $"classification submit failed ({(int)resp.StatusCode}): {text}");
            jobId = JsonNode.Parse(text)?["jobId"]?.GetValue<string>();
        }
        if (string.IsNullOrWhiteSpace(jobId))
            return (null, 0, false, "no jobId returned");

        // Poll status until the job leaves the processing states (or we give up).
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1500, ct);
            using var sResp = await SendAsync(HttpMethod.Get, $"{baseUrl}/api/classification/job/status?jobId={jobId}", null);
            var sText = await sResp.Content.ReadAsStringAsync(ct);
            var status = JsonNode.Parse(sText)?["jobStatus"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(status) &&
                status.IndexOf("Succeeded", StringComparison.OrdinalIgnoreCase) >= 0)
                break;
            if (!string.IsNullOrWhiteSpace(status) &&
                (status.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 status.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0))
                return (null, 0, false, $"classification job {status}");
        }

        using var rResp = await SendAsync(HttpMethod.Get, $"{baseUrl}/api/classification?jobId={jobId}", null);
        var rText = await rResp.Content.ReadAsStringAsync(ct);
        if (!rResp.IsSuccessStatusCode)
            return (null, 0, false, $"classification result failed ({(int)rResp.StatusCode}): {rText}");
        try
        {
            var doc = JsonNode.Parse(rText)?["documents"]?.AsArray()?.FirstOrDefault();
            var className = doc?["className"]?.GetValue<string>();
            var confidence = doc?["confidence"]?.GetValue<double>() ?? 0;
            var reviewStatus = doc?["reviewStatus"]?.GetValue<string>() ?? "";
            var reviewRequired = !reviewStatus.Equals("ReviewNotRequired", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(className) || className.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
                return (null, confidence, true, null);
            return (className, confidence, reviewRequired, null);
        }
        catch (Exception ex)
        {
            return (null, 0, false, $"could not parse result: {ex.Message}");
        }
    }

    public sealed record ExtractedField(string Id, string Name, string Value, double Confidence, bool ReviewRequired);

    // Recognizes (OCR) the document then extracts values for the given fields (recognition is a required
    // first pass; extraction reuses the SAME correlationId + fileReference). Returns the field values.
    public static async Task<(List<ExtractedField> Fields, string? Error)> ExtractAsync(
        IHttpClientFactory factory, IdpOptions idp, string token, string sourceUrl,
        IReadOnlyList<(string Id, string Name)> fields, ILogger log, CancellationToken ct)
    {
        var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(90);
        var baseUrl = idp.BaseUrl.TrimEnd('/');
        var correlationId = Guid.NewGuid().ToString();
        var fileReference = Guid.NewGuid().ToString();
        var classId = Guid.NewGuid().ToString();
        var empty = new List<ExtractedField>();

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? payload)
        {
            var msg = new HttpRequestMessage(method, url);
            msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            if (payload is not null)
                msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return await http.SendAsync(msg, ct);
        }

        // 1) Recognition (OCR). `actions` is a single enum string; 202 with an empty body.
        using (var rec = await SendAsync(HttpMethod.Post, $"{baseUrl}/api/recognition/file",
            new { correlationId, fileReference, sourceUrl, actions = "Ocr" }))
        {
            if (!rec.IsSuccessStatusCode)
                return (empty, $"recognition submit failed ({(int)rec.StatusCode})");
        }
        // Poll the recognition result by correlationId + fileReference (NOT a jobId).
        bool recognized = false;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1500, ct);
            using var rs = await SendAsync(HttpMethod.Get,
                $"{baseUrl}/api/recognition/file/metadata?correlationId={correlationId}&fileReference={fileReference}", null);
            var rsText = await rs.Content.ReadAsStringAsync(ct);
            var status = TryGetString(rsText, "status");
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)) { recognized = true; break; }
            if (!string.IsNullOrWhiteSpace(status) && status.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0)
                return (empty, "recognition failed");
        }
        if (!recognized) return (empty, "recognition timed out");

        // 2) Extraction reusing the SAME correlationId + fileReference. documents[].classId MUST equal the
        //    classExtractionDefinitions[].documentClassId, and treatEachFileAsDocument MUST be false.
        var fieldDefs = fields.Select(f => new { id = f.Id, name = f.Name, description = f.Name }).ToArray();
        var extractBody = new
        {
            correlationId,
            configuration = new
            {
                executionProfile = new
                {
                    profileId = idp.ClassificationProfileId,
                    versionId = idp.ClassificationProfileVersion,
                    recognitionProfile = new { profileId = idp.RecognitionProfileId, versionId = idp.RecognitionProfileVersion },
                },
                treatEachFileAsDocument = false,
                pageLimit = 10,
                classExtractionDefinitions = new[]
                {
                    new { documentClassId = classId, name = "Document", description = "Document", fieldDefinitions = fieldDefs }
                },
            },
            contentFileReferences = new[] { new { fileReference, sourceUrl } },
            documents = new[]
            {
                new { id = Guid.NewGuid().ToString(), classId, pages = new[] { new { contentFileReferenceIndex = 0, sourcePageIndex = 0, rotation = 0 } } }
            },
        };

        string? jobId;
        using (var ex = await SendAsync(HttpMethod.Post, $"{baseUrl}/api/extraction", extractBody))
        {
            var text = await ex.Content.ReadAsStringAsync(ct);
            if (!ex.IsSuccessStatusCode) return (empty, $"extraction submit failed ({(int)ex.StatusCode}): {text}");
            jobId = JsonNode.Parse(text)?["jobId"]?.GetValue<string>();
        }
        if (string.IsNullOrWhiteSpace(jobId)) return (empty, "no extraction jobId");

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(1500, ct);
            using var sResp = await SendAsync(HttpMethod.Get, $"{baseUrl}/api/extraction/job/status?jobId={jobId}", null);
            var sText = await sResp.Content.ReadAsStringAsync(ct);
            var status = TryGetString(sText, "jobStatus");
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)) break;
            if (!string.IsNullOrWhiteSpace(status) &&
                (status.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 status.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0))
                return (empty, $"extraction job {status}");
        }

        using var rr = await SendAsync(HttpMethod.Get, $"{baseUrl}/api/extraction?jobId={jobId}", null);
        var rrText = await rr.Content.ReadAsStringAsync(ct);
        if (!rr.IsSuccessStatusCode) return (empty, $"extraction result failed ({(int)rr.StatusCode})");
        try
        {
            var result = new List<ExtractedField>();
            var fieldsArr = JsonNode.Parse(rrText)?["documents"]?.AsArray()?.FirstOrDefault()?["fields"]?.AsArray();
            if (fieldsArr is not null)
                foreach (var fn in fieldsArr)
                {
                    var id = fn?["id"]?.ToString() ?? "";
                    var name = fn?["name"]?.ToString() ?? "";
                    var value = fn?["extractedValue"]?.ToString() ?? "";
                    double.TryParse(fn?["extractionConfidence"]?.ToString(), out var conf);
                    var rev = !((fn?["reviewStatus"]?.ToString() ?? "")
                        .Equals("ReviewNotRequired", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(value))
                        result.Add(new ExtractedField(id, name, value, conf, rev));
                }
            return (result, null);
        }
        catch (Exception exc)
        {
            return (empty, $"could not parse extraction: {exc.Message}");
        }
    }

    static string? TryGetString(string json, string prop)
    {
        try { return JsonNode.Parse(json)?[prop]?.ToString(); } catch { return null; }
    }
}
