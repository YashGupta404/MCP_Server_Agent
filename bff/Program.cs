// UCEB Agent BFF (Backend-for-Frontend)
// -------------------------------------
// A small .NET minimal API that owns the OAuth secret and the user's IAM tokens.
//
// Flow (per-user login):
//   1. Extension opens  GET /auth/login?ext_redirect=<chromiumapp-url>  in launchWebAuthFlow.
//   2. We redirect to dev IAM (Authorization Code + PKCE).
//   3. IAM redirects back to  GET /auth/callback  with a code.
//   4. We exchange code + PKCE verifier + CLIENT SECRET for the user's tokens (kept server-side),
//      mint our own opaque session id, and redirect to the extension with #session=<id>.
//   5. Extension calls  POST /api/chat  with header  X-BFF-Session: <id>. We use the stored
//      user access token (refreshing if needed) to call the agent /invoke endpoint.
//
// The client secret and the user's IAM tokens NEVER leave this backend.

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Fixed HTTPS port so the redirect URI is stable. Uses the ASP.NET dev cert
// (run once:  dotnet dev-certs https --trust).
builder.WebHost.UseUrls("https://localhost:5010");

// Allow larger request bodies so base64-encoded file attachments (uploads from the plugin) fit.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64L * 1024 * 1024);

// User secrets are auto-loaded only in the Development environment. Load them explicitly so the
// client secret (Auth:ClientSecret) is available regardless of ASPNETCORE_ENVIRONMENT.
builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));
builder.Services.Configure<WorkdayOptions>(builder.Configuration.GetSection("Workday"));
builder.Services.Configure<IdpOptions>(builder.Configuration.GetSection("Idp"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SessionStore>();

// The extension origin is chrome-extension://<id>. We don't use cookies (only a custom
// header), so a permissive dev CORS policy is fine.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
var agent = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
var mcp = app.Services.GetRequiredService<IOptions<McpOptions>>().Value;
var workday = app.Services.GetRequiredService<IOptions<WorkdayOptions>>().Value;
var idp = app.Services.GetRequiredService<IOptions<IdpOptions>>().Value;
// Staged file bytes IDP's cloud service fetches via GET /api/idp/file/{id} (the classification sourceUrl).
var idpFiles = new ConcurrentDictionary<string, (byte[] Bytes, string Mime, string Name)>();
var sessions = app.Services.GetRequiredService<SessionStore>();
var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var workdayTokens = new WorkdayTokenCache();
// Caches the signed-in user's resolved Workday identity (the MCP is a single signed-in user, so process-wide).
object? selfWorkerIdentity = null;
var log = app.Logger;

// Returns the friendlyName of the ECM system the MCP is currently pointed at ("" if none). Used by the
// system-config endpoints; document listing/upload are backend-agnostic and don't branch on it.
async Task<string> ActiveSystemFriendlyNameAsync(CancellationToken ct)
{
    try
    {
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_active_system_configuration", new { }, log, ct);
        int a = text?.IndexOf('\'') ?? -1;
        int b = a >= 0 ? text!.IndexOf('\'', a + 1) : -1;
        return (a >= 0 && b > a) ? text!.Substring(a + 1, b - a - 1) : "";
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Could not resolve the active ECM system.");
        return "";
    }
}

// ---------- Exchange an auth code (from the extension's PKCE flow) for a session ----------
// The extension runs the interactive PKCE login (redirect_uri = its chromiumapp.org URL, which
// IAM accepts) and posts the resulting code + verifier here. We add the CLIENT SECRET and do the
// token exchange server-side, so the secret never touches the browser.
app.MapPost("/auth/exchange", async (ExchangeRequest req) =>
{
    if (string.IsNullOrEmpty(req.Code) || string.IsNullOrEmpty(req.CodeVerifier) || string.IsNullOrEmpty(req.RedirectUri))
        return Results.Json(new { error = "missing_fields" }, statusCode: 400);

    var http = httpFactory.CreateClient();
    var tokenResponse = await http.PostAsync(auth.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = req.Code,
        ["redirect_uri"] = req.RedirectUri,
        ["client_id"] = auth.ClientId,
        ["client_secret"] = auth.ClientSecret,
        ["code_verifier"] = req.CodeVerifier,
    }));

    var body = await tokenResponse.Content.ReadAsStringAsync();
    if (!tokenResponse.IsSuccessStatusCode)
    {
        log.LogError("Token exchange failed {Status}: {Body}", (int)tokenResponse.StatusCode, body);
        return Results.Json(new { error = "token_exchange_failed", status = (int)tokenResponse.StatusCode, detail = body },
            statusCode: 400);
    }

    var token = JsonSerializer.Deserialize<TokenResponse>(body)!;
    var sessionId = Pkce.RandomToken();
    sessions.Save(sessionId, token);
    log.LogInformation("/auth/exchange: session created");

    // DIAGNOSTIC: decode the issued access token and log the identity + entitlement claims so we can
    // diff which user (e.g. yash vs a-rizzo) actually receives the agent scopes/roles. This is what
    // decides Agent Builder access — a user missing `environment_authorization`/`hxp` in `scope` or
    // lacking the invoke role/permission is the one IAM rejects. Full token is NEVER logged.
    LogTokenEntitlements(token.access_token, token.scope);

    return Results.Json(new { session = sessionId });
});

// Decodes a JWT's payload (no signature check — diagnostics only) and logs the claims that gate
// Agent Builder access: subject/name, granted scope, and any roles/permissions/groups. Never logs
// the raw token. Safe to leave in: it only reads standard IAM claims from a token we already hold.
void LogTokenEntitlements(string? accessToken, string? grantedScopeFromResponse)
{
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        log.LogWarning("[auth-diag] no access_token to decode");
        return;
    }

    try
    {
        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            log.LogWarning("[auth-diag] access_token is not a JWT (parts={Parts})", parts.Length);
            return;
        }

        string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        JsonNode? payload = JsonNode.Parse(payloadJson);
        if (payload is null)
        {
            log.LogWarning("[auth-diag] could not parse JWT payload");
            return;
        }

        string? Str(string key) => payload[key]?.ToString();
        string Joined(string key) => payload[key] is JsonArray arr
            ? string.Join(",", arr.Select(n => n?.ToString()))
            : payload[key]?.ToString() ?? "(none)";

        // Prefer the scope inside the token; fall back to the token-endpoint response scope.
        string scope = payload["scope"]?.ToString() ?? grantedScopeFromResponse ?? "(none)";

        log.LogInformation(
            "[auth-diag] user sub={Sub} name={Name} preferred_username={User} email={Email}",
            Str("sub") ?? "(none)", Str("name") ?? "(none)",
            Str("preferred_username") ?? "(none)", Str("email") ?? "(none)");
        log.LogInformation("[auth-diag] granted scope: {Scope}", scope);
        log.LogInformation(
            "[auth-diag] roles={Roles} | permissions={Perms} | groups={Groups}",
            Joined("roles"), Joined("permissions"), Joined("groups"));
        log.LogInformation(
            "[auth-diag] aud={Aud} client_id={ClientId} hxp_authorization={Hxp}",
            Joined("aud"), Str("client_id") ?? "(none)",
            payload["hxp_authorization"]?.ToJsonString() ?? "(none)");

        // Explicit pass/fail on the scopes the Agent Orchestration API requires.
        bool hasHxp = scope.Split(' ').Contains("hxp");
        bool hasEnvAuth = scope.Split(' ').Contains("environment_authorization");
        log.LogInformation(
            "[auth-diag] AGENT SCOPES -> hxp={HasHxp} environment_authorization={HasEnvAuth} => {Verdict}",
            hasHxp, hasEnvAuth,
            hasHxp && hasEnvAuth ? "OK (agent scopes granted)" : "MISSING (agent access will be denied)");
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "[auth-diag] failed to decode/log token entitlements");
    }
}

