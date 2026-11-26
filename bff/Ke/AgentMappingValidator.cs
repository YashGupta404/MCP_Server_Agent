// Validates the solution_config_mapper_poc agent's `solutionMapping` outcome against the LIVE ECM catalog and
// the LIVE query catalog. The agent emits nested lists as JSON strings; this parses them and flags any id,
// query input or operator that does not exist.

using System.Text.Json;
using System.Text.Json.Nodes;

static class AgentMappingValidator
{
    public sealed record Result(JsonObject Mapping, List<string> Issues);

    private static readonly HashSet<string> InputSources = new() { "1", "2", "3", "4", "5", "6" };

    // Built-in document columns every backend returns alongside attributes.
    private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
        { "DocumentName", "DocumentTypeName", "DocumentDate", "DocumentID" };

    public static Result Validate(JsonElement outcome, EcmCatalog catalog, JsonArray queryCatalog)
    {
        var issues = new List<string>();
        var m = new JsonObject();

        string Str(string name) =>
            outcome.TryGetProperty(name, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
                : "";

        // Fields the agent returns as JSON text; fall back to the raw string when it isn't valid JSON.
        JsonNode? Json(string name)
        {
            if (!outcome.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return JsonNode.Parse(v.GetRawText());
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return JsonNode.Parse(s); }
            catch { issues.Add($"{name}: agent returned non-JSON text"); return JsonValue.Create(s); }
        }

        void NormalizeInputSource(JsonObject item, string where)
        {
            var src = item["inputSource"]?.ToString();
            if (string.IsNullOrEmpty(src)) return;
            item["inputSource"] = src;
            if (!InputSources.Contains(src)) issues.Add($"{where}: invalid inputSource '{src}'.");
        }

        // ---- doctype + attributes ----
        var typeId = Str("ecmDocumentTypeId");
        var doctype = catalog.Doctypes.FirstOrDefault(d => string.Equals(d.ContentTypeId, typeId, StringComparison.OrdinalIgnoreCase));
        if (doctype is null) issues.Add($"Document type id '{typeId}' is not in the live ECM catalog.");
        var attrIds = new HashSet<string>(doctype?.Keywords.Select(k => k.Id) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        void CheckAttr(string where, string? id, bool allowSystem = false)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (allowSystem && SystemColumns.Contains(id)) return;
            if (!attrIds.Contains(id)) issues.Add($"{where}: attribute '{id}' does not belong to document type '{typeId}'.");
        }

        // The agent may emit a nested object as "", plain text or double-encoded JSON; indexing those would throw.
        JsonObject? AsObject(JsonNode? n, string where)
        {
            if (n is null or JsonObject) return n as JsonObject;
            var s = n is JsonValue v && v.TryGetValue<string>(out var str) ? str : n.ToJsonString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { if (JsonNode.Parse(s) is JsonObject parsed) return parsed; } catch { }
            issues.Add($"{where}: expected an object, agent returned {n.ToJsonString()}.");
            return null;
        }

        var linkage = AsObject(Json("recordLinkageMapping"), "recordLinkageMapping");
        if (linkage is not null)
            foreach (var key in new[] { "boContext", "boUser" })
                if (linkage[key] is { } node && node is not JsonObject)
                    linkage[key] = AsObject(node.DeepClone(), $"recordLinkageMapping.{key}");
        var boContextAttr = (linkage?["boContext"] as JsonObject)?["ecmAttributeId"]?.ToString();
        CheckAttr("recordLinkage.boContext", boContextAttr);
        CheckAttr("recordLinkage.boUser", (linkage?["boUser"] as JsonObject)?["ecmAttributeId"]?.ToString());

        var imports = Json("fieldImportMappings") as JsonArray;
        if (imports is not null)
            foreach (var item in imports.OfType<JsonObject>())
            {
                CheckAttr("fieldImportMappings", item["ecmAttributeId"]?.ToString());
                NormalizeInputSource(item, $"fieldImportMappings[{item["ecmAttributeId"]}]");
            }

        // displayColumns: accept plain ids or { ecmAttributeId, reason } objects.
        var columns = Json("displayColumns") as JsonArray;
        if (columns is not null)
            foreach (var c in columns)
                CheckAttr("displayColumns", c is JsonObject co ? co["ecmAttributeId"]?.ToString() : c?.ToString(), allowSystem: true);

        // ---- queries ----
        var queriesById = queryCatalog.OfType<JsonObject>()
            .Where(q => q["id"] is not null)
            .ToDictionary(q => q["id"]!.ToString(), q => q, StringComparer.OrdinalIgnoreCase);

        JsonObject? Input(JsonObject query, string? fieldId) =>
            (query["inputs"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(i => string.Equals(i["id"]?.ToString(), fieldId, StringComparison.OrdinalIgnoreCase));

        void CheckOperator(string where, JsonObject? input, string? op)
        {
            if (input is null || string.IsNullOrWhiteSpace(op)) return;
            var ops = (input["supportedOperators"] as JsonArray)?.Select(o => o?.ToString()).ToList();
            if (ops is { Count: > 0 } && !ops.Contains(op, StringComparer.Ordinal))
                issues.Add($"{where}: operator '{op}' is not supported (allowed: {string.Join(", ", ops)}).");
        }

        // Default list query: UCEB auto-filters it by the boContext attribute, so that must be one of its inputs.
        var defaultQuery = AsObject(Json("queryMapping"), "queryMapping");
        if (defaultQuery is JsonObject dq && !string.IsNullOrWhiteSpace(dq["queryId"]?.ToString()))
        {
            var qid = dq["queryId"]!.ToString();
            if (!queriesById.TryGetValue(qid, out var q))
                issues.Add($"queryMapping: query '{qid}' is not in the live query catalog.");
            else
            {
                var filterId = dq["recordFilterAttributeId"]?.ToString();
                var input = Input(q, filterId);
                if (input is null)
                    issues.Add($"queryMapping: '{filterId}' is not an input of query '{qid}', so UCEB cannot scope it to the record.");
                if (!string.IsNullOrWhiteSpace(boContextAttr) && !string.Equals(filterId, boContextAttr, StringComparison.OrdinalIgnoreCase))
                    issues.Add($"queryMapping: record filter '{filterId}' differs from the record-linkage attribute '{boContextAttr}'.");
                CheckOperator("queryMapping", input, dq["operator"]?.ToString());
            }
        }

        var searchQueries = Json("searchQueryMappings") as JsonArray;
        if (searchQueries is not null)
            foreach (var sq in searchQueries.OfType<JsonObject>())
            {
                var qid = sq["queryId"]?.ToString() ?? "";
                if (!queriesById.TryGetValue(qid, out var q))
                {
                    issues.Add($"searchQueryMappings: query '{qid}' is not in the live query catalog.");
                    continue;
                }
                if (sq["filterClauses"] is JsonArray clauses)
                    foreach (var fc in clauses.OfType<JsonObject>())
                    {
                        var fid = fc["ecmFieldId"]?.ToString();
                        var input = Input(q, fid);
                        if (input is null) issues.Add($"searchQueryMappings[{qid}]: '{fid}' is not an input of the query.");
                        CheckOperator($"searchQueryMappings[{qid}]", input, fc["operator"]?.ToString());
                        NormalizeInputSource(fc, $"searchQueryMappings[{qid}].{fid}");
                    }
                if (sq["displayColumns"] is JsonArray qcols)
                {
                    var resultIds = new HashSet<string>((q["resultColumns"] as JsonArray)?.OfType<JsonObject>()
                        .Select(c => c["id"]?.ToString() ?? "") ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    foreach (var c in qcols)
                    {
                        var cid = c is JsonObject co ? co["ecmAttributeId"]?.ToString() : c?.ToString();
                        if (!string.IsNullOrWhiteSpace(cid) && !resultIds.Contains(cid))
                            issues.Add($"searchQueryMappings[{qid}]: column '{cid}' is not a result column of the query.");
                    }
                }
            }

        m["doctypeMapping"] = AsObject(Json("doctypeMapping"), "doctypeMapping");
        m["recordLinkageMapping"] = linkage;
        m["fieldImportMappings"] = imports;
        m["displayColumnMapping"] = columns;
        m["queryMapping"] = defaultQuery;
        m["searchQueryMappings"] = searchQueries;
        m["unmapped"] = Json("unmapped");
        m["confidence"] = outcome.TryGetProperty("confidence", out var conf) && conf.TryGetDouble(out var d) ? d : null;
        m["rationale"] = Str("rationale");
        // The agent's own restatement of its inputs (present once the Studio outcome has these fields).
        foreach (var f in new[] { "receivedBusinessContext", "receivedDocumentInsights", "receivedEcmCatalogSummary", "receivedEcmQueriesSummary" })
            if (outcome.TryGetProperty(f, out _)) m[f] = Json(f);
        return new Result(m, issues);
    }

    // Capture confidence computed in code (the LLM doesn't follow scoring rules reliably). Two parts only:
    // doctype = share of documents whose KE classification equals the chosen doctype; keywords = share of the
    // doctype's keywords that are filled automatically (record field from the record id, others with a value).
    public static JsonObject CaptureConfidence(JsonObject mapping, EcmCatalog catalog, IReadOnlyList<string?> classifications, string? recordId)
    {
        var typeId = (mapping["doctypeMapping"] as JsonObject)?["ecmDocumentTypeId"]?.ToString();
        var doctype = catalog.Doctypes.FirstOrDefault(d => string.Equals(d.ContentTypeId, typeId, StringComparison.OrdinalIgnoreCase));

        double doctypeScore;
        string doctypeReason;
        var known = classifications.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (doctype is null)
        {
            doctypeScore = 0;
            doctypeReason = $"Document type '{typeId}' is not in the live catalog.";
        }
        else if (known.Count == 0)
        {
            doctypeScore = 0.8;
            doctypeReason = "Not cross-checked: no KE classification available.";
        }
        else
        {
            var matches = known.Count(c => string.Equals(c!.Trim(), doctype.ContentTypeName, StringComparison.OrdinalIgnoreCase));
            doctypeScore = (double)matches / known.Count;
            doctypeReason = $"KE classified {matches} of {known.Count} document(s) as '{doctype.ContentTypeName}'.";
        }

        var recordField = ((mapping["recordLinkageMapping"] as JsonObject)?["boContext"] as JsonObject)?["ecmAttributeId"]?.ToString();
        var values = (mapping["fieldImportMappings"] as JsonArray)?.OfType<JsonObject>()
            .Where(i => !string.IsNullOrWhiteSpace(i["ecmAttributeId"]?.ToString()))
            .GroupBy(i => i["ecmAttributeId"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()["sampleValue"]?.ToString(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var keywords = doctype?.Keywords ?? new List<EcmKeyword>();
        var missing = new JsonArray();
        var populated = 0;
        foreach (var k in keywords)
        {
            var filled = string.Equals(k.Id, recordField, StringComparison.OrdinalIgnoreCase)
                ? !string.IsNullOrWhiteSpace(recordId)
                : values.TryGetValue(k.Id, out var v) && !string.IsNullOrWhiteSpace(v);
            if (filled) populated++;
            else missing.Add($"{k.Label} ({k.Id})");
        }
        var keywordScore = keywords.Count == 0 ? 0 : (double)populated / keywords.Count;

        return new JsonObject
        {
            ["overall"] = Math.Round((doctypeScore + keywordScore) / 2, 2),
            ["doctype"] = new JsonObject { ["score"] = Math.Round(doctypeScore, 2), ["reason"] = doctypeReason },
            ["keywords"] = new JsonObject
            {
                ["score"] = Math.Round(keywordScore, 2),
                ["populated"] = populated,
                ["total"] = keywords.Count,
                ["missing"] = missing,
            },
        };
    }
}
