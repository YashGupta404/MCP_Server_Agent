// Fetches the active system's ECM query catalog (list_queries + get_query_metadata per query) as compact JSON
// for the mapping agent: [{ id, name, type, maxResults, inputs[...], resultColumns[...] }].

using System.Text.Json.Nodes;

static class EcmDoctypeCatalog
{
    // list_document_types?fetchMetadata=true walks every doctype group in one call and 408s after UCEB's 20s budget
    // on large/slow OnBase systems; then list without metadata and fetch each doctype's metadata separately.
    public static async Task<EcmCatalog> FetchAsync(IHttpClientFactory httpFactory, McpOptions mcp, ILogger log, CancellationToken ct)
    {
        var full = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types", new { fetchMetadata = true }, log, ct);
        if (full.TrimStart().StartsWith('{')) return KeContextClient.ParseCatalog(full);

        log.LogWarning("EcmDoctypeCatalog: full catalog failed ({Error}); fetching metadata per doctype", Truncate(full));
        var list = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types", new { fetchMetadata = false }, log, ct);
        if (!list.TrimStart().StartsWith('{'))
            throw new InvalidOperationException($"list_document_types failed: {Truncate(list)}");

        var types = (JsonNode.Parse(list)?["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        using var gate = new SemaphoreSlim(4);
        var withMetadata = await Task.WhenAll(types.Select(async t =>
        {
            var id = t["DocumentTypeId"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) return (JsonNode)t.DeepClone();
            await gate.WaitAsync(ct);
            try
            {
                var meta = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_document_type_metadata", new { documentTypeId = id }, log, ct);
                if (meta.TrimStart().StartsWith('{') && JsonNode.Parse(meta)?["data"] is JsonObject data) return data.DeepClone();
                log.LogWarning("EcmDoctypeCatalog: metadata failed for doctype {Id}: {Error}", id, Truncate(meta));
            }
            finally { gate.Release(); }
            return (JsonNode)t.DeepClone();
        }));
        return KeContextClient.ParseCatalog(new JsonObject { ["data"] = new JsonArray(withMetadata) }.ToJsonString());
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}

static class EcmQueryCatalog
{
    public static async Task<JsonArray> FetchAsync(IHttpClientFactory httpFactory, McpOptions mcp, ILogger log, CancellationToken ct)
    {
        var result = new JsonArray();
        var listText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_queries", new { }, log, ct);
        var queries = JsonNode.Parse(listText)?["data"]?["queries"] as JsonArray;
        if (queries is null) return result;

        foreach (var q in queries.OfType<JsonObject>())
        {
            var id = q["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var entry = new JsonObject
            {
                ["id"] = id,
                ["name"] = q["name"]?.ToString(),
                ["type"] = q["type"]?.ToString(),
            };
            try
            {
                var metaText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_query_metadata", new { queryId = id }, log, ct);
                var data = JsonNode.Parse(metaText)?["data"];
                entry["maxResults"] = data?["maxResults"]?.DeepClone();
                entry["inputs"] = new JsonArray((data?["inputs"] as JsonArray)?.OfType<JsonObject>().Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i["id"]?.ToString(),
                    ["name"] = i["name"]?.ToString(),
                    ["dataType"] = i["dataType"]?.ToString(),
                    ["required"] = i["required"]?.DeepClone(),
                    ["supportedOperators"] = i["supportedOperators"]?.DeepClone(),
                }).ToArray() ?? Array.Empty<JsonNode>());
                entry["resultColumns"] = new JsonArray((data?["resultColumns"] as JsonArray)?.OfType<JsonObject>().Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c["id"]?.ToString(),
                    ["name"] = c["name"]?.ToString(),
                    ["dataType"] = c["dataType"]?.ToString(),
                }).ToArray() ?? Array.Empty<JsonNode>());
            }
            catch (Exception ex)
            {
                // A query whose metadata can't be read is still listed, so the agent can report it as unmapped.
                log.LogWarning(ex, "EcmQueryCatalog: metadata failed for query {QueryId}", id);
                entry["metadataError"] = ex.Message;
            }
            result.Add(entry);
        }
        return result;
    }
}