// Base64Url -> bytes (JWT segments are base64url without padding).
static byte[] Base64UrlDecode(string input)
{
    string s = input.Replace('-', '+').Replace('_', '/');
    switch (s.Length % 4)
    {
        case 2: s += "=="; break;
        case 3: s += "="; break;
    }
    return Convert.FromBase64String(s);
}

// ---------- Proxy chat to the agent ----------
app.MapPost("/api/chat", async (HttpContext ctx, ChatRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out var session))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    var hasAttachments = req.Attachments is { Length: > 0 };
    if (string.IsNullOrWhiteSpace(req.Message) && !hasAttachments)
        return Results.Json(new { error = "empty_message" }, statusCode: 400);

    // Attached files are streamed to the MCP server's /staging/upload endpoint (BFF -> MCP directly,
    // not through the LLM and not via a shared filesystem). The MCP holds the bytes and returns a
    // short stagingId; we hand that id to the agent, which uploads by calling the upload_staged_file
    // tool. This is deployment-safe: it works even when the BFF and MCP run on different machines.
    var effectiveMessage = req.Message ?? string.Empty;
    if (hasAttachments)
    {
        if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        {
            log.LogError("/api/chat: attachment received but Mcp:BaseUrl / Mcp:ApiKey is not configured.");
            return Results.Json(new { error = "upload_not_configured", detail = "The MCP staging endpoint (Mcp:BaseUrl / Mcp:ApiKey) is not configured on the BFF." }, statusCode: 500);
        }

        var stagingUrl = $"{mcp.BaseUrl.TrimEnd('/')}/staging/upload";
        var stageHttp = httpFactory.CreateClient();
        stageHttp.Timeout = TimeSpan.FromSeconds(120);
        var stagedLines = new List<string>();
        foreach (var att in req.Attachments!)
        {
            if (att is null || string.IsNullOrWhiteSpace(att.DataBase64))
                continue;

            var originalName = string.IsNullOrWhiteSpace(att.Name) ? "upload" : Path.GetFileName(att.Name);
            using var stageReq = new HttpRequestMessage(HttpMethod.Post, stagingUrl);
            stageReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);
            stageReq.Content = new StringContent(
                JsonSerializer.Serialize(new { fileName = originalName, mime = att.Mime, dataBase64 = att.DataBase64 }),
                Encoding.UTF8, "application/json");

            try
            {
                var stageResp = await stageHttp.SendAsync(stageReq);
                var stageBody = await stageResp.Content.ReadAsStringAsync();
                if (!stageResp.IsSuccessStatusCode)
                {
                    log.LogError("/api/chat: staging failed for {Original} {Status}: {Body}", originalName, (int)stageResp.StatusCode, stageBody);
                    continue;
                }

                using var stageDoc = JsonDocument.Parse(stageBody);
                var stagingId = stageDoc.RootElement.TryGetProperty("stagingId", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(stagingId))
                {
                    log.LogWarning("/api/chat: staging response for {Original} had no stagingId: {Body}", originalName, stageBody);
                    continue;
                }

                stagedLines.Add($"- \"{originalName}\" (stagingId: {stagingId})");
                log.LogInformation("/api/chat: staged attachment {Original} on MCP as {StagingId}", originalName, stagingId);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "/api/chat: error staging attachment {Original} to MCP", originalName);
            }
        }

        if (stagedLines.Count > 0)
        {
            effectiveMessage = (effectiveMessage +
                "\n\n[The user attached the following file(s); their bytes are already STAGED on the MCP server. " +
                "To upload one, call the upload_staged_file tool with the stagingId shown below and set the document name to the quoted original name:\n" +
                string.Join("\n", stagedLines) + "]").Trim();
        }
        else
        {
            return Results.Json(new { error = "staging_failed", detail = "The attached file(s) could not be staged on the MCP server." }, statusCode: 502);
        }
    }

    // Steer the agent to the record's default-list query so "list this record's documents" returns the
    // SAME complete set the panel shows (every doc type, scoped by the business-object-context field),
    // instead of the LLM picking an arbitrary configured query and reporting a partial list.
    if (!string.IsNullOrWhiteSpace(req.BusinessObjectType) && !string.IsNullOrWhiteSpace(req.BusinessObjectId)
        && !string.IsNullOrWhiteSpace(mcp.BaseUrl) && !string.IsNullOrWhiteSpace(mcp.ApiKey))
    {
        try
        {
            using var dlqCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var dlq = await McpJsonRpc.GetDefaultListQueryAsync(httpFactory, mcp, req.BusinessObjectType!, log, dlqCts.Token);
            if (dlq is not null && !string.IsNullOrWhiteSpace(dlq.BoContextFieldId))
            {
                effectiveMessage = (effectiveMessage +
                    $"\n\n[To list the documents for this record ({req.BusinessObjectType} {req.BusinessObjectId}), call the " +
                    $"query_documents tool with businessObjectType=\"{req.BusinessObjectType}\", queryId=\"{dlq.Id}\", " +
                    $"filterFieldId=\"{dlq.BoContextFieldId}\", filterValue=\"{req.BusinessObjectId}\", " +
                    $"filterOperator=\"EqualsCaseInsensitive\", maxResults=500. Report EVERY returned document — do not " +
                    $"truncate and do not substitute a different query.]").Trim();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "/api/chat: default-list steering hint skipped for {Type}", req.BusinessObjectType);
        }

        // Give the agent the saved-query catalog so it can run a conversational SEARCH: offer the
        // queries, ask for a value, then execute query_documents with the right queryId/fieldId/operator.
        try
        {
            using var scCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var catalog = await McpJsonRpc.BuildSearchCatalogAsync(httpFactory, mcp, req.BusinessObjectType!, log, scCts.Token);
            if (!string.IsNullOrWhiteSpace(catalog))
            {
                effectiveMessage = (effectiveMessage +
                    $"\n\n[To SEARCH this record's documents (find by a field value, not list everything), the saved " +
                    $"queries available for {req.BusinessObjectType} are:\n{catalog}\n" +
                    $"When the user wants to search/find a document: present these query names and ask which one, then ask " +
                    $"for the value to search for. Execute with the query_documents tool: businessObjectType=\"{req.BusinessObjectType}\", " +
                    $"queryId=<chosen query's id>, filterFieldId=<that query's search fieldId>, filterValue=<the user's value>, " +
                    $"filterOperator=<that query's operator>, maxResults=25. Use ONLY the queryIds and fieldIds listed above — " +
                    $"never invent them — and report every returned document.]").Trim();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "/api/chat: search catalog hint skipped for {Type}", req.BusinessObjectType);
        }
    }

    var http = httpFactory.CreateClient();
    // The first message can trigger an interactive MCP->UCEB login (up to 120s), so let the
    // per-request CancellationTokenSource be the sole timeout instead of HttpClient's 100s default.
    http.Timeout = Timeout.InfiniteTimeSpan;

    // Refresh the access token if it's expired (or about to).
    if (DateTimeOffset.UtcNow >= session!.ExpiresAt && !string.IsNullOrEmpty(session.RefreshToken))
    {
        var refreshResp = await http.PostAsync(auth.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = session.RefreshToken!,
            ["client_id"] = auth.ClientId,
            ["client_secret"] = auth.ClientSecret,
        }));
        var refreshBody = await refreshResp.Content.ReadAsStringAsync();
        if (refreshResp.IsSuccessStatusCode)
        {
            session = sessions.Save(sessionId, JsonSerializer.Deserialize<TokenResponse>(refreshBody)!);
        }
        else
        {
            log.LogWarning("Refresh failed {Status}: {Body}", (int)refreshResp.StatusCode, refreshBody);
            return Results.Json(new { error = "session_expired" }, statusCode: 401);
        }
    }

    var invokeUrl = $"{agent.ApiBaseUrl}/v1/agents/{agent.AgentId}/versions/{agent.VersionId}/invoke";
    using var invokeReq = new HttpRequestMessage(HttpMethod.Post, invokeUrl);
    invokeReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session!.AccessToken}");
    invokeReq.Headers.TryAddWithoutValidation("X-Session-ID", string.IsNullOrEmpty(req.ConversationId) ? sessionId : req.ConversationId);
    // Some gateways/WAFs reject requests without a User-Agent (the browser normally supplies one).
    invokeReq.Headers.TryAddWithoutValidation("User-Agent", "UcebAgentBff/0.1");
    invokeReq.Headers.TryAddWithoutValidation("Accept", "application/json");
    invokeReq.Content = new StringContent(
        JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = effectiveMessage } } }),
        Encoding.UTF8, "application/json");

    HttpResponseMessage invokeResp;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        invokeResp = await http.SendAsync(invokeReq, cts.Token);
    }
    catch (OperationCanceledException)
    {
        log.LogError("Invoke timed out after 180s url={Url}", invokeUrl);
        return Results.Json(new { error = "invoke_timeout", status = 504, detail = "The agent did not respond within 180 seconds." }, statusCode: 504);
    }
    var invokeBody = await invokeResp.Content.ReadAsStringAsync();

    if (!invokeResp.IsSuccessStatusCode)
    {
        log.LogError("Invoke failed {Status} url={Url}: {Body}", (int)invokeResp.StatusCode, invokeUrl, invokeBody);
        return Results.Json(new { error = "invoke_failed", status = (int)invokeResp.StatusCode, detail = invokeBody },
            statusCode: (int)invokeResp.StatusCode);
    }

    return Results.Json(new { reply = ExtractReply(invokeBody) });
});

