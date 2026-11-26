using System.Text.Json;
using KePoc;
using Microsoft.Extensions.Configuration;

// Usage:
//   dotnet run -- <document> [contentType]
//   dotnet run -- generate <contextJson> <catalogJson> <busType> <busId> [owner] [fileName]
//   dotnet run -- dc <document>   (Data Curation text via the BFF's DataCurationClient + BFF user-secrets)
if (args.Length > 0 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
    return RunGenerate(args);
if (args.Length > 1 && args[0].Equals("dc", StringComparison.OrdinalIgnoreCase))
    return await RunDataCuration(args[1]);

// Load config: appsettings (optional) -> user-secrets -> environment.
var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = config.GetSection("Ke").Get<KeOptions>() ?? new KeOptions();

var missing = options.Missing();
if (missing.Length > 0)
{
    Console.Error.WriteLine("Missing required secrets: " + string.Join(", ", missing));
    Console.Error.WriteLine("Set them with:");
    Console.Error.WriteLine("  dotnet user-secrets set \"Ke:ClientId\" \"<id>\"");
    Console.Error.WriteLine("  dotnet user-secrets set \"Ke:ClientSecret\" \"<secret>\"");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var tokens = new TokenService(http, options);

// No file arg -> Step 1 only: just prove the token works.
if (args.Length == 0)
{
    Console.WriteLine("No document provided; verifying token only (pass a file path to run Context enrichment).");
    Console.WriteLine($"Token endpoint : {options.TokenEndpoint}");
    Console.WriteLine($"Client id      : {Mask(options.ClientId)}");
    try
    {
        var t = await tokens.GetTokenAsync();
        Console.WriteLine($"OK - access_token acquired (length {t.Length}).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FAILED: " + ex.Message);
        return 2;
    }
}

// Step 2/3: run the full Context enrichment pipeline on one document.
var filePath = args[0];
if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"File not found: {filePath}");
    return 1;
}
var contentType = args.Length > 1 ? args[1] : ContextClient.GuessContentType(filePath);
var bytes = await File.ReadAllBytesAsync(filePath);

var ke = new ContextClient(http, tokens, options);
Console.WriteLine($"File        : {filePath} ({bytes.Length} bytes, {contentType})");
Console.WriteLine($"Context URL : {options.ContextBaseUrl}");

try
{
    Console.WriteLine("1/4 presigned upload URL...");
    var presign = await ke.GetUploadUrlAsync(contentType);
    if (string.IsNullOrEmpty(presign.PresignedUrl) || string.IsNullOrEmpty(presign.ObjectKey))
        throw new InvalidOperationException("presign response missing presignedUrl/objectKey.");

    Console.WriteLine("2/4 uploading bytes...");
    await ke.UploadAsync(presign.PresignedUrl!, contentType, bytes);

    Console.WriteLine("3/4 submitting /content/process (classify + NER + summarize)...");
    var processingId = await ke.ProcessAsync(presign.ObjectKey!, options.ContextClasses, options.SummaryMaxWords);
    Console.WriteLine($"    processingId = {processingId}");

    Console.WriteLine("4/4 polling results...");
    var results = await ke.PollResultsAsync(processingId);

    var first = results.Results?.FirstOrDefault();
    Console.WriteLine();
    Console.WriteLine("===== Context enrichment result =====");
    Console.WriteLine($"Classification : {first?.TextClassification?.Result ?? "(none)"}");
    Console.WriteLine($"Summary        : {first?.TextSummary?.Result ?? "(none)"}");
    Console.WriteLine("Named entities :");
    if (first?.NamedEntityText?.Result is { Count: > 0 } ner)
        foreach (var (type, values) in ner)
            Console.WriteLine($"  - {type}: {string.Join(", ", values ?? new List<string>())}");
    else
        Console.WriteLine("  (none)");

    // Full JSON for building the field->keyword mapping in Step 4.
    var outPath = Path.ChangeExtension(filePath, ".context.json");
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nFull result written to {outPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAILED: " + ex.Message);
    return 2;
}

static string Mask(string s) =>
    string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= 6 ? "****" : s[..3] + "****" + s[^3..];

static async Task<int> RunDataCuration(string filePath)
{
    if (!File.Exists(filePath)) { Console.Error.WriteLine($"File not found: {filePath}"); return 1; }
    var cfg = new ConfigurationBuilder().AddUserSecrets("uceb-bff-9f2c1a7e").Build();
    var o = cfg.GetSection("DataCuration").Get<DataCurationOptions>() ?? new DataCurationOptions();
    if (!o.IsConfigured) { Console.Error.WriteLine("DataCuration:ClientId/ClientSecret missing in BFF user-secrets."); return 1; }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds + 30) };
    try
    {
        var r = await new DataCurationClient(http, o).ExtractAsync(await File.ReadAllBytesAsync(filePath));
        Console.WriteLine($"job={r.JobId} status={r.Status} fileType={r.FileType} pages={r.PageCount}");
        Console.WriteLine("===== text =====");
        Console.WriteLine(r.Text);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FAILED: " + ex.Message);
        return 2;
    }
}

