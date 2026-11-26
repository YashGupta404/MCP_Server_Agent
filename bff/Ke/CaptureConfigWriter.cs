// Just-in-time capture config (zero-setup PoC). UCEB only accepts an upload when the active system's solution
// config has an entry for the (busObject, doctype) pair, so we write a minimal capture-only entry right before
// the upload. Shape validated on OnBase9714 (2026-11-18, documentId 417): displayColumns is required, and
// OnBase/CFS also needs ecmContentTypeGroupId.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

static class CaptureConfigWriter
{
    public sealed record Field(string Id, string Label, string DataType);

    public sealed record Spec(
        string BusObject, string DocTypeName, string DocTypeId, string? DocTypeGroupId,
        string? RecordFieldId, IReadOnlyList<Field> Fields);

    // Returns the config `data` node ({configurations, configVersion}) with the (busObject, doctype) entry
    // replaced; every other entry is kept.
    public static JsonObject Upsert(JsonNode getResponse, Spec s)
    {
        var data = (getResponse["data"] ?? getResponse) as JsonObject
                   ?? throw new InvalidOperationException("Solution configuration response had no data object.");
        var boc = data["configurations"]?["businessObjectConfig"] as JsonObject
                  ?? throw new InvalidOperationException("Solution configuration has no businessObjectConfig.");
        if (boc["additionalConfig"] is not JsonArray list)
        {
            list = new JsonArray();
            boc["additionalConfig"] = list;
        }

        var existing = list.OfType<JsonObject>().Where(e =>
                string.Equals(e["busObject"]?.ToString(), s.BusObject, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(e["ecmContentTypeId"]?.ToString(), s.DocTypeId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e["ecmContentTypeName"]?.ToString(), s.DocTypeName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var e in existing) list.Remove(e);

        list.Add(BuildEntry(s));
        return data;
    }

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Currency", "SpecificCurrency", "Integer", "Numeric", "Number", "Decimal", "Float", "Double" };

    // OnBase numeric keywords reject text with a broker 500 (verified: Currency 'USD' fails, '2322.00' works).
    // Returns the number only ("USD 2,322.00" -> "2322.00"), the value unchanged for other types, or null if no number.
    public static string? CoerceValue(string? dataType, string value)
    {
        if (string.IsNullOrWhiteSpace(dataType) || !NumericTypes.Contains(dataType)) return value;
        var m = System.Text.RegularExpressions.Regex.Match(value.Replace(",", ""), @"-?\d+(\.\d+)?");
        if (!m.Success) return null;
        return dataType.Equals("Integer", StringComparison.OrdinalIgnoreCase) && m.Value.Contains('.') ? null : m.Value;
    }

    public static JsonObject BuildEntry(Spec s)
    {
        static JsonObject Value(string value, string type) =>
            new() { ["value"] = value, ["type"] = string.IsNullOrWhiteSpace(type) ? "String" : type, ["format"] = "" };

        var imports = new JsonArray();
        if (!string.IsNullOrWhiteSpace(s.RecordFieldId))
            imports.Add(new JsonObject { ["ecmFieldName"] = s.RecordFieldId, ["inputSource"] = "1", ["value"] = Value("Id", "String") });
        foreach (var f in s.Fields)
            imports.Add(new JsonObject { ["ecmFieldName"] = f.Id, ["inputSource"] = "6", ["value"] = Value("", f.DataType) });

        return new JsonObject
        {
            ["busObject"] = s.BusObject,
            ["ecmContentTypeName"] = s.DocTypeName,
            ["ecmContentTypeId"] = s.DocTypeId,
            ["ecmContentTypeGroupId"] = string.IsNullOrWhiteSpace(s.DocTypeGroupId) ? null : s.DocTypeGroupId,
            ["contextParams"] = new JsonArray(),
            ["ecmMetadataFieldLabels"] = new JsonArray(s.Fields.Select(f => (JsonNode)new JsonObject
            {
                ["ecmColumnName"] = f.Label,
                ["ecmColumnId"] = f.Id,
                ["localeStrings"] = new JsonArray(new JsonObject
                {
                    ["culture"] = "en_US",
                    ["name"] = f.Label,
                    ["description"] = $"Default label for {f.Id}",
                }),
            }).ToArray()),
            ["displayColumns"] = new JsonObject
            {
                ["ecmColumnSets"] = new JsonArray(new JsonObject
                {
                    ["formFactorId"] = "desktop",
                    ["columns"] = new JsonArray(s.Fields.Select(f => (JsonNode)JsonValue.Create(f.Id)!).ToArray()),
                }),
                ["defaultFormFactor"] = "desktop",
            },
            ["metadataFieldImportMappings"] = imports,
        };
    }
}

// list_document_types doesn't say which group a doctype belongs to, so list each group's doctypes once
// (doctypeGroupId filter) and cache doctype -> group per system.
static class DoctypeGroupResolver
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string?> ResolveAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string systemName, string docTypeId, ILogger log, CancellationToken ct)
    {
        var key = $"{systemName}|{docTypeId}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var groupsText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_type_groups", new { }, log, ct);
        if (!groupsText.TrimStart().StartsWith('{'))
            throw new InvalidOperationException($"list_document_type_groups failed: {Truncate(groupsText)}");

        foreach (var g in (JsonNode.Parse(groupsText)?["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var groupId = g["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(groupId)) continue;
            var typesText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types",
                new { fetchMetadata = false, doctypeGroupId = groupId }, log, ct);
            if (!typesText.TrimStart().StartsWith('{'))
            {
                log.LogWarning("DoctypeGroupResolver: group {GroupId} listing failed: {Error}", groupId, Truncate(typesText));
                continue;
            }
            foreach (var t in (JsonNode.Parse(typesText)?["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var id = t["DocumentTypeId"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) Cache[$"{systemName}|{id}"] = groupId;
            }
            if (Cache.TryGetValue(key, out var found)) return found;
        }
        return null;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}
