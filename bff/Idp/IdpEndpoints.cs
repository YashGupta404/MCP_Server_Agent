using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

// IDP auto-classification + metadata extraction endpoints, extracted from Program.cs. Registered from
// Program.cs via app.MapIdpEndpoints(...). The dependencies (options, staged-file store, session store,
// http factory, MCP options, logger) are passed in so these routes keep the exact same behavior as when
// they were inline, without pulling IDP logic into the main minimal-API file.
static class IdpEndpoints
{
    public static void MapIdpEndpoints(
        this WebApplication app,
        IdpOptions idp,
        ConcurrentDictionary<string, (byte[] Bytes, string Mime, string Name)> idpFiles,
        SessionStore sessions,
        IHttpClientFactory httpFactory,
        McpOptions mcp,
        ILogger log)
    {
        // ---------- IDP auto-classification (Option C: BFF reads a cached IDP token) ----------
        // IDP's cloud service can't reach localhost, so each uploaded file's bytes are staged in-memory and
        // served PUBLICLY (no session) at GET /api/idp/file/{id}. The sourceUrl we hand IDP is
        // {Idp:PublicBaseUrl}/api/idp/file/{id} — Idp:PublicBaseUrl is a devtunnel pointing at this BFF.
        app.MapGet("/api/idp/file/{id}", (string id) =>
        {
            if (idpFiles.TryGetValue(id, out var f))
                return Results.File(f.Bytes, string.IsNullOrWhiteSpace(f.Mime) ? "application/octet-stream" : f.Mime, f.Name);
            return Results.NotFound();
        });

        // Lightweight readiness probe so the panel can show whether IDP is usable (token cached + public host set).
        app.MapGet("/api/idp/status", async (HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
            if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
                return Results.Json(new { error = "not_authenticated" }, statusCode: 401);
            using var sCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var token = await IdpClient.GetTokenAsync(httpFactory, idp, log, sCts.Token);
            return Results.Json(new
            {
                connected = token is not null,
                publicHost = !string.IsNullOrWhiteSpace(idp.PublicBaseUrl),
            });
        });

        // Classifies each attached file against the active system's document types and returns the detected
        // doc type + confidence per file. Does NOT upload anything — the panel pre-fills the review grid and
        // the user still clicks Upload (existing /api/capture or /api/upload path).
        app.MapPost("/api/idp/classify", async (HttpContext ctx, IdpClassifyRequest req) =>
        {
            var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
            if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
                return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

            if (req.Attachments is not { Length: > 0 })
                return Results.Json(new { error = "no_file", detail = "Attach at least one file to classify." }, statusCode: 400);

            string? token;
            using (var tokCts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                token = await IdpClient.GetTokenAsync(httpFactory, idp, log, tokCts.Token);
            if (token is null)
                return Results.Json(new { error = "idp_not_connected", detail = "No valid IDP token and silent refresh failed. Run bff/idp-feature-test.ps1 to sign in to IDP, then retry." }, statusCode: 401);

            if (string.IsNullOrWhiteSpace(idp.PublicBaseUrl))
                return Results.Json(new { error = "idp_host_not_configured", detail = "Idp:PublicBaseUrl is not set. Point a devtunnel at this BFF and set Idp:PublicBaseUrl." }, statusCode: 500);

            // Candidate classes = the active system's document types (name == class == returned className, 1:1).
            List<string> docTypes;
            try
            {
                using var dtCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types", new { }, log, dtCts.Token);
                docTypes = McpJsonRpc.ParseDocumentTypes(text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "/api/idp/classify: could not list document types");
                return Results.Json(new { error = "doctypes_failed", detail = ex.Message }, statusCode: 502);
            }
            if (docTypes.Count == 0)
                return Results.Json(new { error = "no_doctypes", detail = "The active system has no document types to classify against." }, statusCode: 400);

            var publicBase = idp.PublicBaseUrl.TrimEnd('/');
            var boType = req.BusinessObjectType;
            var stagedIds = new ConcurrentBag<string>();
            // Classify (and extract metadata for) files CONCURRENTLY (capped) so a bulk drop isn't paced one-at-a-time.
            const int maxConcurrency = 6;
            using var gate = new SemaphoreSlim(maxConcurrency);

            // Memoize each doc type's field list (id+label+importable) so we fetch the solution config once per type.
            var fieldCache = new ConcurrentDictionary<string, Task<List<(string Id, string Name, bool Importable)>>>(StringComparer.OrdinalIgnoreCase);
            Task<List<(string Id, string Name, bool Importable)>> FieldsFor(string docType) => fieldCache.GetOrAdd(docType, dt => Task.Run(async () =>
            {
                var list = new List<(string Id, string Name, bool Importable)>();
                try
                {
                    using var fcts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var arr = await McpJsonRpc.GetCicDocTypeFieldsAsync(httpFactory, mcp, dt, boType, log, fcts.Token);
                    foreach (var o in arr)
                    {
                        var jn = JsonNode.Parse(JsonSerializer.Serialize(o));
                        var fid = jn?["id"]?.ToString();
                        var label = jn?["label"]?.ToString();
                        var importable = jn?["importable"]?.GetValue<bool>() ?? true;
                        if (!string.IsNullOrWhiteSpace(fid))
                            list.Add((fid!, string.IsNullOrWhiteSpace(label) ? fid! : label!, importable));
                    }
                }
                catch (Exception ex) { log.LogWarning(ex, "/api/idp/classify: fields fetch failed for {DocType}", dt); }
                return list;
            }));

            // Which of the active system's types can actually be filed on THIS record (for upload gating).
            HashSet<string>? configuredTypes = null;
            if (!string.IsNullOrWhiteSpace(boType))
            {
                using var cfgCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                configuredTypes = await McpJsonRpc.GetConfiguredCicContentTypesAsync(httpFactory, mcp, boType, log, cfgCts.Token);
            }
            // Classify against the record's CONFIGURED types so a document lands on an uploadable type with metadata
            // (e.g. "Invoices", which is configured), not a bare near-name match (e.g. "Invoice", not configured here).
            // Fall back to all system types when there's no record context / nothing configured.
            var candidateTypes = (configuredTypes != null && configuredTypes.Count > 0) ? configuredTypes.ToList() : docTypes;

            // Enrich each class description with its metadata field labels so cryptic type names (e.g.
            // "COM - Application" -> fields "Loan Number") are understood by the classifier, not just matched by name.
            var candidateClasses = new List<(string Name, string Description)>();
            foreach (var t in candidateTypes)
            {
                List<(string Id, string Name, bool Importable)> defs;
                try { defs = await FieldsFor(t); } catch { defs = new List<(string, string, bool)>(); }
                var labels = defs.Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(8).ToList();
                var desc = labels.Count > 0
                    ? $"A '{t}' document. Typical fields include: {string.Join(", ", labels)}."
                    : $"A '{t}' document.";
                candidateClasses.Add((t, desc));
            }

            var attachments = req.Attachments!.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.DataBase64)).ToList();

            // Stage every file first so IDP can be handed ALL of them in ONE classification job (far fewer
            // round-trips than a job per file). Preserve attachment order for the response.
            var staged = new List<(string Name, string? Id, string? SourceUrl, string? InvalidError)>();
            foreach (var att in attachments)
            {
                var name = string.IsNullOrWhiteSpace(att.Name) ? "upload" : Path.GetFileName(att.Name);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(att.DataBase64!); }
                catch { staged.Add((name, null, null, "invalid file data")); continue; }
                var id = Guid.NewGuid().ToString("N");
                idpFiles[id] = (bytes, string.IsNullOrWhiteSpace(att.Mime) ? "application/octet-stream" : att.Mime!, name);
                stagedIds.Add(id);
                staged.Add((name, id, $"{publicBase}/api/idp/file/{id}", null));
            }

            object[] results;
            try
            {
                // 1) ONE classification job for all valid files; map the result back to each file.
                var classByRef = new Dictionary<string, IdpClient.BatchClassResult>(StringComparer.Ordinal);
                var validFiles = staged.Where(s => s.Id is not null).Select(s => (s.Id!, s.SourceUrl!)).ToList();
                if (validFiles.Count > 0)
                {
                    using var cCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                    var (byRef, cErr) = await IdpClient.ClassifyBatchAsync(httpFactory, idp, token, validFiles, candidateClasses, log, cCts.Token);
                    classByRef = byRef;
                    if (cErr is not null) log.LogWarning("/api/idp/classify: batch classify -> {Error}", cErr);
                }

                // 2) Extract each classified file's metadata CONCURRENTLY (fields depend on the detected type).
                var tasks = staged.Select(async s =>
                {
                    if (s.Id is null)
                        return (object)new { name = s.Name, docType = (string?)null, confidence = 0.0, reviewRequired = true, error = s.InvalidError, fields = Array.Empty<object>(), uploadable = false };

                    classByRef.TryGetValue(s.Id, out var cls);
                    var docType = cls?.DocType;
                    var confidence = cls?.Confidence ?? 0;
                    var reviewRequired = cls?.ReviewRequired ?? true;
                    var error = cls?.Error;

                    object[] fields = Array.Empty<object>();
                    if (!string.IsNullOrWhiteSpace(docType) && !string.IsNullOrWhiteSpace(boType))
                    {
                        var defs = await FieldsFor(docType!);
                        if (defs.Count > 0)
                        {
                            await gate.WaitAsync();
                            try
                            {
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                                var (vals, exErr) = await IdpClient.ExtractAsync(httpFactory, idp, token, s.SourceUrl!,
                                    defs.Select(d => (d.Id, d.Name)).ToList(), log, cts.Token);
                                if (exErr is not null) log.LogWarning("/api/idp/classify: extract {Name} -> {Error}", s.Name, exErr);
                                fields = vals.Select(v =>
                                {
                                    bool imp = true;
                                    foreach (var d in defs)
                                        if (string.Equals(d.Id, v.Id, StringComparison.OrdinalIgnoreCase)) { imp = d.Importable; break; }
                                    return (object)new { id = v.Id, name = v.Name, value = v.Value, confidence = Math.Round(v.Confidence, 4), reviewRequired = v.ReviewRequired, importable = imp };
                                }).ToArray();
                            }
                            finally { gate.Release(); }
                        }
                    }
                    return (object)new { name = s.Name, docType, confidence = Math.Round(confidence, 4), reviewRequired, error, fields, uploadable = configuredTypes == null || (docType != null && configuredTypes.Contains(docType)) };
                });
                results = await Task.WhenAll(tasks);
            }
            finally
            {
                // The files only need to be reachable during classification/extraction; drop the bytes afterwards.
                foreach (var id in stagedIds) idpFiles.TryRemove(id, out _);
            }

            return Results.Json(new { results });
        });

        // ---------- IDP metadata extraction (recognition -> extraction) for one file + a set of fields ----------
        // Returns the extracted value (+ confidence) per requested field so the panel can pre-fill the metadata inputs.
        app.MapPost("/api/idp/extract", async (HttpContext ctx, IdpExtractRequest req) =>
        {
            var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
            if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
                return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

            if (req.Attachment is null || string.IsNullOrWhiteSpace(req.Attachment.DataBase64))
                return Results.Json(new { error = "no_file", detail = "Attach a file to extract from." }, statusCode: 400);
            if (req.Fields is not { Length: > 0 })
                return Results.Json(new { error = "no_fields", detail = "No fields to extract." }, statusCode: 400);

            string? token;
            using (var tokCts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                token = await IdpClient.GetTokenAsync(httpFactory, idp, log, tokCts.Token);
            if (token is null)
                return Results.Json(new { error = "idp_not_connected", detail = "No valid IDP token and silent refresh failed. Run bff/idp-feature-test.ps1 to sign in to IDP, then retry." }, statusCode: 401);
            if (string.IsNullOrWhiteSpace(idp.PublicBaseUrl))
                return Results.Json(new { error = "idp_host_not_configured", detail = "Idp:PublicBaseUrl is not set." }, statusCode: 500);

            byte[] bytes;
            try { bytes = Convert.FromBase64String(req.Attachment.DataBase64); }
            catch { return Results.Json(new { error = "bad_file", detail = "Invalid file data." }, statusCode: 400); }

            var id = Guid.NewGuid().ToString("N");
            idpFiles[id] = (bytes, string.IsNullOrWhiteSpace(req.Attachment.Mime) ? "application/octet-stream" : req.Attachment.Mime!, string.IsNullOrWhiteSpace(req.Attachment.Name) ? "upload" : req.Attachment.Name!);
            var sourceUrl = $"{idp.PublicBaseUrl.TrimEnd('/')}/api/idp/file/{id}";

            try
            {
                var wanted = req.Fields!.Select(f => (f.Id, f.Name)).ToList();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var (fields, error) = await IdpClient.ExtractAsync(httpFactory, idp, token, sourceUrl, wanted, log, cts.Token);
                if (error is not null)
                    log.LogWarning("/api/idp/extract: {Error}", error);
                return Results.Json(new
                {
                    fields = fields.Select(f => new { id = f.Id, name = f.Name, value = f.Value, confidence = Math.Round(f.Confidence, 4), reviewRequired = f.ReviewRequired }),
                    error,
                });
            }
            finally
            {
                idpFiles.TryRemove(id, out _);
            }
        });
    }
}
