// KE-driven auto-config (PoC): OCR/classify/NER via the KE Context API, then generate a solution-config
// mapping fragment from the known ECM keyword catalog. Ported from the ke-poc console app. Global
// namespace to match the BFF's existing DTO style.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---- Options (bound from the "Ke" config section) ----
public sealed class KeContextOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TokenEndpoint { get; set; } = "https://auth.staging.app.hyland.com/idp/connect/token";
    public string Scope { get; set; } = "environment_authorization";
    public string ContextBaseUrl { get; set; } = "https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/context-enrichment";
    public string[] ContextClasses { get; set; } =
        { "change request", "incident", "invoice", "purchase order", "contract", "resume", "other" };
    public int SummaryMaxWords { get; set; } = 50;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

// ---- Context API DTOs (from the verified Staging swagger-context-api.yaml) ----
public sealed class KePresignedUrlResult
{
    [JsonPropertyName("presignedUrl")] public string? PresignedUrl { get; set; }
    [JsonPropertyName("objectKey")] public string? ObjectKey { get; set; }
}
public sealed class KeProcessRequest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "context.api/v2";
    [JsonPropertyName("objectKeys")] public List<KeObjectKeyRef> ObjectKeys { get; set; } = new();
    [JsonPropertyName("actions")] public KeProcessActions Actions { get; set; } = new();
}
public sealed class KeObjectKeyRef { [JsonPropertyName("path")] public string Path { get; set; } = ""; }
public sealed class KeProcessActions
{
    [JsonPropertyName("textClassification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeTextClassificationAction? TextClassification { get; set; }
    [JsonPropertyName("namedEntityRecognitionText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? NamedEntityRecognitionText { get; set; }
    [JsonPropertyName("textSummarization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeTextSummarizationAction? TextSummarization { get; set; }
}
public sealed class KeTextClassificationAction
{
    [JsonPropertyName("classes")] public string[] Classes { get; set; } = Array.Empty<string>();
    [JsonPropertyName("instructions")] public object Instructions { get; set; } = new { };
}
public sealed class KeTextSummarizationAction { [JsonPropertyName("maxWordCount")] public int MaxWordCount { get; set; } = 50; }
public sealed class KeProcessAccepted { [JsonPropertyName("processingId")] public string? ProcessingId { get; set; } }
public sealed class KeContentsProcessingResults
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("results")] public List<KeS3ContentProcessingResult>? Results { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("inProgress")] public bool InProgress { get; set; }
}
public sealed class KeS3ContentProcessingResult
{
    [JsonPropertyName("objectKey")] public string? ObjectKey { get; set; }
    [JsonPropertyName("textSummary")] public KeStringResult? TextSummary { get; set; }
    [JsonPropertyName("textClassification")] public KeStringResult? TextClassification { get; set; }
    [JsonPropertyName("namedEntityText")] public KeNerResult? NamedEntityText { get; set; }
}
public sealed class KeStringResult
{
    [JsonPropertyName("isSuccess")] public bool IsSuccess { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
}
public sealed class KeNerResult
{
    [JsonPropertyName("isSuccess")] public bool IsSuccess { get; set; }
    [JsonPropertyName("result")] public Dictionary<string, List<string>?>? Result { get; set; }
}

// ---- Known ECM keyword catalog ----
public sealed class EcmCatalog { [JsonPropertyName("doctypes")] public List<EcmDoctype> Doctypes { get; set; } = new(); }
public sealed class EcmDoctype
{
    [JsonPropertyName("contentTypeId")] public string ContentTypeId { get; set; } = "";
    [JsonPropertyName("contentTypeName")] public string ContentTypeName { get; set; } = "";
    [JsonPropertyName("contentTypeLabel")] public string ContentTypeLabel { get; set; } = "";
    [JsonPropertyName("keywords")] public List<EcmKeyword> Keywords { get; set; } = new();
}
public sealed class EcmKeyword
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("dataType")] public string DataType { get; set; } = "String";
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; }
}

// ---- KE Context API client (presign -> PUT -> process -> poll) ----
public sealed class KeContextClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly HttpClient _http;
    private readonly KeContextOptions _o;
    private string? _token;
    private DateTimeOffset _exp = DateTimeOffset.MinValue;

    public KeContextClient(HttpClient http, KeContextOptions o) { _http = http; _o = o; }

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
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"KE token failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString()!;
        var secs = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
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

    private string Base => _o.ContextBaseUrl.TrimEnd('/');

    public async Task<KeContentsProcessingResults> EnrichAsync(byte[] bytes, string contentType, string[] classes, int summaryWords, CancellationToken ct = default)
    {
        // 1) presign
        using var p = await AuthedAsync(HttpMethod.Get, $"{Base}/files/upload/presigned-url?contentType={Uri.EscapeDataString(contentType)}", ct);
        using var pr = await _http.SendAsync(p, ct);
        var pb = await pr.Content.ReadAsStringAsync(ct);
        if (!pr.IsSuccessStatusCode) throw new InvalidOperationException($"presign failed ({(int)pr.StatusCode}): {pb}");
        var presign = JsonSerializer.Deserialize<KePresignedUrlResult>(pb, Json)!;
        // 2) PUT bytes (no bearer to storage)
        using var put = new HttpRequestMessage(HttpMethod.Put, presign.PresignedUrl) { Content = new ByteArrayContent(bytes) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var putResp = await _http.SendAsync(put, ct);
        if (!putResp.IsSuccessStatusCode) throw new InvalidOperationException($"upload failed ({(int)putResp.StatusCode})");
        // 3) process
        var request = new KeProcessRequest
        {
            ObjectKeys = { new KeObjectKeyRef { Path = presign.ObjectKey! } },
            Actions = new KeProcessActions
            {
                TextClassification = classes.Length > 0 ? new KeTextClassificationAction { Classes = classes } : null,
                NamedEntityRecognitionText = new { },
                TextSummarization = new KeTextSummarizationAction { MaxWordCount = summaryWords },
            },
        };
        using var proc = await AuthedAsync(HttpMethod.Post, $"{Base}/content/process", ct);
        proc.Content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
        using var procResp = await _http.SendAsync(proc, ct);
        var procBody = await procResp.Content.ReadAsStringAsync(ct);
        if (!procResp.IsSuccessStatusCode) throw new InvalidOperationException($"/content/process failed ({(int)procResp.StatusCode}): {procBody}");
        var pid = JsonSerializer.Deserialize<KeProcessAccepted>(procBody, Json)!.ProcessingId!;
        // 4) poll
        var url = $"{Base}/content/process/{Uri.EscapeDataString(pid)}/results";
        var delay = TimeSpan.FromSeconds(1); var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (true)
        {
            using var g = await AuthedAsync(HttpMethod.Get, url, ct);
            using var gr = await _http.SendAsync(g, ct);
            if (gr.StatusCode == HttpStatusCode.OK)
                return JsonSerializer.Deserialize<KeContentsProcessingResults>(await gr.Content.ReadAsStringAsync(ct), Json)!;
            if (gr.StatusCode != HttpStatusCode.Accepted)
                throw new InvalidOperationException($"results poll failed ({(int)gr.StatusCode})");
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("KE processing did not finish within 120s.");
            await Task.Delay(delay, ct);
            delay = delay < TimeSpan.FromSeconds(5) ? delay + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        }
    }

    public static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    // Parse the live UCEB list_document_types?fetchMetadata=true response into an EcmCatalog.
    // Shape: { data: [ { DocumentTypeName, DocumentTypeId, AttributeTypes: [ { id, name, dataType } ] } ] }.
    public static EcmCatalog ParseCatalog(string mcpJson)
    {
        var catalog = new EcmCatalog();
        using var doc = JsonDocument.Parse(mcpJson);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
        if (data.ValueKind != JsonValueKind.Array) return catalog;
        foreach (var dt in data.EnumerateArray())
        {
            var name = Read(dt, "DocumentTypeName");
            var id = Read(dt, "DocumentTypeId");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var doctype = new EcmDoctype { ContentTypeId = string.IsNullOrWhiteSpace(id) ? name! : id!, ContentTypeName = name!, ContentTypeLabel = name! };
            if (dt.TryGetProperty("AttributeTypes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                foreach (var a in attrs.EnumerateArray())
                {
                    var aid = Read(a, "id");
                    var aname = Read(a, "name");
                    var adt = Read(a, "dataType");
                    if (string.IsNullOrWhiteSpace(aid)) continue;
                    var required = a.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True;
                    doctype.Keywords.Add(new EcmKeyword { Id = aid!, Label = string.IsNullOrWhiteSpace(aname) ? aid! : aname!, DataType = string.IsNullOrWhiteSpace(adt) ? "String" : adt!, Required = required });
                }
            catalog.Doctypes.Add(doctype);
        }
        return catalog;
    }

    private static string? Read(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null)
            : null;
}

// ---- Mapping generator (config-less layers 1-6) ----
public sealed class KeMappingGenerator
{
    public sealed record LobContext(string BusinessObjectType, string BusinessObjectId, string IdProp, string OwnerProp, string FileName);

    public (Dictionary<string, object?> Config, string Report, object Mappings) Generate(KeContentsProcessingResults ke, EcmCatalog catalog, LobContext ctx)
    {
        var report = new StringBuilder();
        var result = ke.Results?.FirstOrDefault() ?? throw new InvalidOperationException("KE result had no entries.");
        var ner = result.NamedEntityText?.Result;

        var classification = result.TextClassification?.Result ?? "";
        var doctype = BestDoctype(catalog, classification)
                      ?? throw new InvalidOperationException($"No catalog doctype matched classification '{classification}'.");
        report.AppendLine($"Layer 1 (doctype): classification \"{classification}\" -> {doctype.ContentTypeId}");

        var ids = result.NamedEntityText?.Result?.TryGetValue("ids_identifiers", out var v) == true ? v : null;
        var idMatched = ids?.Any(s => string.Equals(s?.Trim(), ctx.BusinessObjectId, StringComparison.OrdinalIgnoreCase)) == true;
        var recordKw = doctype.Keywords.FirstOrDefault(IsRecordId);
        var ownerKw = doctype.Keywords.FirstOrDefault(IsOwner);
        report.AppendLine($"Layer 2 (record linkage): recordId={recordKw?.Id ?? "(none)"} [value-match {(idMatched ? "CONFIRMED" : "not found")}], owner={ownerKw?.Id ?? "(none)"}");

        var contextParams = new Dictionary<string, object?>();
        if (recordKw is not null) contextParams["boContext"] = new Dictionary<string, object?> { ["boProp"] = ctx.IdProp, ["ecmProp"] = recordKw.Id, ["ecmPropId"] = recordKw.Id };
        if (ownerKw is not null) contextParams["boUser"] = new Dictionary<string, object?> { ["boProp"] = ctx.OwnerProp, ["ecmProp"] = ownerKw.Id, ["ecmPropId"] = ownerKw.Id };

        var contentKws = doctype.Keywords.Where(k => k != recordKw && k != ownerKw).ToList();
        var importMappings = new List<object>();
        var labels = new List<object>();
        var displayColumns = new List<string>();
        foreach (var kw in contentKws)
        {
            string inputSource; string value; string src;
            if (IsDocumentName(kw)) { inputSource = "4"; value = "File Name"; src = "file name"; }
            else
            {
                inputSource = "6";
                var (ext, s) = SuggestValue(kw, ner);  // KE-extracted value for this keyword, if any
                value = ext ?? "";
                src = s;
            }
            importMappings.Add(new Dictionary<string, object?>
            {
                ["ecmFieldName"] = kw.Id,
                ["inputSource"] = inputSource,
                ["value"] = new Dictionary<string, object?> { ["value"] = value, ["type"] = kw.DataType, ["format"] = "" },
            });
            labels.Add(new Dictionary<string, object?>
            {
                ["ecmColumnName"] = kw.Id, ["ecmColumnId"] = kw.Id,
                ["localeStrings"] = new[] { new Dictionary<string, object?> { ["culture"] = "en_US", ["name"] = kw.Label, ["description"] = $"Default label for {kw.Id}" } },
            });
            displayColumns.Add(kw.Id);
            report.AppendLine($"Layer 3 (import): {kw.Id} -> inputSource {inputSource} ({src}){(string.IsNullOrEmpty(value) || inputSource == "4" ? "" : $" = '{value}'")}");
        }

        var additionalEntry = new Dictionary<string, object?>
        {
            ["busObject"] = ctx.BusinessObjectType,
            ["ecmContentTypeName"] = doctype.ContentTypeName,
            ["ecmContentTypeId"] = doctype.ContentTypeId,
            ["ecmMetadataFieldLabels"] = labels,
            ["displayColumns"] = new Dictionary<string, object?>
            {
                ["ecmColumnSets"] = new[] { new Dictionary<string, object?> { ["formFactorId"] = "desktop", ["columns"] = displayColumns } },
                ["defaultFormFactor"] = "desktop",
            },
            ["metadataFieldImportMappings"] = importMappings,
            ["defaultListQuery"] = new Dictionary<string, object?> { ["name"] = doctype.ContentTypeId, ["id"] = doctype.ContentTypeId, ["type"] = "ContentType", ["default"] = true },
        };

        var boc = new Dictionary<string, object?> { ["contextParams"] = contextParams, ["additionalConfig"] = new[] { additionalEntry } };

        // A clean summary of every kind of mapping (for a mapping-only view).
        var mappings = new Dictionary<string, object?>
        {
            ["doctypeMapping"] = new Dictionary<string, object?>
            {
                ["busObject"] = ctx.BusinessObjectType,
                ["ecmContentTypeName"] = doctype.ContentTypeName,
                ["ecmContentTypeId"] = doctype.ContentTypeId,
                ["fromClassification"] = classification,
            },
            ["recordLinkageMapping"] = new Dictionary<string, object?>
            {
                ["boContext"] = recordKw is null ? null : new Dictionary<string, object?>
                    { ["lobField"] = ctx.IdProp, ["ecmKeyword"] = recordKw.Id, ["label"] = recordKw.Label, ["valueMatch"] = idMatched ? "confirmed" : "heuristic" },
                ["boUser"] = ownerKw is null ? null : new Dictionary<string, object?>
                    { ["lobField"] = ctx.OwnerProp, ["ecmKeyword"] = ownerKw.Id, ["label"] = ownerKw.Label },
            },
            ["fieldImportMappings"] = contentKws.Select(kw =>
            {
                if (IsDocumentName(kw))
                    return new Dictionary<string, object?> { ["ecmKeyword"] = kw.Id, ["label"] = kw.Label, ["inputSource"] = "4", ["source"] = "file name", ["keExtractedValue"] = (string?)null };
                var (ext, _) = SuggestValue(kw, ner);
                return new Dictionary<string, object?>
                {
                    ["ecmKeyword"] = kw.Id,
                    ["label"] = kw.Label,
                    ["inputSource"] = "6",
                    ["source"] = ext is not null ? "KE-extracted" : "user/IDP-provided",
                    ["keExtractedValue"] = ext,
                };
            }).ToList(),
            ["displayColumnMapping"] = displayColumns,
            ["queryMapping"] = new Dictionary<string, object?>
                { ["name"] = doctype.ContentTypeName, ["id"] = doctype.ContentTypeId, ["type"] = "ContentType" },
        };

        return (boc, report.ToString(), mappings);
    }

    private static EcmDoctype? BestDoctype(EcmCatalog catalog, string classification)
    {
        var exact = catalog.Doctypes.FirstOrDefault(d =>
            string.Equals(d.ContentTypeName, classification, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.ContentTypeLabel, classification, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var want = Tokens(classification);
        return catalog.Doctypes
            .Select(d => (d, score: Score(Tokens(d.ContentTypeLabel + " " + d.ContentTypeName + " " + d.ContentTypeId), want)))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.d).FirstOrDefault();
    }
    private static int Score(HashSet<string> have, HashSet<string> want)
    {
        int score = 0;
        foreach (var w in want) foreach (var h in have) if (w == h || (w.Length >= 4 && (h.StartsWith(w) || w.StartsWith(h)))) { score++; break; }
        return score;
    }
    private static HashSet<string> Tokens(string s) => new(s.ToLowerInvariant().Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries));
    private static bool Has(EcmKeyword k, params string[] needles)
    {
        var hay = (k.Id + " " + k.Label).ToLowerInvariant().Replace("_", "").Replace(" ", "");
        return needles.Any(n => hay.Contains(n));
    }
    private static bool IsRecordId(EcmKeyword k) => string.Equals(k.Role, "recordId", StringComparison.OrdinalIgnoreCase) || Has(k, "recordnumber", "businessobjectid", "boid", "sobjectid");
    private static bool IsOwner(EcmKeyword k) => string.Equals(k.Role, "owner", StringComparison.OrdinalIgnoreCase) || Has(k, "sobjectowner", "recordowner", "openedby", "createdby");
    private static bool IsDocumentName(EcmKeyword k) => string.Equals(k.Role, "documentName", StringComparison.OrdinalIgnoreCase) || Has(k, "documentname");

    // IDP-style value fill: match a keyword to one of KE's extracted NER values by the keyword's label.
    // Heuristic (demo quality): dates->dates_times, total/amount->largest monetary, number/#->ids,
    // vendor/company/name->organizations, currency->3-letter money token.
    private static (string? value, string source) SuggestValue(EcmKeyword kw, Dictionary<string, List<string>?>? ner)
    {
        if (ner is null || ner.Count == 0) return (null, "user/IDP-provided");
        var label = (kw.Id + " " + kw.Label).ToLowerInvariant();
        List<string>? G(string k) => ner.TryGetValue(k, out var v) ? v : null;

        if (label.Contains("date"))
        { var d = G("dates_times"); if (d is { Count: > 0 }) return (d[0], "KE-extracted"); }

        if (label.Contains("total") || label.Contains("amount") || label.Contains("value"))
        {
            var m = G("monetary_values");
            if (m is { Count: > 0 })
            {
                string? best = null; decimal max = decimal.MinValue;
                foreach (var x in m) { var n = ParseMoney(x); if (n.HasValue && n.Value > max) { max = n.Value; best = x; } }
                if (best is not null) return (best, "KE-extracted");
            }
        }
        if (label.Contains("invoice") || label.Contains("number") || label.Contains("#"))
        { var id = G("ids_identifiers"); if (id is { Count: > 0 }) return (id[0], "KE-extracted"); }

        if (label.Contains("vendor") || label.Contains("company") || label.Contains("organization") || label.Contains("name"))
        { var o = G("organizations"); if (o is { Count: > 0 }) return (o[0], "KE-extracted"); }

        if (label.Contains("currency"))
        {
            var m = G("monetary_values");
            var cur = m?.SelectMany(x => x.Split(' ')).FirstOrDefault(t => t.Length == 3 && t.All(char.IsLetter));
            if (cur is not null) return (cur.ToUpperInvariant(), "KE-extracted");
        }
        return (null, "user/IDP-provided");
    }

    private static decimal? ParseMoney(string s)
    {
        var cleaned = new string(s.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(cleaned, out var n) ? n : (decimal?)null;
    }
}
