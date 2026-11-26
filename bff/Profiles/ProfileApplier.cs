using System.Text.Json.Nodes;

// Writes a profile's solution configuration for one system into UCEB (set_solution_configuration). Only the
// profile's business objects are replaced in additionalConfig/queryConfig; everything else is kept. The current
// config is backed up first under config-backups/profiles/.
static class ProfileApplier
{
    public static async Task<ApplyResult> ApplyAsync(
        IntegrationProfile profile, string system, SystemCatalog catalog, string contentRoot,
        IHttpClientFactory httpFactory, McpOptions mcp, ILogger log, CancellationToken ct)
    {
        var result = new ApplyResult { At = DateTimeOffset.UtcNow };
        try
        {
            await McpSystemScope.RunAsync(httpFactory, mcp, system, log, async () =>
            {
                var current = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
                if (!current.TrimStart().StartsWith('{'))
                    throw new InvalidOperationException($"Could not read the solution configuration: {current}");

                var backupDir = Path.Combine(contentRoot, "config-backups", "profiles");
                Directory.CreateDirectory(backupDir);
                var safe = string.Concat(system.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                result.Backup = Path.Combine(backupDir, $"{safe}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(result.Backup, current, ct);

                var node = JsonNode.Parse(current)!;
                var data = (node["data"] ?? node) as JsonObject ?? throw new InvalidOperationException("Solution configuration has no data object.");
                var configurations = data["configurations"] as JsonObject ?? throw new InvalidOperationException("Solution configuration has no configurations.");
                if (configurations["businessObjectConfig"] is not JsonObject boc)
                    configurations["businessObjectConfig"] = boc = new JsonObject();

                var solution = profile.Solutions.TryGetValue(system, out var s) ? s : new SystemSolution();
                var objects = profile.BusinessObjects.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (boc["additionalConfig"] is not JsonArray additional)
                    boc["additionalConfig"] = additional = new JsonArray();
                foreach (var e in additional.OfType<JsonObject>().Where(e => objects.Contains(e["busObject"]?.ToString() ?? "")).ToList())
                    additional.Remove(e);
                if (boc["queryConfig"] is not JsonArray queryConfig)
                    boc["queryConfig"] = queryConfig = new JsonArray();
                foreach (var e in queryConfig.OfType<JsonObject>().Where(e => objects.Contains(e["busObject"]?.ToString() ?? "")).ToList())
                    queryConfig.Remove(e);

                foreach (var obj in profile.BusinessObjects)
                {
                    if (!solution.BusinessObjects.TryGetValue(obj.Value, out var objSolution)) continue;
                    var queries = new JsonArray();
                    foreach (var doc in objSolution.DocumentTypes)
                    {
                        var groupId = await DoctypeGroupResolver.ResolveAsync(httpFactory, mcp, system, doc.DocumentTypeId, log, ct);
                        var entry = BuildEntry(obj.Value, doc, groupId, profile.RecordIdProperty);
                        if (!string.IsNullOrWhiteSpace(doc.QueryId))
                        {
                            entry["defaultListQuery"] = new JsonObject
                            {
                                ["name"] = doc.QueryName ?? doc.QueryId,
                                ["id"] = doc.QueryId,
                                ["type"] = "PreConfigured",
                                ["default"] = true,
                            };
                            if (!queries.OfType<JsonObject>().Any(q => q["id"]?.ToString() == doc.QueryId))
                                queries.Add(BuildQuery(doc, catalog, profile.RecordIdProperty, isDefault: queries.Count == 0));
                        }
                        additional.Add(entry);
                    }
                    if (queries.Count > 0)
                        queryConfig.Add(new JsonObject { ["busObject"] = obj.Value, ["queries"] = queries });
                }
                EnsureContextParams(boc, solution, profile.RecordIdProperty);

                var write = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "set_solution_configuration",
                    new { dataJson = data.ToJsonString() }, log, ct);
                if (!write.Contains("saved", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(write);
                return true;
            }, ct);
            result.Ok = true;
            ProfileCatalog.Invalidate(system);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ProfileApplier: writing profile {Profile} to {System} failed", profile.Name, system);
            result.Ok = false;
            result.Error = ex.Message;
        }
        return result;
    }

    private static JsonObject Value(string value, string type) =>
        new() { ["value"] = value, ["type"] = string.IsNullOrWhiteSpace(type) ? "String" : type, ["format"] = "" };

    private static JsonArray Labels(IEnumerable<KeywordMapping> keywords) => new(keywords.Select(k => (JsonNode)new JsonObject
    {
        ["ecmColumnName"] = k.Name,
        ["ecmColumnId"] = k.KeywordId,
        ["localeStrings"] = new JsonArray(new JsonObject
        {
            ["culture"] = "en_US",
            ["name"] = k.Name,
            ["description"] = $"Default label for {k.KeywordId}",
        }),
    }).ToArray());

    // Record keyword -> inputSource 1 (LOB object property). Every other keyword -> 6 (manual entry): UCEB has no
    // "extracted from the document" source, so the panel supplies those values at upload time.
    private static JsonObject BuildEntry(string busObject, DocTypeMapping doc, string? groupId, string recordIdProperty)
    {
        var imports = new JsonArray();
        foreach (var k in doc.Keywords)
        {
            var isRecord = string.Equals(k.KeywordId, doc.RecordKeywordId, StringComparison.OrdinalIgnoreCase);
            imports.Add(new JsonObject
            {
                ["ecmFieldName"] = k.KeywordId,
                ["inputSource"] = isRecord ? "1" : "6",
                ["value"] = Value(isRecord ? recordIdProperty : "", k.DataType),
            });
        }
        return new JsonObject
        {
            ["busObject"] = busObject,
            ["ecmContentTypeName"] = doc.DocumentTypeName,
            ["ecmContentTypeId"] = doc.DocumentTypeId,
            ["ecmContentTypeGroupId"] = string.IsNullOrWhiteSpace(groupId) ? null : groupId,
            ["contextParams"] = new JsonArray(),
            ["ecmMetadataFieldLabels"] = Labels(doc.Keywords),
            ["displayColumns"] = new JsonObject
            {
                ["ecmColumnSets"] = new JsonArray(new JsonObject
                {
                    ["formFactorId"] = "desktop",
                    ["columns"] = new JsonArray(doc.Keywords.Select(k => (JsonNode)JsonValue.Create(k.KeywordId)!).ToArray()),
                }),
                ["defaultFormFactor"] = "desktop",
            },
            ["metadataFieldImportMappings"] = imports,
        };
    }

    // The query is scoped to the current record by a non-editable filter on the record keyword (inputSource 1).
    private static JsonObject BuildQuery(DocTypeMapping doc, SystemCatalog catalog, string recordIdProperty, bool isDefault)
    {
        var meta = catalog.Query(doc.QueryId);
        var inputs = (meta?["inputs"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var filterInput = inputs.FirstOrDefault(i => string.Equals(i["id"]?.ToString(), doc.RecordKeywordId, StringComparison.OrdinalIgnoreCase));
        var recordKeyword = doc.Keywords.FirstOrDefault(k => string.Equals(k.KeywordId, doc.RecordKeywordId, StringComparison.OrdinalIgnoreCase));

        var filters = new JsonArray();
        var filterId = filterInput?["id"]?.ToString() ?? doc.RecordKeywordId;
        if (!string.IsNullOrWhiteSpace(filterId))
        {
            filters.Add(new JsonObject
            {
                ["ecmFieldName"] = filterInput?["name"]?.ToString() ?? recordKeyword?.Name ?? filterId,
                ["ecmFieldId"] = filterId,
                ["inputSource"] = "1",
                ["value"] = Value(recordIdProperty, "String"),
                ["editable"] = false,
                ["operator"] = "EqualsCaseInsensitive",
            });
        }

        var columns = (meta?["resultColumns"] as JsonArray)?.OfType<JsonObject>()
            .Where(c => !string.IsNullOrWhiteSpace(c["id"]?.ToString()))
            .Select(c => (Id: c["id"]!.ToString(), Name: c["name"]?.ToString() ?? c["id"]!.ToString(), Type: c["dataType"]?.ToString() ?? "String"))
            .ToList();
        if (columns is null || columns.Count == 0)
            columns = new() { ("DocumentName", "Document Name", "String"), ("DocumentDate", "Document Date", "String"), ("DocumentTypeName", "Document Type", "String") };

        return new JsonObject
        {
            ["name"] = doc.QueryName ?? doc.QueryId,
            ["id"] = doc.QueryId,
            ["label"] = null,
            ["description"] = null,
            ["type"] = "PreConfigured",
            ["default"] = isDefault,
            ["filterClauses"] = filters,
            ["displayColumns"] = new JsonObject
            {
                ["ecmColumnSets"] = new JsonArray(new JsonObject
                {
                    ["formFactorId"] = "desktop",
                    ["columns"] = new JsonArray(columns.Select(c => (JsonNode)JsonValue.Create(c.Id)!).ToArray()),
                }),
                ["defaultFormFactor"] = "desktop",
            },
            ["displayColumnConfig"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject
            {
                ["ecmColumnName"] = c.Name,
                ["ecmColumnId"] = c.Id,
                ["type"] = c.Type,
                ["localeStrings"] = null,
            }).ToArray()),
        };
    }

    // Keeps an existing boContext; only fills it when the system has none yet (new system configs).
    private static void EnsureContextParams(JsonObject boc, SystemSolution solution, string recordIdProperty)
    {
        if (boc["contextParams"]?["boContext"]?["ecmPropId"] is not null) return;
        var doc = solution.BusinessObjects.Values.SelectMany(o => o.DocumentTypes).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.RecordKeywordId));
        if (doc is null) return;
        var kw = doc.Keywords.FirstOrDefault(k => string.Equals(k.KeywordId, doc.RecordKeywordId, StringComparison.OrdinalIgnoreCase));
        if (boc["contextParams"] is not JsonObject cp) boc["contextParams"] = cp = new JsonObject();
        cp["boContext"] = new JsonObject
        {
            ["boProp"] = recordIdProperty,
            ["ecmProp"] = kw?.Name ?? doc.RecordKeywordId,
            ["ecmPropId"] = doc.RecordKeywordId,
        };
    }
}
