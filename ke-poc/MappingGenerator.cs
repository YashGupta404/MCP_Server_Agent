using System.Text;
using System.Text.Json;

namespace KePoc;

/// Generates a ServiceNow-shaped businessObjectConfig from a KE Context result + the known ECM keyword
/// catalog + the LOB record context. This is the config-less mapping logic (layers 1-6).
public sealed class MappingGenerator
{
    public sealed record LobContext(string BusinessObjectType, string BusinessObjectId,
        string IdProp, string OwnerProp, string FileName);

    public (Dictionary<string, object?> Config, string Report) Generate(
        ContentsProcessingResults ke, EcmCatalog catalog, LobContext ctx)
    {
        var report = new StringBuilder();
        var result = ke.Results?.FirstOrDefault()
                     ?? throw new InvalidOperationException("KE result had no entries.");

        // --- Layer 1: classification -> doctype -----------------------------------------------------
        var classification = result.TextClassification?.Result ?? "";
        var doctype = BestDoctype(catalog, classification)
                      ?? throw new InvalidOperationException($"No catalog doctype matched classification '{classification}'.");
        report.AppendLine($"Layer 1 (doctype): classification \"{classification}\" -> {doctype.ContentTypeId}");

        // --- Layer 2: record linkage ----------------------------------------------------------------
        // Value-match: did KE extract an id equal to the busObject id we already have from context?
        var ids = result.NamedEntityText?.Result?.TryGetValue("ids_identifiers", out var v) == true ? v : null;
        var idMatched = ids?.Any(s => string.Equals(s?.Trim(), ctx.BusinessObjectId, StringComparison.OrdinalIgnoreCase)) == true;

        var recordKw = doctype.Keywords.FirstOrDefault(k => IsRecordId(k));
        var ownerKw = doctype.Keywords.FirstOrDefault(k => IsOwner(k));
        report.AppendLine($"Layer 2 (record linkage): recordId keyword = {recordKw?.Id ?? "(none)"} " +
                          $"[value-match {(idMatched ? "CONFIRMED" : "not found")} against NER ids_identifiers], " +
                          $"owner keyword = {ownerKw?.Id ?? "(none)"}");

        var contextParams = new Dictionary<string, object?>();
        if (recordKw is not null)
            contextParams["boContext"] = new Dictionary<string, object?>
            { ["boProp"] = ctx.IdProp, ["ecmProp"] = recordKw.Id, ["ecmPropId"] = recordKw.Id };
        if (ownerKw is not null)
            contextParams["boUser"] = new Dictionary<string, object?>
            { ["boProp"] = ctx.OwnerProp, ["ecmProp"] = ownerKw.Id, ["ecmPropId"] = ownerKw.Id };

        // --- Layer 3: import mappings + inputSource --------------------------------------------------
        // Content keywords = everything that isn't the record-id or owner (those are context-linked).
        var contentKws = doctype.Keywords.Where(k => k != recordKw && k != ownerKw).ToList();
        var importMappings = new List<object>();
        var labels = new List<object>();
        var displayColumns = new List<string>();

        foreach (var kw in contentKws)
        {
            string inputSource;
            string value;
            if (IsDocumentName(kw)) { inputSource = "4"; value = "File Name"; }   // filename
            else { inputSource = "6"; value = ""; }                               // user/IDP provided

            importMappings.Add(new Dictionary<string, object?>
            {
                ["ecmFieldName"] = kw.Id,
                ["inputSource"] = inputSource,
                ["value"] = new Dictionary<string, object?> { ["value"] = value, ["type"] = kw.DataType, ["format"] = "" },
            });
            labels.Add(new Dictionary<string, object?>
            {
                ["ecmColumnName"] = kw.Id,
                ["ecmColumnId"] = kw.Id,
                ["localeStrings"] = new[] { new Dictionary<string, object?>
                    { ["culture"] = "en_US", ["name"] = kw.Label, ["description"] = $"Default label for {kw.Id}" } },
            });
            displayColumns.Add(kw.Id);
            report.AppendLine($"Layer 3 (import): {kw.Id} -> inputSource {inputSource}" +
                              $"{(inputSource == "4" ? " (file name)" : " (user-provided)")}");
        }

        // --- Layers 4-6: labels / display columns / default query (mechanical) ----------------------
        var additionalEntry = new Dictionary<string, object?>
        {
            ["busObject"] = ctx.BusinessObjectType,
            ["ecmContentTypeName"] = doctype.ContentTypeId,
            ["ecmContentTypeId"] = doctype.ContentTypeId,
            ["ecmMetadataFieldLabels"] = labels,
            ["displayColumns"] = new Dictionary<string, object?>
            {
                ["ecmColumnSets"] = new[] { new Dictionary<string, object?>
                    { ["formFactorId"] = "desktop", ["columns"] = displayColumns } },
                ["defaultFormFactor"] = "desktop",
            },
            ["metadataFieldImportMappings"] = importMappings,
            ["defaultListQuery"] = new Dictionary<string, object?>
            { ["name"] = doctype.ContentTypeId, ["id"] = doctype.ContentTypeId, ["type"] = "ContentType", ["default"] = true },
        };

        var businessObjectConfig = new Dictionary<string, object?>
        {
            ["contextParams"] = contextParams,
            ["additionalConfig"] = new[] { additionalEntry },
        };

        return (businessObjectConfig, report.ToString());
    }

    private static EcmDoctype? BestDoctype(EcmCatalog catalog, string classification)
    {
        var want = Tokens(classification);
        return catalog.Doctypes
            .Select(d => (d, score: Score(Tokens(d.ContentTypeLabel + " " + d.ContentTypeId), want)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.d)
            .FirstOrDefault();
    }

    // Token overlap, lenient: exact token or prefix match (invoice ~ invoices) counts.
    private static int Score(HashSet<string> have, HashSet<string> want)
    {
        int score = 0;
        foreach (var w in want)
            foreach (var h in have)
                if (w == h || (w.Length >= 4 && (h.StartsWith(w) || w.StartsWith(h)))) { score++; break; }
        return score;
    }

    private static HashSet<string> Tokens(string s) =>
        new(s.ToLowerInvariant().Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries));

    private static bool Has(EcmKeyword k, params string[] needles)
    {
        var hay = (k.Id + " " + k.Label).ToLowerInvariant().Replace("_", "").Replace(" ", "");
        return needles.Any(n => hay.Contains(n));
    }

    // Prefer an explicit catalog role hint (needed for opaque OnBase numeric ids); else name heuristic.
    private static bool IsRecordId(EcmKeyword k) =>
        string.Equals(k.Role, "recordId", StringComparison.OrdinalIgnoreCase) || Has(k, "recordnumber", "businessobjectid", "boid", "sobjectid");
    private static bool IsOwner(EcmKeyword k) =>
        string.Equals(k.Role, "owner", StringComparison.OrdinalIgnoreCase) || Has(k, "recordowner", "openedby", "createdby") || k.Label.Equals("owner", StringComparison.OrdinalIgnoreCase);
    private static bool IsDocumentName(EcmKeyword k) =>
        string.Equals(k.Role, "documentName", StringComparison.OrdinalIgnoreCase) || Has(k, "documentname");

    public static string ToJson(object o) =>
        JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
}
