using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Deterministic first-pass suggestions for the profile wizard: for every (system, business object) pair it picks
// document types, a population source per keyword (lob | document | manual) and a view query, each with a confidence.
// Pairs the admin already configured are kept untouched.
static class ProfileSuggester
{
    public sealed record ObjectScore(string System, string BusinessObject, int Import, int View, string ImportBasis, string ViewBasis);

    public sealed record Result(Dictionary<string, SystemSolution> Solutions, List<ObjectScore> Scores, int ImportConfidence, int ViewConfidence);

    private const int MaxDoctypesPerObject = 6;
    private static readonly Regex IdLike = new(@"(#|\bno\.?\b|\bnumber\b|\bnum\b|\bid\b)", RegexOptions.IgnoreCase);
    private static readonly Regex GenericRecordKeyword = new(@"(sobject[_ ]?id|record[_ ]?(number|id)|business[_ ]?object[_ ]?id|recordnumber)", RegexOptions.IgnoreCase);
    private static readonly Regex LobProperty = new(@"\b(owner|assigned to|assignment group|short description|requested by|opened by|caller|state|priority|status)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ManualProperty = new(@"\b(note|notes|comment|comments|remark|remarks|reviewer)\b", RegexOptions.IgnoreCase);

    public static Result Suggest(
        IReadOnlyList<BusinessObjectRef> objects, IReadOnlyList<string> systems,
        IReadOnlyDictionary<string, SystemCatalog> catalogs, Dictionary<string, SystemSolution>? existing)
    {
        var solutions = existing is null ? new Dictionary<string, SystemSolution>() : new Dictionary<string, SystemSolution>(existing);
        var scores = new List<ObjectScore>();

        foreach (var system in systems)
        {
            if (!catalogs.TryGetValue(system, out var catalog)) continue;
            if (!solutions.TryGetValue(system, out var solution)) solutions[system] = solution = new SystemSolution();

            foreach (var obj in objects)
            {
                if (solution.BusinessObjects.TryGetValue(obj.Value, out var kept) && kept.DocumentTypes.Count > 0)
                {
                    scores.Add(new ObjectScore(system, obj.Value, 100, 100, "Kept your configuration.", "Kept your configuration."));
                    continue;
                }
                var (docs, importScore, importBasis) = PickDoctypes(obj, catalog);
                var mappings = docs.Select(d => BuildMapping(obj, d, catalog)).ToList();
                var viewScores = mappings.Select(m => PickQuery(obj, m, catalog)).ToList();
                var viewScore = viewScores.Count == 0 ? 60 : viewScores.Min(v => v.Score);
                solution.BusinessObjects[obj.Value] = new ObjectSolution { DocumentTypes = mappings };
                scores.Add(new ObjectScore(system, obj.Value, obj.Custom ? Math.Min(importScore, 72) : importScore,
                    obj.Custom ? Math.Min(viewScore, 78) : viewScore,
                    importBasis, viewScores.FirstOrDefault().Basis ?? "No document types to query."));
            }
        }

        return new Result(solutions, scores,
            scores.Count == 0 ? 100 : scores.Min(s => s.Import),
            scores.Count == 0 ? 100 : scores.Min(s => s.View));
    }

    private static string[] Tokens(BusinessObjectRef obj) =>
        Regex.Split($"{obj.Label} {obj.Value}".Replace("__c", ""), @"[^A-Za-z0-9]+|(?<=[a-z])(?=[A-Z])")
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 1 && t is not "sc" and not "u")
            .Distinct()
            .ToArray();