// ---------- Context-aware panel: list documents for the record on the browser screen ----------
// The extension detects the business object on the active tab (type + id) and posts it here. We call
// the MCP `list_documents` tool DIRECTLY (deterministic JSON-RPC, no LLM) so the panel loads fast,
// reusing the same Mcp:BaseUrl + Mcp:ApiKey the staging upload already uses.
app.MapPost("/api/context", async (HttpContext ctx, ContextRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(req.BusinessObjectId) || string.IsNullOrWhiteSpace(req.BusinessObjectType))
        return Results.Json(new { error = "missing_fields", detail = "businessObjectId and businessObjectType are required." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured", detail = "Mcp:BaseUrl / Mcp:ApiKey is not configured on the BFF." }, statusCode: 500);

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        // Default record listing: prefer the configured defaultListQuery (a single query keyed on the
        // business-object-context field) so EVERY doc type filed against this record shows with one
        // consistent, generic column set. Falls back to list_documents -> merged record listing for
        // objects that have no defaultListQuery (e.g. Workday).
        var dlq = await McpJsonRpc.GetDefaultListQueryAsync(httpFactory, mcp, req.BusinessObjectType, log, cts.Token);
        string text;
        object[] documents;
        string[]? columns = null;
        if (dlq is not null && !string.IsNullOrWhiteSpace(dlq.BoContextFieldId))
        {
            text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", new
            {
                businessObjectType = req.BusinessObjectType,
                queryId = dlq.Id,
                filterFieldId = dlq.BoContextFieldId,
                filterValue = req.BusinessObjectId,
                filterOperator = "EqualsCaseInsensitive",
                maxResults = 500,
            }, log, cts.Token);
            documents = McpJsonRpc.ParseDocumentList(text);
            if (dlq.Columns.Length > 0) columns = dlq.Columns;
        }
        else
        {
            text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_documents", new
            {
                businessObjectId = req.BusinessObjectId,
                businessObjectType = req.BusinessObjectType,
                onlyMine = req.OnlyMine ?? false,
            }, log, cts.Token);

            documents = McpJsonRpc.ParseDocumentList(text);
            if (documents.Length == 0)
            {
                text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", new
                {
                    businessObjectType = req.BusinessObjectType,
                    businessObjectId = req.BusinessObjectId,
                }, log, cts.Token);
                documents = McpJsonRpc.ParseDocumentList(text);
            }
        }
        log.LogInformation("[/api/context] type={Type} id={Id} -> {Count} document(s)",
            req.BusinessObjectType, req.BusinessObjectId, documents.Length);
        return Results.Json(new
        {
            businessObjectId = req.BusinessObjectId,
            businessObjectType = req.BusinessObjectType,
            documents,
            columns,
            raw = text,
        });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/context failed for {Type}/{Id}", req.BusinessObjectType, req.BusinessObjectId);
        return Results.Json(new { error = "context_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- List the available UCEB document (content) types for the upload dropdown ----------
// Deterministic (BFF -> MCP list_document_types); the panel populates its doc-type <select> from this.
app.MapGet("/api/doctypes", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types", new { }, log, cts.Token);
        var types = McpJsonRpc.ParseDocumentTypes(text);
        return Results.Json(new { types, raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/doctypes failed");
        return Results.Json(new { error = "doctypes_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- Doc-type metadata fields: user-editable fields for the upload form ----------
// Powers the upload section's dynamic metadata inputs. Workday reads the type's capture default
// attributes; Salesforce/CIC reads the type's configured field labels from the solution config
// (excluding the record-scoping / auto-mapped fields).
app.MapGet("/api/doctype-fields", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    var docType = ctx.Request.Query["docType"].ToString();
    var boType = ctx.Request.Query["businessObjectType"].ToString();
    var boId = ctx.Request.Query["businessObjectId"].ToString();
    var workday = string.Equals(ctx.Request.Query["workday"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    if (string.IsNullOrWhiteSpace(docType))
        return Results.Json(new { error = "missing_docType", detail = "docType is required." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fields = workday
            ? await McpJsonRpc.GetWorkdayDocTypeFieldsAsync(httpFactory, mcp, docType,
                string.IsNullOrWhiteSpace(boType) ? "employee" : boType, boId, log, cts.Token)
            : await McpJsonRpc.GetCicDocTypeFieldsAsync(httpFactory, mcp, docType, boType, log, cts.Token);
        return Results.Json(new { fields });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/doctype-fields failed for {DocType}", docType);
        return Results.Json(new { error = "doctype_fields_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- Native query model: list queries, a query's metadata, and execute (server-side search) ----------
// Mirrors the native HFS/HFW query mechanism: list_queries -> get_query_metadata (searchable inputs + result
// columns) -> query_documents (execute with a keyword filter). Returns the raw tool JSON for the extension to
// render dynamic columns + a validated keyword-search form.
app.MapGet("/api/queries", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);
    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);
    var boType = ctx.Request.Query["businessObjectType"].ToString();
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Show ONLY the queries configured for this business object (solution config queryConfig[busObject]),
        // matching native HFS — not the system-wide list. Falls back to list_queries when unspecified.
        if (!string.IsNullOrWhiteSpace(boType))
        {
            var configured = await McpJsonRpc.GetQueriesForBusinessObjectAsync(httpFactory, mcp, boType, log, cts.Token);
            if (configured is not null)
                return Results.Json(new { queries = configured });
        }
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_queries", new { }, log, cts.Token);
        return Results.Json(new { raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/queries failed");
        return Results.Json(new { error = "queries_failed", detail = ex.Message }, statusCode: 502);
    }
});

app.MapGet("/api/query-metadata", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);
    var queryId = ctx.Request.Query["queryId"].ToString();
    if (string.IsNullOrWhiteSpace(queryId))
        return Results.Json(new { error = "missing_queryId", detail = "queryId is required." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_query_metadata", new { queryId }, log, cts.Token);
        return Results.Json(new { raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/query-metadata failed for {QueryId}", queryId);
        return Results.Json(new { error = "query_metadata_failed", detail = ex.Message }, statusCode: 502);
    }
});

// Executes a query server-side (keyword search). Body: { businessObjectType, queryId?, businessObjectId?,
// filterFieldId?, filterValue?, filterOperator? }. Returns the parsed documents (same shape as /api/context).
app.MapPost("/api/query-execute", async (HttpContext ctx, QueryExecuteRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);
    if (string.IsNullOrWhiteSpace(req.BusinessObjectType))
        return Results.Json(new { error = "missing_fields", detail = "businessObjectType is required." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var args = new Dictionary<string, object?>
        {
            ["businessObjectType"] = req.BusinessObjectType,
            ["businessObjectId"] = req.BusinessObjectId,
            ["queryId"] = req.QueryId,
            ["filterFieldId"] = req.FilterFieldId,
            ["filterValue"] = req.FilterValue,
            ["filterOperator"] = string.IsNullOrWhiteSpace(req.FilterOperator) ? "Contains" : req.FilterOperator,
        };
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", args, log, cts.Token);
        var documents = McpJsonRpc.ParseDocumentList(text);
        return Results.Json(new { documents, raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/query-execute failed");
        return Results.Json(new { error = "query_execute_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- System configurations: list the registered ECM systems and set the active one ----------
// The plugin's onboarding step lets the signed-in user choose which ECM system (CIC / OnBase / …) to
// connect to. GET returns the registry; POST sets the active systemFriendlyName on the MCP so every
// subsequent document call (list / upload / doc-types) resolves to that system. Deterministic (no LLM).
app.MapGet("/api/system-configs", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_system_configurations", new { }, log, cts.Token);
        var active = await ActiveSystemFriendlyNameAsync(cts.Token);
        var configs = McpJsonRpc.ParseSystemConfigs(text, active);
        return Results.Json(new { configs, active, raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/system-configs failed");
        return Results.Json(new { error = "system_configs_failed", detail = ex.Message }, statusCode: 502);
    }
});

app.MapPost("/api/system-config", async (HttpContext ctx, SystemConfigRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(req.FriendlyName))
        return Results.Json(new { error = "missing_fields", detail = "friendlyName is required." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "set_active_system_configuration",
            new { friendlyName = req.FriendlyName }, log, cts.Token);
        var active = await ActiveSystemFriendlyNameAsync(cts.Token);
        log.LogInformation("/api/system-config: active system set to '{Active}'", active);
        return Results.Json(new { active, raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/system-config failed for {Name}", req.FriendlyName);
        return Results.Json(new { error = "set_system_config_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- Direct upload: attach a file to the record on the browser screen ----------
// The extension's upload section posts the file bytes + the target record (auto-filled from the
// detected context) + the chosen document type here. We stage the bytes on the MCP server and then
// call the `upload_staged_file` tool DIRECTLY (deterministic JSON-RPC, no LLM) — this is the same
// staging path /api/chat uses, but without going through the agent.
app.MapPost("/api/upload", async (HttpContext ctx, UploadRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(req.BusinessObjectId) || string.IsNullOrWhiteSpace(req.BusinessObjectType))
        return Results.Json(new { error = "missing_fields", detail = "businessObjectId and businessObjectType are required." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(req.EcmContentTypeName))
        return Results.Json(new { error = "missing_docType", detail = "A document type is required." }, statusCode: 400);

    if (req.Attachments is not { Length: > 0 })
        return Results.Json(new { error = "no_file", detail = "Attach at least one file to upload." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured", detail = "Mcp:BaseUrl / Mcp:ApiKey is not configured on the BFF." }, statusCode: 500);

    var stagingUrl = $"{mcp.BaseUrl.TrimEnd('/')}/staging/upload";
    var stageHttp = httpFactory.CreateClient();
    stageHttp.Timeout = TimeSpan.FromSeconds(120);

    var uploaded = new List<string>();
    var errors = new List<string>();

    foreach (var att in req.Attachments!)
    {
        if (att is null || string.IsNullOrWhiteSpace(att.DataBase64))
            continue;

        var originalName = string.IsNullOrWhiteSpace(att.Name) ? "upload" : Path.GetFileName(att.Name);
        // The content platform validates the upload by its filename EXTENSION and accepts "jpg" but not
        // "jpeg" (both are image/jpeg) -> normalize so a valid JPEG named *.jpeg still uploads.
        var stagedName = NormalizeUploadExtension(originalName);

        // 1) stage the bytes on the MCP server
        string? stagingId = null;
        try
        {
            using var stageReq = new HttpRequestMessage(HttpMethod.Post, stagingUrl);
            stageReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);
            stageReq.Content = new StringContent(
                JsonSerializer.Serialize(new { fileName = stagedName, mime = att.Mime, dataBase64 = att.DataBase64 }),
                Encoding.UTF8, "application/json");
            var stageResp = await stageHttp.SendAsync(stageReq);
            var stageBody = await stageResp.Content.ReadAsStringAsync();
            if (!stageResp.IsSuccessStatusCode)
            {
                log.LogError("/api/upload: staging failed for {Original} {Status}: {Body}", originalName, (int)stageResp.StatusCode, stageBody);
                errors.Add($"{originalName}: staging failed ({(int)stageResp.StatusCode})");
                continue;
            }
            using var stageDoc = JsonDocument.Parse(stageBody);
            stagingId = stageDoc.RootElement.TryGetProperty("stagingId", out var idEl) ? idEl.GetString() : null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/upload: error staging {Original}", originalName);
            errors.Add($"{originalName}: {ex.Message}");
            continue;
        }

        if (string.IsNullOrEmpty(stagingId))
        {
            errors.Add($"{originalName}: no stagingId returned");
            continue;
        }

        // 2) call upload_staged_file directly
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            // The upload tool derives any backend-specific metadata (e.g. OnBase keywords) from the active
            // system's config itself, so we just pass the record + content type.
            var uploadArgs = new
            {
                stagingId,
                businessObjectId = req.BusinessObjectId,
                businessObjectType = req.BusinessObjectType,
                ecmContentTypeName = req.EcmContentTypeName,
                documentName = stagedName,
                extraAttributesJson = (req.AdditionalAttributes is { Length: > 0 })
                    ? JsonSerializer.Serialize(req.AdditionalAttributes)
                    : null,
            };
            var (text, isError) = await McpJsonRpc.CallToolWithStatusAsync(httpFactory, mcp, "upload_staged_file", uploadArgs, log, cts.Token);

            // The MCP call can succeed at the JSON-RPC level while the tool itself reports a failure
            // (either via isError or an error message in the text). Only count a REAL success.
            if (isError || UploadTextIndicatesFailure(text))
            {
                var detail = string.IsNullOrWhiteSpace(text) ? "the upload tool reported an error" : text.Trim();
                log.LogError("/api/upload: upload_staged_file reported failure for {Original}: {Result}", originalName, text);
                errors.Add($"{originalName}: {detail}");
            }
            else
            {
                uploaded.Add(originalName);
                log.LogInformation("/api/upload: uploaded {Original} to {Type}/{Id} as {DocType}: {Result}",
                    originalName, req.BusinessObjectType, req.BusinessObjectId, req.EcmContentTypeName, text);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/upload: upload_staged_file failed for {Original}", originalName);
            errors.Add($"{originalName}: {ex.Message}");
        }
    }

    if (uploaded.Count == 0)
        return Results.Json(new { error = "upload_failed", detail = string.Join("; ", errors) }, statusCode: 502);

    return Results.Json(new { uploaded, errors });
});

// ---------- Capture a document into a Workday LOB record (deterministic; BFF -> MCP capture_document) ----------
// Mirrors /api/upload but uses the Workday single-POST capture path (/bow/core/documents) instead of the
// Salesforce/CIC 3-step attach. Stages each file's bytes on the MCP server, then calls the capture_document
// tool with the documentType + business-object attributes that tie the document to the record.
app.MapPost("/api/capture", async (HttpContext ctx, CaptureRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(req.DocumentTypeId))
        return Results.Json(new { error = "missing_docType", detail = "A documentTypeId is required." }, statusCode: 400);

    if (req.Attachments is not { Length: > 0 })
        return Results.Json(new { error = "no_file", detail = "Attach at least one file to capture." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured", detail = "Mcp:BaseUrl / Mcp:ApiKey is not configured on the BFF." }, statusCode: 500);

    var businessObjectType = string.IsNullOrWhiteSpace(req.BusinessObjectType) ? "employee" : req.BusinessObjectType;

    // Resolve the record-identifying business-object attributes.
    //  - If the caller supplied them explicitly, pass them through verbatim.
    //  - Otherwise, when a record id is provided, auto-fetch the document type's default capture
    //    attributes (which carry the real Workday field ids) so a single "Upload" click can file
    //    into Workday without the UI needing to know the doc-type's attribute schema.
    string attributesJson;
    if (req.BusinessObjectAttributes is { Length: > 0 } attrs)
    {
        attributesJson = JsonSerializer.Serialize(attrs);
    }
    else if (!string.IsNullOrWhiteSpace(req.BusinessObjectId))
    {
        attributesJson = "[]";
        try
        {
            var singleValued = JsonSerializer.Serialize(new[]
            {
                new { name = "businessObjectId", value = req.BusinessObjectId }
            });
            using var attrCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var (attrText, attrIsError) = await McpJsonRpc.CallToolWithStatusAsync(httpFactory, mcp, "get_capture_default_attributes", new
            {
                documentTypeId = req.DocumentTypeId,
                businessObjectType,
                singleValuedBusinessObjectAttributesJson = singleValued,
            }, log, attrCts.Token);

            string? dataArrayJson = null;
            if (!attrIsError && !string.IsNullOrWhiteSpace(attrText))
            {
                var braceIdx = attrText.IndexOf('{');
                if (braceIdx >= 0)
                {
                    try
                    {
                        var attrNode = JsonNode.Parse(attrText[braceIdx..]);
                        if (attrNode?["data"] is JsonArray dataArr)
                        {
                            // get_capture_default_attributes returns the full attribute *schema* but with
                            // every value null — the singleValuedBusinessObjectAttributes are not mapped
                            // onto the returned ids. Inject the record's id onto the businessObjectId
                            // attribute so the capture actually ties to the worker; without it UCEB NREs
                            // (Object reference not set) before the document is stored.
                            foreach (var item in dataArr)
                            {
                                if (item is not JsonObject obj) continue;
                                var id = obj["id"]?.GetValue<string>();
                                var name = obj["name"]?.GetValue<string>();
                                if ((id?.EndsWith("businessObjectId", StringComparison.OrdinalIgnoreCase) ?? false)
                                    || (name?.EndsWith("businessObjectId", StringComparison.OrdinalIgnoreCase) ?? false))
                                {
                                    obj["value"] = req.BusinessObjectId;
                                }
                            }

                            // Inject any user-entered metadata values onto their matching attribute
                            // (matched by field id or name), so the upload form's extra fields are filed.
                            if (req.AdditionalAttributes is { Length: > 0 } userAttrs)
                            {
                                foreach (var ua in userAttrs)
                                {
                                    var uName = ua.TryGetProperty("name", out var un) ? un.GetString()
                                        : (ua.TryGetProperty("id", out var ui2) ? ui2.GetString() : null);
                                    if (string.IsNullOrWhiteSpace(uName)) continue;
                                    var uVal = ua.TryGetProperty("value", out var uv) ? uv.ToString() : null;
                                    foreach (var it in dataArr)
                                    {
                                        if (it is not JsonObject o2) continue;
                                        var iid = o2["id"]?.GetValue<string>();
                                        var inm = o2["name"]?.GetValue<string>();
                                        if (string.Equals(iid, uName, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(inm, uName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            o2["value"] = uVal;
                                        }
                                    }
                                }
                            }
                            dataArrayJson = dataArr.ToJsonString();
                        }
                    }
                    catch (JsonException) { }
                }
            }

            if (dataArrayJson is not null)
                attributesJson = dataArrayJson;
            else
                log.LogWarning("/api/capture: could not resolve default capture attributes for {DocType}; proceeding with []. Tool said: {Text}", req.DocumentTypeId, attrText);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/capture: error resolving default capture attributes for {DocType}", req.DocumentTypeId);
        }
    }
    else
    {
        attributesJson = "[]";
    }

    var stagingUrl = $"{mcp.BaseUrl.TrimEnd('/')}/staging/upload";
    var stageHttp = httpFactory.CreateClient();
    stageHttp.Timeout = TimeSpan.FromSeconds(120);

    var captured = new List<string>();
    var errors = new List<string>();

    foreach (var att in req.Attachments!)
    {
        if (att is null || string.IsNullOrWhiteSpace(att.DataBase64))
            continue;

        var originalName = string.IsNullOrWhiteSpace(att.Name) ? "upload" : Path.GetFileName(att.Name);
        var stagedName = NormalizeUploadExtension(originalName);

        // 1) stage the bytes on the MCP server
        string? stagingId = null;
        try
        {
            using var stageReq = new HttpRequestMessage(HttpMethod.Post, stagingUrl);
            stageReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);
            stageReq.Content = new StringContent(
                JsonSerializer.Serialize(new { fileName = stagedName, mime = att.Mime, dataBase64 = att.DataBase64 }),
                Encoding.UTF8, "application/json");
            var stageResp = await stageHttp.SendAsync(stageReq);
            var stageBody = await stageResp.Content.ReadAsStringAsync();
            if (!stageResp.IsSuccessStatusCode)
            {
                log.LogError("/api/capture: staging failed for {Original} {Status}: {Body}", originalName, (int)stageResp.StatusCode, stageBody);
                errors.Add($"{originalName}: staging failed ({(int)stageResp.StatusCode})");
                continue;
            }
            using var stageDoc = JsonDocument.Parse(stageBody);
            stagingId = stageDoc.RootElement.TryGetProperty("stagingId", out var idEl) ? idEl.GetString() : null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/capture: error staging {Original}", originalName);
            errors.Add($"{originalName}: {ex.Message}");
            continue;
        }

        if (string.IsNullOrEmpty(stagingId))
        {
            errors.Add($"{originalName}: no stagingId returned");
            continue;
        }

        // 2) call capture_document
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var (text, isError) = await McpJsonRpc.CallToolWithStatusAsync(httpFactory, mcp, "capture_document", new
            {
                stagingId,
                documentTypeId = req.DocumentTypeId,
                businessObjectAttributesJson = attributesJson,
                businessObjectType,
                documentId = req.DocumentId,
                createNewVersion = req.CreateNewVersion ?? false,
                documentName = stagedName,
            }, log, cts.Token);

            if (isError || CaptureTextIndicatesFailure(text))
            {
                var detail = string.IsNullOrWhiteSpace(text) ? "the capture tool reported an error" : text.Trim();
                log.LogError("/api/capture: capture_document reported failure for {Original}: {Result}", originalName, text);
                errors.Add($"{originalName}: {detail}");
            }
            else
            {
                captured.Add(originalName);
                log.LogInformation("/api/capture: captured {Original} as {DocType} on {BoType}: {Result}",
                    originalName, req.DocumentTypeId, businessObjectType, text);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/capture: capture_document failed for {Original}", originalName);
            errors.Add($"{originalName}: {ex.Message}");
        }
    }

    if (captured.Count == 0)
        return Results.Json(new { error = "capture_failed", detail = string.Join("; ", errors) }, statusCode: 502);

    return Results.Json(new { captured, errors });
});

// ---------- IDP auto-classification + metadata extraction (see Idp/IdpEndpoints.cs) ----------
// Routes: GET /api/idp/file/{id}, GET /api/idp/status, POST /api/idp/classify, POST /api/idp/extract.
app.MapIdpEndpoints(idp, idpFiles, sessions, httpFactory, mcp, log);

// ---------- Open a document in the Hyland viewer (deterministic; returns the viewer URL) ----------
app.MapPost("/api/viewer", async (HttpContext ctx, ViewerRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(req.DocId))
        return Results.Json(new { error = "missing_docId" }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "open_document_in_viewer",
            new { documentId = req.DocId }, log, cts.Token);
        return Results.Json(new { url = McpJsonRpc.ExtractUrl(text), raw = text });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/viewer failed for {DocId}", req.DocId);
        return Results.Json(new { error = "viewer_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- Stream a document PREVIEW image so the plugin can render it INSIDE the panel ----------
// The extension GETs this with its session; we proxy to the MCP's /documents/{id}/preview endpoint
// (X-Api-Key) and stream the rendition image bytes straight back. No LLM, no viewer SPA, no iframe —
// the extension turns the bytes into a blob: URL and shows them in an <img>, which sidesteps the
// third-party-cookie / frame-ancestors problems of embedding the Studio viewer.
app.MapGet("/api/document/preview", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    var docId = ctx.Request.Query["docId"].ToString();
    if (string.IsNullOrWhiteSpace(docId))
        return Results.Json(new { error = "missing_docId" }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    var renditionType = ctx.Request.Query["renditionType"].ToString();
    if (string.IsNullOrWhiteSpace(renditionType)) renditionType = "preview";
    var pageNo = ctx.Request.Query["pageNo"].ToString();
    if (string.IsNullOrWhiteSpace(pageNo)) pageNo = "1";

    try
    {
        var http = httpFactory.CreateClient();
        var url = $"{mcp.BaseUrl.TrimEnd('/')}/documents/{Uri.EscapeDataString(docId)}/preview" +
                  $"?renditionType={Uri.EscapeDataString(renditionType)}&pageNo={Uri.EscapeDataString(pageNo)}";
        using var previewReq = new HttpRequestMessage(HttpMethod.Get, url);
        previewReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var mcpResp = await http.SendAsync(previewReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (!mcpResp.IsSuccessStatusCode)
        {
            var errBody = await mcpResp.Content.ReadAsStringAsync(cts.Token);
            return Results.Json(
                new { error = "preview_failed", status = (int)mcpResp.StatusCode, detail = errBody },
                statusCode: (int)mcpResp.StatusCode);
        }

        var bytes = await mcpResp.Content.ReadAsByteArrayAsync(cts.Token);
        var contentType = mcpResp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return Results.File(bytes, contentType);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/document/preview failed for {DocId}", docId);
        return Results.Json(new { error = "preview_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- Raw document CONTENT bytes for the PDF.js in-panel viewer ----------
// Salesforce: MCP downloads the actual file bytes → we stream them back; the extension renders with PDF.js.
// Workday: MCP has no download endpoint → it returns JSON { workday: true, viewerUrl } which we forward;
// the extension opens the URL in a first-party window/tab where the session cookie IS sent.
app.MapGet("/api/document/content", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    var docId = ctx.Request.Query["docId"].ToString();
    if (string.IsNullOrWhiteSpace(docId))
        return Results.Json(new { error = "missing_docId" }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    try
    {
        var http = httpFactory.CreateClient();
        var url = $"{mcp.BaseUrl.TrimEnd('/')}/documents/{Uri.EscapeDataString(docId)}/content";
        using var contentReq = new HttpRequestMessage(HttpMethod.Get, url);
        contentReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var mcpResp = await http.SendAsync(contentReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (!mcpResp.IsSuccessStatusCode)
        {
            var errBody = await mcpResp.Content.ReadAsStringAsync(cts.Token);
            return Results.Json(
                new { error = "content_failed", status = (int)mcpResp.StatusCode, detail = errBody },
                statusCode: (int)mcpResp.StatusCode);
        }

        var contentType = mcpResp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

        // Workday path: MCP returns JSON with viewerUrl — forward it as-is for the extension.
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await mcpResp.Content.ReadAsStringAsync(cts.Token);
            return Results.Content(json, "application/json");
        }

        // Salesforce path: raw file bytes — stream them back.
        var bytes = await mcpResp.Content.ReadAsByteArrayAsync(cts.Token);
        return Results.File(bytes, contentType);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/document/content failed for {DocId}", docId);
        return Results.Json(new { error = "content_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- The SIGNED-IN user's own Workday identity ("self") ----------
// Panel + chatbot default document scope: the logged-in user's OWN records, regardless of which employee
// profile page is open. Resolves the MCP-signed-in user's name (IAM userinfo, via get_my_identity) to a
// Workday WID through the Staffing search. Cached process-wide (the MCP is a single signed-in user).
app.MapGet("/api/me", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (selfWorkerIdentity is not null)
        return Results.Json(selfWorkerIdentity);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured" }, statusCode: 500);

    // 1) Ask the MCP who is signed in (IAM userinfo -> display name).
    string? name = null;
    try
    {
        using var idCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (text, isError) = await McpJsonRpc.CallToolWithStatusAsync(httpFactory, mcp, "get_my_identity", new { }, log, idCts.Token);
        if (!isError && !string.IsNullOrWhiteSpace(text))
        {
            var uiIdx = text.IndexOf("userinfo:", StringComparison.OrdinalIgnoreCase);
            var braceIdx = uiIdx >= 0 ? text.IndexOf('{', uiIdx) : -1;
            if (braceIdx >= 0)
            {
                try { name = JsonNode.Parse(text[braceIdx..])?["name"]?.GetValue<string>(); }
                catch (JsonException) { }
            }
        }
    }
    catch (Exception ex) { log.LogError(ex, "/api/me: get_my_identity failed"); }

    if (string.IsNullOrWhiteSpace(name))
        return Results.Json(new { error = "identity_unresolved", detail = "Could not read the signed-in user's name from IAM userinfo." }, statusCode: 502);

    // 2) Resolve that name to a Workday WID via the Staffing search (exactly-one match = confident).
    var (token, tokenErr) = await GetWorkdayAccessTokenAsync(ctx);
    if (token is null)
        return Results.Json(new { error = "workday_auth_failed", detail = tokenErr }, statusCode: 502);

    try
    {
        var http = httpFactory.CreateClient();
        var url = $"{workday.StaffingBaseUrl.TrimEnd('/')}/workers?search={Uri.EscapeDataString(name)}&limit=20";
        using var reqMsg = new HttpRequestMessage(HttpMethod.Get, url);
        reqMsg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        reqMsg.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await http.SendAsync(reqMsg);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { error = "workday_search_failed", detail = body }, statusCode: 502);

        var matches = new List<WorkerMatch>();
        var root = JsonNode.Parse(body);
        if (root?["data"] is JsonArray dataArr)
        {
            foreach (var item in dataArr)
            {
                if (item is not JsonObject o) continue;
                matches.Add(new WorkerMatch(
                    Wid: (string?)o["id"],
                    Name: (string?)o["descriptor"],
                    EmployeeId: (string?)o["workerId"],
                    BusinessTitle: (string?)o["primaryJob"]?["businessTitle"],
                    SupervisoryOrganization: (string?)o["primaryJob"]?["supervisoryOrganization"]?["descriptor"]));
            }
        }

        var self = matches.Count == 1 ? matches[0] : null;
        if (self?.Wid is null)
        {
            log.LogWarning("/api/me: name '{Name}' resolved to {Count} workers (not a unique match).", name, matches.Count);
            return Results.Json(new { error = "identity_ambiguous", name, total = matches.Count, matches }, statusCode: 409);
        }

        var result = new { wid = self.Wid, name = self.Name, employeeId = self.EmployeeId };
        selfWorkerIdentity = result;
        log.LogInformation("/api/me: resolved signed-in user '{Name}' -> WID {Wid} (employeeId {EmployeeId}).", self.Name, self.Wid, self.EmployeeId);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/me failed");
        return Results.Json(new { error = "me_failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---------- Resolve a Workday worker WID from a name or Employee ID (Workday Staffing REST API) ----------
// Calls GET {StaffingBaseUrl}/workers?search={q}. The search matches by worker NAME or worker ID
// (Employee ID), case-insensitive. Each returned worker carries id (=WID), workerId (=Employee ID) and
// descriptor (=name), so the caller can auto-fill the businessObjectId for capture/upload instead of
// hunting for the WID in Workday. Auth uses a separate Workday OAuth client (NOT the UCEB token); for
// quick testing you can pass a raw bearer via the X-Workday-Token header.
app.MapGet("/api/worker/resolve", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    var q = ctx.Request.Query["q"].ToString();
    if (string.IsNullOrWhiteSpace(q)) q = ctx.Request.Query["search"].ToString();
    if (string.IsNullOrWhiteSpace(q)) q = ctx.Request.Query["employeeId"].ToString();
    if (string.IsNullOrWhiteSpace(q)) q = ctx.Request.Query["name"].ToString();
    if (string.IsNullOrWhiteSpace(q))
        return Results.Json(new { error = "missing_query", detail = "Provide ?q=<worker name or employee id>." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(workday.StaffingBaseUrl))
        return Results.Json(new { error = "workday_not_configured", detail = "Set Workday:StaffingBaseUrl in configuration." }, statusCode: 500);

    var (token, tokenErr) = await GetWorkdayAccessTokenAsync(ctx);
    if (token is null)
        return Results.Json(new { error = "workday_auth_failed", detail = tokenErr }, statusCode: 502);

    var url = $"{workday.StaffingBaseUrl.TrimEnd('/')}/workers?search={Uri.EscapeDataString(q)}&limit=20";
    try
    {
        var http = httpFactory.CreateClient();
        using var reqMsg = new HttpRequestMessage(HttpMethod.Get, url);
        reqMsg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        reqMsg.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await http.SendAsync(reqMsg);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            log.LogError("/api/worker/resolve: staffing search failed {Status}: {Body}", (int)resp.StatusCode, body);
            return Results.Json(new { error = "workday_search_failed", status = (int)resp.StatusCode, detail = body }, statusCode: 502);
        }

        var matches = new List<WorkerMatch>();
        var root = JsonNode.Parse(body);
        if (root?["data"] is JsonArray dataArr)
        {
            foreach (var item in dataArr)
            {
                if (item is not JsonObject o) continue;
                matches.Add(new WorkerMatch(
                    Wid: (string?)o["id"],
                    Name: (string?)o["descriptor"],
                    EmployeeId: (string?)o["workerId"],
                    BusinessTitle: (string?)o["primaryJob"]?["businessTitle"],
                    SupervisoryOrganization: (string?)o["primaryJob"]?["supervisoryOrganization"]?["descriptor"]));
            }
        }

        // Prefer an exact Employee ID hit (clean 1:1); otherwise a lone match; else null (ambiguous).
        var exact = matches.FirstOrDefault(m => string.Equals(m.EmployeeId, q, StringComparison.OrdinalIgnoreCase));
        string? wid = exact?.Wid ?? (matches.Count == 1 ? matches[0].Wid : null);

        return Results.Json(new { query = q, total = matches.Count, wid, matches });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/worker/resolve failed for {Query}", q);
        return Results.Json(new { error = "resolve_failed", detail = ex.Message }, statusCode: 502);
    }
});

// Acquires a Workday OAuth bearer token for the Staffing API. Order: (1) an X-Workday-Token header
// override (handy for testing with a token from the REST API Explorer), (2) a cached token, (3) a
// fresh token via refresh_token grant (when Workday:RefreshToken is set) or client_credentials.
async Task<(string? token, string? error)> GetWorkdayAccessTokenAsync(HttpContext ctx)
{
    var manual = ctx.Request.Headers["X-Workday-Token"].ToString();
    if (!string.IsNullOrWhiteSpace(manual)) return (manual, null);

    if (workdayTokens.TryGet(out var cached)) return (cached, null);

    if (string.IsNullOrWhiteSpace(workday.TokenUrl) || string.IsNullOrWhiteSpace(workday.ClientId))
        return (null, "Workday API client is not configured. Set Workday:TokenUrl, Workday:ClientId, Workday:ClientSecret (and optionally Workday:RefreshToken/Workday:Scope) in user-secrets, or pass an X-Workday-Token header.");

    var form = new Dictionary<string, string>();
    if (!string.IsNullOrWhiteSpace(workday.RefreshToken))
    {
        form["grant_type"] = "refresh_token";
        form["refresh_token"] = workday.RefreshToken;
    }
    else
    {
        form["grant_type"] = "client_credentials";
    }
    if (!string.IsNullOrWhiteSpace(workday.Scope)) form["scope"] = workday.Scope;

    try
    {
        var http = httpFactory.CreateClient();
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, workday.TokenUrl);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{workday.ClientId}:{workday.ClientSecret}"));
        reqMsg.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        reqMsg.Content = new FormUrlEncodedContent(form);
        using var resp = await http.SendAsync(reqMsg);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return (null, $"Workday token request failed ({(int)resp.StatusCode}): {body}");
        var tok = JsonSerializer.Deserialize<TokenResponse>(body);
        if (tok is null || string.IsNullOrWhiteSpace(tok.access_token))
            return (null, "Workday token response had no access_token.");
        workdayTokens.Set(tok.access_token, tok.expires_in);
        return (tok.access_token, null);
    }
    catch (Exception ex)
    {
        return (null, $"Workday token request errored: {ex.Message}");
    }
}

// ---------- helpers: status / logout ----------
app.MapGet("/auth/status", (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    return Results.Json(new { authenticated = !string.IsNullOrEmpty(sessionId) && sessions.TryGet(sessionId, out _) });
});

app.MapPost("/auth/logout", (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (!string.IsNullOrEmpty(sessionId)) sessions.Remove(sessionId);
    return Results.Ok(new { ok = true });
});

app.Run();

// ================= helpers =================

// The content platform validates uploads by filename extension and rejects some aliases even though the
// bytes/mime are valid (it accepts "jpg" but not "jpeg", "tiff" but not "tif"). Normalize known-problem
// extensions to the accepted spelling so a correctly-formed file still uploads. Byte content is unchanged.
static string NormalizeUploadExtension(string fileName)
{
    if (string.IsNullOrWhiteSpace(fileName)) return fileName;
    var ext = Path.GetExtension(fileName);
    if (string.IsNullOrEmpty(ext)) return fileName;
    var replacement = ext.ToLowerInvariant() switch
    {
        ".jpeg" => ".jpg",
        ".tif" => ".tiff",
        _ => null,
    };
    if (replacement is null) return fileName;
    return fileName[..^ext.Length] + replacement;
}

// Heuristic: does the upload_staged_file tool's text response describe a FAILURE rather than a
// successful upload? MCP tools sometimes return isError=false while embedding an error message in
// the text (e.g. content-type validation), so we also scan the text for known failure phrases.
static bool UploadTextIndicatesFailure(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return true; // no confirmation => treat as failure
    var t = text.ToLowerInvariant();

    // Failure phrases first (whole words/phrases only — NEVER bare HTTP codes like "401",
    // which match digits inside GUIDs/ids and cause false positives on success messages).
    string[] failureMarkers =
    {
        "does not exist", "is required", "not configured", "no lob", "missing lob",
        "please provide", "not supported", "failed", "failure", "exception", "denied",
        "unauthorized", "forbidden", "could not", "couldn't", "unable to",
        "not allowed", "not found", "rejected", "badrequest", "bad request",
    };
    foreach (var m in failureMarkers)
        if (t.Contains(m)) return true;

    // Positive success signal: upload_staged_file returns the new documentId (and "attached it to").
    if (t.Contains("documentid") || t.Contains("attached it to")) return false;

    // No clear success signal and no failure phrase -> treat as failure (require real confirmation).
    return true;
}

// Heuristic: does the capture_document tool's text response describe a FAILURE rather than a successful
// capture? Same idea as UploadTextIndicatesFailure — capture_document returns "Captured '...'" plus the
// response JSON (which carries a documentId) on success, and an error message on failure.
static bool CaptureTextIndicatesFailure(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return true; // no confirmation => treat as failure
    var t = text.ToLowerInvariant();

    string[] failureMarkers =
    {
        "does not exist", "is required", "not configured", "no lob", "missing lob",
        "please provide", "not supported", "capture failed", "failed", "failure", "exception", "denied",
        "unauthorized", "forbidden", "could not", "couldn't", "unable to",
        "not allowed", "not found", "rejected", "badrequest", "bad request", "must be a json array",
        "not valid json",
    };
    foreach (var m in failureMarkers)
        if (t.Contains(m)) return true;

    // Positive success signal: the tool starts with "Captured '...'" and the response JSON carries a documentId.
    if (t.Contains("captured '") || t.Contains("documentid")) return false;

    return true;
}

// AgentResponse.output is an array of items: type "message" (assistant text) or
// type "function_call". We surface the assistant text from output[].content[] where
// content.type == "output_text".
static string ExtractReply(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return "(no text in response)";

        var texts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var t) && t.GetString() == "message" &&
                item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var pt) && pt.GetString() == "output_text" &&
                        part.TryGetProperty("text", out var text) && text.GetString() is { } s && s.Length > 0)
                    {
                        texts.Add(s);
                    }
                }
            }
        }
        var reply = string.Join("\n", texts).Trim();
        return reply.Length > 0 ? reply : "(no text in response)";
    }
    catch
    {
        return "(could not parse agent response)";
    }
}

// ================= MCP JSON-RPC (Streamable HTTP) client =================
// Moved to Mcp/McpJsonRpc.cs — the deterministic (no-LLM) client that does the initialize ->
// notifications/initialized -> tools/call handshake and parses the MCP tool responses.

// ================= types =================
// Moved to dedicated files: Configuration/BffOptions.cs (AuthOptions/AgentOptions/McpOptions/WorkdayOptions),
// Infrastructure/BffInfrastructure.cs (Pkce/SessionStore/UserSession/WorkdayTokenCache), Models/BffModels.cs
// (request/response records + WorkerMatch). IDP types live under Idp/; the MCP client under Mcp/McpJsonRpc.cs.

