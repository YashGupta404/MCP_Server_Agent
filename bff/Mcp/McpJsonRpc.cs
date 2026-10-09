using System.Text;
using System.Text.Json;

// ================= MCP JSON-RPC (Streamable HTTP) client =================

// Minimal client for calling a tool on the MCP server deterministically (no LLM). Does the required
// handshake (initialize -> notifications/initialized -> tools/call), tracking the Mcp-Session-Id the
// server returns, and handles both JSON and text/event-stream (SSE) responses.
static class McpJsonRpc
{
    public static async Task<string> CallToolAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string toolName, object arguments,
        ILogger log, CancellationToken ct)
    {
        var (text, _) = await CallToolWithStatusAsync(httpFactory, mcp, toolName, arguments, log, ct);
        return text;
    }

    // Returns the queries configured for a business object from the solution config
    // (businessObjectConfig.queryConfig[busObject].queries), or null when that object has no queryConfig
    // entry. Shape: [{ id, name, type, default }]. Drives the panel's Queries dropdown per business object.
    public static async Task<object[]?> GetQueriesForBusinessObjectAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string boType, ILogger log, CancellationToken ct)
    {
        var text = await CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (!data.TryGetProperty("configurations", out var confs)) return null;
            if (!confs.TryGetProperty("businessObjectConfig", out var boc)) return null;
            if (!boc.TryGetProperty("queryConfig", out var qc) || qc.ValueKind != JsonValueKind.Array) return null;

            foreach (var entry in qc.EnumerateArray())
            {
                var bo = entry.TryGetProperty("busObject", out var b) ? b.GetString() : null;
                if (!string.Equals(bo, boType, StringComparison.OrdinalIgnoreCase)) continue;

                var list = new List<object>();
                if (entry.TryGetProperty("queries", out var queries) && queries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var q in queries.EnumerateArray())
                    {
                        var id = q.TryGetProperty("id", out var qi) ? qi.GetString() : null;
                        var name = q.TryGetProperty("name", out var qn) ? qn.GetString() : null;
                        var qtype = q.TryGetProperty("type", out var qt) ? qt.GetString() : null;
                        var isDefault = q.TryGetProperty("default", out var qd) && qd.ValueKind == JsonValueKind.True;
                        if (!string.IsNullOrWhiteSpace(id))
                            list.Add(new { id, name = string.IsNullOrWhiteSpace(name) ? id : name, type = qtype, @default = isDefault });
                    }
                }
                return list.ToArray();
            }
            return null;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "GetQueriesForBusinessObjectAsync: parse failed for {BoType}", boType);
            return null;
        }
    }

    // The default-list query for a business object: its id, the business-object-context field id to
    // scope by (contextParams.boContext.ecmPropId), and the ordered display-column labels (from the
    // matching queryConfig query's displayColumns + displayColumnConfig). Null when the object has no
    // additionalConfig.defaultListQuery — callers then fall back to the native list/record listing.
    public sealed record DefaultListQueryInfo(string Id, string? BoContextFieldId, string[] Columns);

    public static async Task<DefaultListQueryInfo?> GetDefaultListQueryAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string boType, ILogger log, CancellationToken ct)
    {
        var text = await CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (!data.TryGetProperty("configurations", out var confs)) return null;
            if (!confs.TryGetProperty("businessObjectConfig", out var boc)) return null;

            // Business-object-context field to scope the query by (the record id lives on this ecm field).
            string? boCtxFieldId = null;
            if (boc.TryGetProperty("contextParams", out var cp) && cp.TryGetProperty("boContext", out var bctx)
                && bctx.TryGetProperty("ecmPropId", out var ep))
                boCtxFieldId = ep.GetString();

            // Find the first additionalConfig entry for this boType that pins a defaultListQuery.
            string? dlqId = null;
            if (boc.TryGetProperty("additionalConfig", out var ac) && ac.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ac.EnumerateArray())
                {
                    var bo = e.TryGetProperty("busObject", out var b) ? b.GetString() : null;
                    if (!string.Equals(bo, boType, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.TryGetProperty("defaultListQuery", out var dlq) && dlq.ValueKind == JsonValueKind.Object
                        && dlq.TryGetProperty("id", out var di) && di.ValueKind == JsonValueKind.String)
                    {
                        dlqId = di.GetString();
                        if (!string.IsNullOrWhiteSpace(dlqId)) break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(dlqId)) return null;

            // Resolve the query's ordered display-column labels from queryConfig.
            var columns = new List<string>();
            if (boc.TryGetProperty("queryConfig", out var qc) && qc.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in qc.EnumerateArray())
                {
                    var bo = entry.TryGetProperty("busObject", out var b) ? b.GetString() : null;
                    if (!string.Equals(bo, boType, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!entry.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Array) break;
                    foreach (var q in queries.EnumerateArray())
                    {
                        var id = q.TryGetProperty("id", out var qi) ? qi.GetString() : null;
                        if (!string.Equals(id, dlqId, StringComparison.OrdinalIgnoreCase)) continue;

                        // id -> label map from displayColumnConfig.
                        var labelById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (q.TryGetProperty("displayColumnConfig", out var dcc) && dcc.ValueKind == JsonValueKind.Array)
                            foreach (var c in dcc.EnumerateArray())
                            {
                                var cid = c.TryGetProperty("ecmColumnId", out var ci) ? ci.GetString() : null;
                                var cname = c.TryGetProperty("ecmColumnName", out var cn) ? cn.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(cid)) labelById[cid!] = string.IsNullOrWhiteSpace(cname) ? cid! : cname!;
                            }

                        // ordered column ids from the default form factor's ecmColumnSet.
                        if (q.TryGetProperty("displayColumns", out var dcs) && dcs.TryGetProperty("ecmColumnSets", out var sets)
                            && sets.ValueKind == JsonValueKind.Array)
                        {
                            var defForm = dcs.TryGetProperty("defaultFormFactor", out var df) ? df.GetString() : null;
                            JsonElement? chosen = null;
                            foreach (var s in sets.EnumerateArray())
                            {
                                var ff = s.TryGetProperty("formFactorId", out var f) ? f.GetString() : null;
                                if (chosen is null) chosen = s;
                                if (string.Equals(ff, defForm, StringComparison.OrdinalIgnoreCase)) { chosen = s; break; }
                            }
                            if (chosen is { } cs && cs.TryGetProperty("columns", out var colIds) && colIds.ValueKind == JsonValueKind.Array)
                                foreach (var cidEl in colIds.EnumerateArray())
                                {
                                    var cid = cidEl.GetString();
                                    if (!string.IsNullOrWhiteSpace(cid))
                                        columns.Add(labelById.TryGetValue(cid!, out var lbl) ? lbl : cid!);
                                }
                        }
                        break;
                    }
                    break;
                }
            }
            return new DefaultListQueryInfo(dlqId!, boCtxFieldId, columns.ToArray());
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "GetDefaultListQueryAsync: parse failed for {BoType}", boType);
            return null;
        }
    }

    // Builds a human-readable catalog of the saved queries a business object can be SEARCHED by, from
    // the solution config queryConfig: each query's name/id and its user-editable search field(s)
    // (filterClauses where editable==true) with fieldId + default operator. Null when none exist.
    // Drives the chatbot's conversational search (pick a query -> enter a value -> query_documents).
    public static async Task<string?> BuildSearchCatalogAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string boType, ILogger log, CancellationToken ct)
    {
        var text = await CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (!data.TryGetProperty("configurations", out var confs)) return null;
            if (!confs.TryGetProperty("businessObjectConfig", out var boc)) return null;
            if (!boc.TryGetProperty("queryConfig", out var qc) || qc.ValueKind != JsonValueKind.Array) return null;

            var lines = new List<string>();
            foreach (var entry in qc.EnumerateArray())
            {
                var bo = entry.TryGetProperty("busObject", out var b) ? b.GetString() : null;
                if (!string.Equals(bo, boType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!entry.TryGetProperty("queries", out var queries) || queries.ValueKind != JsonValueKind.Array) break;

                foreach (var q in queries.EnumerateArray())
                {
                    var id = q.TryGetProperty("id", out var qi) ? qi.GetString() : null;
                    var name = q.TryGetProperty("name", out var qn) ? qn.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var fields = new List<string>();
                    if (q.TryGetProperty("filterClauses", out var fcs) && fcs.ValueKind == JsonValueKind.Array)
                        foreach (var fc in fcs.EnumerateArray())
                        {
                            // Include every filter field so the chatbot offers the same query set as the
                            // panel's Search dropdown (auto record-scoped fields included).
                            var fid = fc.TryGetProperty("ecmFieldId", out var fi) ? fi.GetString() : null;
                            var fname = fc.TryGetProperty("ecmFieldName", out var fn) ? fn.GetString() : null;
                            var op = fc.TryGetProperty("operator", out var o) ? o.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(fid))
                                fields.Add($"{fname} [fieldId {fid}, operator {op}]");
                        }
                    if (fields.Count == 0) continue; // no filter field on this query

                    lines.Add($"- \"{name}\" [queryId {id}] — search field: {string.Join("; ", fields)}");
                }
                break;
            }
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "BuildSearchCatalogAsync: parse failed for {BoType}", boType);
            return null;
        }
    }

    // Returns the set of content types that are CONFIGURED (uploadable) for a business object, from the
    // solution config's additionalConfig. A doc type not in this set can't be filed on that record type.
    public static async Task<HashSet<string>> GetConfiguredCicContentTypesAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string? boType, ILogger log, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var text = await CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
            using var doc = JsonDocument.Parse(text);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            if (data.TryGetProperty("configurations", out var confs) &&
                confs.TryGetProperty("businessObjectConfig", out var boc) &&
                boc.TryGetProperty("additionalConfig", out var addl) && addl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in addl.EnumerateArray())
                {
                    var bo = entry.TryGetProperty("busObject", out var b) ? b.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(boType) && !string.Equals(bo, boType, StringComparison.OrdinalIgnoreCase)) continue;
                    var tn = entry.TryGetProperty("ecmContentTypeName", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(tn)) set.Add(tn!);
                }
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "GetConfiguredCicContentTypesAsync failed for {BoType}", boType); }
        return set;
    }

    // Returns the user-editable metadata fields for a CIC/Salesforce content type from the solution
    // config: additionalConfig[type].ecmMetadataFieldLabels, minus the auto-mapped
    // (metadataFieldImportMappings) fields. Shape: [{ id, label }].
    public static async Task<object[]> GetCicDocTypeFieldsAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string docType, string? boType,
        ILogger log, CancellationToken ct)
    {
        var text = await CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
        var fields = new List<object>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (!data.TryGetProperty("configurations", out var confs)) return fields.ToArray();
            if (!confs.TryGetProperty("businessObjectConfig", out var boc)) return fields.ToArray();
            if (!boc.TryGetProperty("additionalConfig", out var addl) || addl.ValueKind != JsonValueKind.Array)
                return fields.ToArray();

            foreach (var entry in addl.EnumerateArray())
            {
                var typeName = entry.TryGetProperty("ecmContentTypeName", out var tn) ? tn.GetString() : null;
                if (!string.Equals(typeName, docType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(boType) && entry.TryGetProperty("busObject", out var b)
                    && b.GetString() is { Length: > 0 } bo
                    && !string.Equals(bo, boType, StringComparison.OrdinalIgnoreCase)) continue;

                // Friendly display labels (ecmColumnId -> ecmColumnName), used when available.
                var labelById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("ecmMetadataFieldLabels", out var labels) && labels.ValueKind == JsonValueKind.Array)
                    foreach (var l in labels.EnumerateArray())
                    {
                        var lid = l.TryGetProperty("ecmColumnId", out var ci) ? ci.GetString() : null;
                        var lname = l.TryGetProperty("ecmColumnName", out var cn) ? cn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(lid))
                            labelById[lid!] = string.IsNullOrWhiteSpace(lname) ? lid! : lname!;
                    }

                // Partition import mappings by how the value is sourced:
                //  inputSource 1-5 = auto-populated (record / static / username / filename / filetype) — not user-editable.
                //  inputSource 6   = caller-provided — the field the user/IDP fills and UCEB stores.
                var autoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var importableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("metadataFieldImportMappings", out var mm) && mm.ValueKind == JsonValueKind.Array)
                    foreach (var m in mm.EnumerateArray())
                    {
                        var src = m.TryGetProperty("inputSource", out var isrc)
                            ? (isrc.ValueKind == JsonValueKind.String ? isrc.GetString() : isrc.ToString()) : null;
                        var fid = m.TryGetProperty("ecmFieldName", out var fn) ? fn.GetString() : null;
                        if (string.IsNullOrWhiteSpace(fid)) continue;
                        if (src == "6") importableIds.Add(fid!); else autoIds.Add(fid!);
                    }

                // Return importable fields first (these actually persist), then display-only fields (shown +
                // IDP-extractable as a preview, but not stored on upload in this config). importable flags it.
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fid in importableIds)
                {
                    if (autoIds.Contains(fid) || !emitted.Add(fid)) continue;
                    var label = labelById.TryGetValue(fid, out var lbl) ? lbl : HumanizeFieldId(fid);
                    fields.Add(new { id = fid, label, importable = true });
                }
                foreach (var kv in labelById)
                {
                    if (autoIds.Contains(kv.Key) || importableIds.Contains(kv.Key) || !emitted.Add(kv.Key)) continue;
                    fields.Add(new { id = kv.Key, label = kv.Value, importable = false });
                }
                break;
            }
        }
        catch (JsonException ex) { log.LogWarning(ex, "GetCicDocTypeFieldsAsync: parse failed"); }
        return fields.ToArray();
    }

    // Turns a raw field id (e.g. "hfs_MedicationName") into a readable label ("Medication Name"),
    // used when the solution config has no friendly ecmColumnName for an importable field.
    static string HumanizeFieldId(string id)
    {
        var s = id;
        if (s.StartsWith("hfs_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
        s = s.Replace('_', ' ').Replace('-', ' ');
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());
    }

    // Returns the user-editable capture fields for a Workday document type from
    // get_capture_default_attributes, minus the record-identifying / auto fields. Shape: [{ id, label }].
    public static async Task<object[]> GetWorkdayDocTypeFieldsAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string docType, string boType, string? boId,
        ILogger log, CancellationToken ct)
    {
        var singleValued = JsonSerializer.Serialize(new[] { new { name = "businessObjectId", value = boId ?? "" } });
        var text = await CallToolAsync(httpFactory, mcp, "get_capture_default_attributes", new
        {
            documentTypeId = docType,
            businessObjectType = boType,
            singleValuedBusinessObjectAttributesJson = singleValued,
        }, log, ct);

        var fields = new List<object>();
        try
        {
            var braceIdx = text.IndexOf('{');
            if (braceIdx < 0) return fields.ToArray();
            using var doc = JsonDocument.Parse(text[braceIdx..]);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return fields.ToArray();
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var label = item.TryGetProperty("businessObjectAttributeName", out var bn)
                    && bn.GetString() is { Length: > 0 } bns ? bns : (name ?? id);
                var key = ($"{id} {name}").ToLowerInvariant();
                if (key.Contains("objectid") || key.Contains("employeeid") || key.Contains("doc_type_id"))
                    continue;
                fields.Add(new { id, label });
            }
        }
        catch (JsonException ex) { log.LogWarning(ex, "GetWorkdayDocTypeFieldsAsync: parse failed"); }
        return fields.ToArray();
    }

    // Like CallToolAsync but also returns the tool's isError flag from the JSON-RPC result, so callers
    // (e.g. upload) can tell a real success from a tool that ran but reported a failure in its text.
    public static async Task<(string Text, bool IsError)> CallToolWithStatusAsync(
        IHttpClientFactory httpFactory, McpOptions mcp, string toolName, object arguments,
        ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mcp.BaseUrl))
            throw new InvalidOperationException("Mcp:BaseUrl is not configured.");

        var endpoint = $"{mcp.BaseUrl.TrimEnd('/')}/mcp";
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(120);

        // 1) initialize
        var (initResult, sessionId) = await PostRequestAsync(http, mcp, endpoint, sessionId: null,
            protocolVersion: null, id: 1, method: "initialize", @params: new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "UcebAgentBff", version = "0.1" },
            }, log, ct);

        var protocolVersion = "2025-06-18";
        if (initResult is { } ir && ir.TryGetProperty("protocolVersion", out var pv) && pv.GetString() is { } negotiated)
            protocolVersion = negotiated;

        // 2) notifications/initialized (a notification: no id, no response body expected)
        await PostNotificationAsync(http, mcp, endpoint, sessionId, protocolVersion, "notifications/initialized", ct);

        // 3) tools/call
        var (callResult, _) = await PostRequestAsync(http, mcp, endpoint, sessionId, protocolVersion,
            id: 2, method: "tools/call", @params: new { name = toolName, arguments }, log, ct);

        if (callResult is not { } result)
            throw new InvalidOperationException($"MCP tools/call '{toolName}' returned no result.");

        // The tool's text output lives in result.content[] where type == "text".
        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    part.TryGetProperty("text", out var txt) && txt.GetString() is { } s)
                    sb.Append(s);
            }
        }

        var isError = result.TryGetProperty("isError", out var errEl) &&
            errEl.ValueKind == JsonValueKind.True;
        return (sb.ToString(), isError);
    }

    private static async Task<(JsonElement? Result, string? SessionId)> PostRequestAsync(
        HttpClient http, McpOptions mcp, string endpoint, string? sessionId, string? protocolVersion,
        int id, string method, object @params, ILogger log, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        AddHeaders(req, mcp, sessionId, protocolVersion);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }),
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var newSession = sessionId;
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var vals))
            newSession = vals.FirstOrDefault() ?? sessionId;

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            log.LogError("MCP {Method} failed {Status}: {Body}", method, (int)resp.StatusCode, errBody);
            throw new InvalidOperationException($"MCP {method} failed ({(int)resp.StatusCode}): {errBody}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line["data:".Length..].Trim();
                if (data.Length > 0 && TryExtractResult(data, id, out var r))
                    return (r, newSession);
            }
            return (null, newSession);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        return (TryExtractResult(body, id, out var res) ? res : null, newSession);
    }

    private static async Task PostNotificationAsync(
        HttpClient http, McpOptions mcp, string endpoint, string? sessionId, string? protocolVersion,
        string method, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        AddHeaders(req, mcp, sessionId, protocolVersion);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", method }),
            Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        // 202 Accepted (or 200) with no body is expected; nothing to parse.
    }

    private static void AddHeaders(HttpRequestMessage req, McpOptions mcp, string? sessionId, string? protocolVersion)
    {
        req.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (!string.IsNullOrEmpty(sessionId))
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (!string.IsNullOrEmpty(protocolVersion))
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
    }

    // Parses one JSON-RPC message; returns its cloned "result" when the id matches (and throws on error).
    private static bool TryExtractResult(string json, int expectedId, out JsonElement result)
    {
        result = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number &&
                idEl.TryGetInt32(out var gotId) && gotId != expectedId)
                return false;
            if (root.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"MCP error: {err}");
            if (root.TryGetProperty("result", out var res))
            {
                result = res.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Not a complete/parseable JSON message (e.g. a keep-alive line) — skip it.
        }
        return false;
    }

    // Parses the list_documents tool's text output into structured document cards.
    // Expected lines: "- docId: <id> (Col=Value, Col2=Value2)".
    public static object[] ParseDocumentList(string text)
    {
        var docs = new List<(long sortKey, object doc)>();
        if (string.IsNullOrEmpty(text)) return Array.Empty<object>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- docId:", StringComparison.OrdinalIgnoreCase)) continue;

            var rest = line["- docId:".Length..].Trim();
            string docId;
            string? attrsText = null;
            var open = rest.IndexOf('(');
            if (open >= 0)
            {
                docId = rest[..open].Trim();
                var close = rest.LastIndexOf(')');
                attrsText = close > open ? rest[(open + 1)..close] : rest[(open + 1)..];
            }
            else
            {
                docId = rest;
            }

            var attributes = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(attrsText))
            {
                foreach (var pair in attrsText.Split(','))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = pair[..eq].Trim();
                    var v = pair[(eq + 1)..].Trim();
                    if (k.Length > 0) attributes[k] = v;
                }
            }

            // Resolve a human display name across LOBs: Salesforce/CIC exposes hfs_Name; Workday/OnBase
            // exposes "Document Name"/"Name". Fall back to the docId (never an arbitrary attribute, which
            // used to make OnBase cards show a random column value).
            string? PickValue(params string[] keys)
            {
                foreach (var key in keys)
                {
                    var k = attributes.Keys.FirstOrDefault(x =>
                        string.Equals(x, key, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(attributes[x]));
                    if (k is not null) return attributes[k];
                }
                return null;
            }

            string name = PickValue("hfs_Name", "Document", "Document Name", "Name", "File Name", "Title") ?? docId;
            string? type = PickValue("Document Type", "Type");

            // Drop the columns surfaced as name/type (and their duplicates) so the card sub-line doesn't
            // just repeat the title/type.
            foreach (var dup in new[] { "hfs_Name", "Document", "Document Name", "Name", "File Name", "Title", "Document Type", "Type", "Document Handle" })
            {
                var k = attributes.Keys.FirstOrDefault(x => string.Equals(x, dup, StringComparison.OrdinalIgnoreCase));
                if (k is not null) attributes.Remove(k);
            }

            var sortKey = long.TryParse(docId, out var idNum) ? idNum : 0;
            docs.Add((sortKey, new { docId, name, type, attributes }));
        }

        // Newest-first: latest uploads (highest docId) render at the top of the panel.
        return docs.OrderByDescending(d => d.sortKey).Select(d => d.doc).ToArray();
    }

    // Parses the list_system_configurations tool's raw JSON ({ data: [ { friendlyName, systemType,
    // description, active, default, ... } ], ... }) into simple objects for the plugin's system picker.
    public static object[] ParseSystemConfigs(string text, string active)
    {
        var list = new List<object>();
        if (string.IsNullOrWhiteSpace(text)) return list.ToArray();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            System.Text.Json.JsonElement arr = default;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array) arr = root;
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                     && root.TryGetProperty("data", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Array) arr = d;
            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array) return list.ToArray();

            static string? Str(System.Text.Json.JsonElement e, params string[] names)
            {
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                        return v.GetString();
                return null;
            }
            static bool Bool(System.Text.Json.JsonElement e, params string[] names)
            {
                foreach (var n in names)
                    if (e.TryGetProperty(n, out var v)
                        && (v.ValueKind == System.Text.Json.JsonValueKind.True || v.ValueKind == System.Text.Json.JsonValueKind.False))
                        return v.GetBoolean();
                return false;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var friendlyName = Str(item, "friendlyName", "FriendlyName");
                if (string.IsNullOrWhiteSpace(friendlyName)) continue;
                list.Add(new
                {
                    friendlyName,
                    systemType = Str(item, "systemType", "SystemType") ?? "",
                    description = Str(item, "description", "Description") ?? "",
                    isDefault = Bool(item, "default", "Default", "isDefault"),
                    isActive = string.Equals(friendlyName, active, StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (System.Text.Json.JsonException) { }
        return list.ToArray();
    }

    // Extracts document (content) type names from the list_document_types tool text. The tool's
    // output format isn't strictly specified, so this is tolerant: it handles bullet lists,
    // comma-separated single lines, and "name:"/"id:" prefixed lines, returning a de-duplicated set.
    public static string[] ParseDocumentTypes(string text)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return found.ToArray();

        void Add(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var t = token.Trim().Trim('"', '\'', '.', ',', ';', ':').Trim();
            // A content type name is a single word (no spaces), letters/digits/._- , reasonable length.
            if (t.Length is < 2 or > 64) return;
            if (t.Contains(' ')) return;
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, "^[A-Za-z][A-Za-z0-9._-]+$")) return;
            if (found.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(t);
        }

        // Like Add but for names taken from the structured JSON list, where a content type name can
        // legitimately contain spaces and punctuation (e.g. OnBase "COM - Application", "SCH - Schedule A").
        void AddName(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var t = token.Trim().Trim('"', '\'').Trim();
            if (t.Length is < 1 or > 96) return;
            if (found.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(t);
        }

        // list_document_types returns raw JSON, e.g.
        //   { "data": [ { "DocumentTypeName": "...", "DocumentTypeId": "..." }, ... ], "total": n }
        // (Salesforce and Workday share this shape). Parse it directly; only if it isn't JSON do we
        // fall back to the text/bulleted heuristic below.
        try
        {
            using var jdoc = System.Text.Json.JsonDocument.Parse(text);
            var root = jdoc.RootElement;
            System.Text.Json.JsonElement arr = default;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                arr = root;
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                     && root.TryGetProperty("data", out var dataEl)
                     && dataEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                arr = dataEl;

            if (arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String) { AddName(item.GetString()); continue; }
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    // Prefer the human-readable NAME (which may contain spaces, e.g. OnBase
                    // "COM - Application") over the numeric id, so the dropdown shows a usable value and
                    // upload_staged_file receives the ecmContentTypeName it expects.
                    foreach (var prop in new[] { "DocumentTypeName", "documentTypeName", "ecmContentTypeName",
                                                 "name", "DocumentTypeId", "documentTypeId", "id" })
                    {
                        if (item.TryGetProperty(prop, out var pv)
                            && pv.ValueKind == System.Text.Json.JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(pv.GetString()))
                        {
                            AddName(pv.GetString());
                            break;
                        }
                    }
                }
                if (found.Count > 0) return found.ToArray();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON — fall through to the text heuristic.
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            // Strip common bullet / numbering prefixes.
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*([-*•]|\d+[.)])\s*", "");
            // If a line has "name:" / "id:" / "type:", take what's after the colon.
            var colon = line.IndexOf(':');
            if (colon >= 0 && colon < 12)
            {
                var prefix = line[..colon].Trim().ToLowerInvariant();
                if (prefix is "name" or "id" or "type" or "documenttype")
                    line = line[(colon + 1)..].Trim();
            }
            // Comma-separated values on one line.
            if (line.Contains(','))
                foreach (var part in line.Split(','))
                    Add(part);
            else
                Add(line);
        }
        return found.ToArray();
    }

    // Pulls the first http(s) URL out of a tool's text response (e.g. the viewer URL).
    public static string? ExtractUrl(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var idx = text.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var url = text[idx..].Trim();
        var ws = url.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });
        return ws > 0 ? url[..ws] : url;
    }
}