    private static string Squash(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    private static bool ContainsAll(string text, string[] tokens)
    {
        var squashed = Squash(text);
        var labelTokens = tokens.Length > 2 ? tokens.Take(2).ToArray() : tokens;
        return labelTokens.Length > 0 && labelTokens.All(t => squashed.Contains(t));
    }

    private static (List<EcmDoctype> Docs, int Score, string Basis) PickDoctypes(BusinessObjectRef obj, SystemCatalog catalog)
    {
        var configured = catalog.ConfiguredEntries(obj.Value)
            .Select(e => catalog.Doctypes.Doctypes.FirstOrDefault(d =>
                string.Equals(d.ContentTypeId, e["ecmContentTypeId"]?.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.ContentTypeName, e["ecmContentTypeName"]?.ToString(), StringComparison.OrdinalIgnoreCase)))
            .Where(d => d is not null).Select(d => d!).Distinct().ToList();
        if (configured.Count > 0)
            return (configured.Take(MaxDoctypesPerObject).ToList(), 97, $"Already configured for {obj.Label} in {catalog.System}.");

        var tokens = Tokens(obj);
        var byName = catalog.Doctypes.Doctypes.Where(d => ContainsAll(d.ContentTypeName, tokens)).ToList();
        if (byName.Count > 0)
            return (byName.Take(MaxDoctypesPerObject).ToList(), 90, $"Document type names match {obj.Label}.");

        var byKeyword = catalog.Doctypes.Doctypes.Where(d => d.Keywords.Any(k => ContainsAll(k.Label, tokens))).ToList();
        if (byKeyword.Count > 0)
            return (byKeyword.Take(MaxDoctypesPerObject).ToList(), 85, $"Document types carry a {obj.Label} keyword.");

        return (new List<EcmDoctype>(), 60, $"No document type in {catalog.System} clearly matches {obj.Label}. Add one manually.");
    }

    private static DocTypeMapping BuildMapping(BusinessObjectRef obj, EcmDoctype doc, SystemCatalog catalog)
    {
        var tokens = Tokens(obj);
        var entry = catalog.ConfiguredEntries(obj.Value).FirstOrDefault(e =>
            string.Equals(e["ecmContentTypeId"]?.ToString(), doc.ContentTypeId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e["ecmContentTypeName"]?.ToString(), doc.ContentTypeName, StringComparison.OrdinalIgnoreCase));
        var imported = (entry?["metadataFieldImportMappings"] as JsonArray)?.OfType<JsonObject>()
            .Where(m => !string.IsNullOrWhiteSpace(m["ecmFieldName"]?.ToString()))
            .ToDictionary(m => m["ecmFieldName"]!.ToString(), m => m["inputSource"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var record = imported.FirstOrDefault(kv => kv.Value == "1").Key
            ?? doc.Keywords.FirstOrDefault(k => string.Equals(k.Id, catalog.RecordKeywordId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? doc.Keywords.FirstOrDefault(k => ContainsAll(k.Label, tokens) && IdLike.IsMatch(k.Label))?.Id
            ?? doc.Keywords.FirstOrDefault(k => GenericRecordKeyword.IsMatch(k.Label) || GenericRecordKeyword.IsMatch(k.Id))?.Id;

        string SourceFor(EcmKeyword k)
        {
            if (string.Equals(k.Id, record, StringComparison.OrdinalIgnoreCase)) return "lob";
            if (imported.TryGetValue(k.Id, out var src) && src is "1" or "3") return "lob";
            if (ContainsAll(k.Label, tokens) || LobProperty.IsMatch(k.Label)) return "lob";
            if (ManualProperty.IsMatch(k.Label)) return "manual";
            return "document";
        }

        return new DocTypeMapping
        {
            DocumentTypeId = doc.ContentTypeId,
            DocumentTypeName = doc.ContentTypeName,
            RecordKeywordId = record,
            Keywords = doc.Keywords.Select(k => new KeywordMapping
            {
                KeywordId = k.Id,
                Name = k.Label,
                DataType = k.DataType,
                Required = k.Required || string.Equals(k.Id, record, StringComparison.OrdinalIgnoreCase),
                Source = SourceFor(k),
            }).ToList(),
        };
    }

    private static (int Score, string? Basis) PickQuery(BusinessObjectRef obj, DocTypeMapping mapping, SystemCatalog catalog)
    {
        static bool HasInput(JsonObject q, string? id) => id is not null &&
            ((q["inputs"] as JsonArray)?.OfType<JsonObject>().Any(i => string.Equals(i["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase)) ?? false);

        var configured = catalog.ConfiguredQueries(obj.Value).ToList();
        var fromConfig = configured.FirstOrDefault(q => (q["filterClauses"] as JsonArray)?.OfType<JsonObject>()
                             .Any(c => string.Equals(c["ecmFieldId"]?.ToString(), mapping.RecordKeywordId, StringComparison.OrdinalIgnoreCase)) ?? false)
                         ?? configured.FirstOrDefault();
        if (fromConfig is not null)
        {
            Set(fromConfig["id"]?.ToString(), fromConfig["name"]?.ToString());
            return (95, $"Uses the query already configured for {obj.Label}.");
        }

        var byRecord = catalog.Queries.OfType<JsonObject>().FirstOrDefault(q => HasInput(q, mapping.RecordKeywordId));
        if (byRecord is not null)
        {
            Set(byRecord["id"]?.ToString(), byRecord["name"]?.ToString());
            return (90, "Query filters by the business object ID keyword.");
        }

        var byName = catalog.Queries.OfType<JsonObject>().FirstOrDefault(q =>
            ContainsAll(q["name"]?.ToString() ?? "", Tokens(new BusinessObjectRef { Label = mapping.DocumentTypeName, Value = "" })));
        if (byName is not null)
        {
            Set(byName["id"]?.ToString(), byName["name"]?.ToString());
            return (75, "Query name matches the document type. Confirm the business object ID filter.");
        }
        return (60, "No query filters by the business object ID. Choose one manually.");

        void Set(string? id, string? name)
        {
            mapping.QueryId = id;
            mapping.QueryName = string.IsNullOrWhiteSpace(name) ? id : name;
        }
    }
}