// Step 4/5: generate a solution-config fragment from a KE result + the known ECM keyword catalog,
// then diff it against the hand-written servicenow-solution-config.json.
static int RunGenerate(string[] args)
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("Usage: generate <contextJson> <catalogJson> <busType> <busId> [owner] [fileName]");
        return 1;
    }
    var contextJson = args[1];
    var catalogJson = args[2];
    var busType = args[3];
    var busId = args[4];
    var owner = args.Length > 5 ? args[5] : "";
    var fileName = args.Length > 6 ? args[6] : "";
    var realArg = args.Length > 7 ? args[7] : null;
    var realType = args.Length > 8 ? args[8] : null;

    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var ke = JsonSerializer.Deserialize<ContentsProcessingResults>(File.ReadAllText(contextJson), jsonOpts)
             ?? throw new InvalidOperationException("Could not read KE context result.");
    var catalog = JsonSerializer.Deserialize<EcmCatalog>(File.ReadAllText(catalogJson), jsonOpts)
                  ?? throw new InvalidOperationException("Could not read ECM catalog.");

    var ctx = new MappingGenerator.LobContext(busType, busId, IdProp: "number", OwnerProp: "opened_by", FileName: fileName);
    var (boc, report) = new MappingGenerator().Generate(ke, catalog, ctx);

    Console.WriteLine("===== Generation report =====");
    Console.Write(report);

    var outPath = Path.Combine(Path.GetDirectoryName(catalogJson) ?? ".", $"generated-{busType}-config.json");
    var generated = MappingGenerator.ToJson(boc);
    File.WriteAllText(outPath, generated);
    Console.WriteLine($"\nGenerated businessObjectConfig written to {outPath}");

    // Diff vs the hand-written config (default: ServiceNow; override via args 7/8 for other LOBs).
    var realPath = realArg is not null
        ? Path.GetFullPath(realArg)
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "servicenow-solution-config.json"));
    Console.WriteLine($"\n===== Diff vs {Path.GetFileName(realPath)} =====");
    if (!File.Exists(realPath)) { Console.WriteLine($"(real config not found at {realPath})"); return 0; }

    using var genDoc = JsonDocument.Parse(generated);
    using var realDoc = JsonDocument.Parse(File.ReadAllText(realPath));
    var realRoot = realDoc.RootElement;
    var realBoc = realRoot.TryGetProperty("configurations", out var cfg)
        ? cfg.GetProperty("businessObjectConfig")
        : realRoot.GetProperty("data").GetProperty("configurations").GetProperty("businessObjectConfig");

    int ok = 0, tot = 0;
    void Check(string label, string? g, string? r)
    {
        tot++; var m = string.Equals(g, r, StringComparison.OrdinalIgnoreCase); if (m) ok++;
        Console.WriteLine($"  [{(m ? "MATCH" : "DIFF")}] {label}: gen={g ?? "-"} | real={r ?? "-"}");
    }
    // For context params, match the generated keyword id against real ecmProp OR ecmPropId (OnBase numeric).
    void CheckCtx(string label, string? g, string? rProp, string? rPropId)
    {
        tot++; var m = string.Equals(g, rProp, StringComparison.OrdinalIgnoreCase) || string.Equals(g, rPropId, StringComparison.OrdinalIgnoreCase);
        if (m) ok++;
        Console.WriteLine($"  [{(m ? "MATCH" : "DIFF")}] {label}: gen={g ?? "-"} | real.ecmProp={rProp ?? "-"} / ecmPropId={rPropId ?? "-"}");
    }

    CheckCtx("boContext record keyword",
        Str(genDoc.RootElement, "contextParams", "boContext", "ecmProp"),
        Str(realBoc, "contextParams", "boContext", "ecmProp"),
        Str(realBoc, "contextParams", "boContext", "ecmPropId"));
    CheckCtx("boUser owner keyword",
        Str(genDoc.RootElement, "contextParams", "boUser", "ecmProp"),
        Str(realBoc, "contextParams", "boUser", "ecmProp"),
        Str(realBoc, "contextParams", "boUser", "ecmPropId"));

    var genEntry = FirstAdditional(genDoc.RootElement);
    var realEntry = realType is not null ? FindAdditional(realBoc, realType) : FirstAdditional(realBoc);
    Check("ecmContentTypeName",
        genEntry?.GetProperty("ecmContentTypeName").GetString(),
        realEntry?.GetProperty("ecmContentTypeName").GetString());

    var gMap = ImportMap(genEntry);
    var rMap = ImportMap(realEntry);
    foreach (var field in gMap.Keys.Union(rMap.Keys).OrderBy(x => x))
        Check($"import[{field}].inputSource", gMap.GetValueOrDefault(field), rMap.GetValueOrDefault(field));

    Console.WriteLine($"\nResult: {ok}/{tot} fields match the hand-written config.");
    return 0;
}

static string? Str(JsonElement e, params string[] path)
{
    foreach (var p in path)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
        e = next;
    }
    return e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
}

static JsonElement? FirstAdditional(JsonElement boc) =>
    boc.TryGetProperty("additionalConfig", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
        ? arr[0] : null;

// Find the additionalConfig entry for a given ecmContentTypeName.
static JsonElement? FindAdditional(JsonElement boc, string contentTypeName)
{
    if (boc.TryGetProperty("additionalConfig", out var arr) && arr.ValueKind == JsonValueKind.Array)
        foreach (var e in arr.EnumerateArray())
            if (e.TryGetProperty("ecmContentTypeName", out var n) &&
                string.Equals(n.GetString(), contentTypeName, StringComparison.OrdinalIgnoreCase))
                return e;
    return null;
}

static Dictionary<string, string> ImportMap(JsonElement? entry)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (entry is { } e && e.TryGetProperty("metadataFieldImportMappings", out var arr) && arr.ValueKind == JsonValueKind.Array)
        foreach (var m in arr.EnumerateArray())
        {
            var f = m.TryGetProperty("ecmFieldName", out var fn) ? fn.GetString() : null;
            var s = m.TryGetProperty("inputSource", out var isrc) ? isrc.GetString() : null;
            if (f is not null) map[f] = s ?? "";
        }
    return map;
}
