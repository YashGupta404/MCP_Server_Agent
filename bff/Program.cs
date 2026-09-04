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
        // Backend-agnostic listing: query_documents lists a record's documents using the ACTIVE system's
        // configured queries (merged internally by the tool). If that system has no queries configured,
        // fall back to the record-scoped list_documents.
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", new
        {
            businessObjectType = req.BusinessObjectType,
            businessObjectId = req.BusinessObjectId,
        }, log, cts.Token);

        if (text.Contains("No document queries are configured", StringComparison.OrdinalIgnoreCase))
        {
            text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_documents", new
            {
                businessObjectId = req.BusinessObjectId,
                businessObjectType = req.BusinessObjectType,
                onlyMine = req.OnlyMine ?? false,
            }, log, cts.Token);
        }

        var documents = McpJsonRpc.ParseDocumentList(text);
        log.LogInformation("[/api/context] type={Type} id={Id} -> {Count} document(s)",
            req.BusinessObjectType, req.BusinessObjectId, documents.Length);
        return Results.Json(new
        {
            businessObjectId = req.BusinessObjectId,
            businessObjectType = req.BusinessObjectType,
            documents,
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
        var docs = new List<object>();
        if (string.IsNullOrEmpty(text)) return docs.ToArray();

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

            string name = PickValue("hfs_Name", "Document Name", "Name", "File Name", "Title") ?? docId;
            string? type = PickValue("Document Type", "Type");

            // Drop the columns surfaced as name/type (and their duplicates) so the card sub-line doesn't
            // just repeat the title/type.
            foreach (var dup in new[] { "hfs_Name", "Document Name", "Name", "File Name", "Title", "Document Type", "Type", "Document Handle" })
            {
                var k = attributes.Keys.FirstOrDefault(x => string.Equals(x, dup, StringComparison.OrdinalIgnoreCase));
                if (k is not null) attributes.Remove(k);
            }

            docs.Add(new { docId, name, type, attributes });
        }

        return docs.ToArray();
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

// ================= types =================

static class Pkce
{
    public static string RandomToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();

    public UserSession Save(string id, TokenResponse token)
    {
        var session = new UserSession
        {
            AccessToken = token.access_token,
            RefreshToken = token.refresh_token,
            // 60s safety buffer so we refresh slightly early.
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.expires_in - 60)),
        };
        _sessions[id] = session;
        return session;
    }

    public bool TryGet(string id, out UserSession? session) => _sessions.TryGetValue(id, out session);
    public void Remove(string id) => _sessions.TryRemove(id, out _);
}

sealed class UserSession
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

sealed class AuthOptions
{
    public string AuthorizeEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string EndSessionEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scopes { get; set; } = "";
}

sealed class AgentOptions
{
    public string ApiBaseUrl { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string VersionId { get; set; } = "latest";
}

sealed class McpOptions
{
    // Base URL of the MCP server (without a trailing /mcp), e.g. http://localhost:5200 locally or the
    // dev tunnel URL. The BFF POSTs attachment bytes to {BaseUrl}/staging/upload.
    public string BaseUrl { get; set; } = "";
    public string HeaderName { get; set; } = "X-Api-Key";
    // The MCP API key; keep it in user-secrets, not appsettings.
    public string ApiKey { get; set; } = "";
}

sealed class WorkdayOptions
{
    // Workday Cloud Platform Staffing API base, e.g. https://api.us.wcp.workday.com/staffing/v7.
    public string StaffingBaseUrl { get; set; } = "https://api.us.wcp.workday.com/staffing/v7";
    // OAuth token endpoint for the Workday API client. Keep credentials in user-secrets.
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    // When set, a refresh_token grant is used; otherwise client_credentials.
    public string RefreshToken { get; set; } = "";
    public string Scope { get; set; } = "";
}

// Small thread-safe cache for the Workday access token so we don't re-auth on every lookup.
sealed class WorkdayTokenCache
{
    private readonly object _lock = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public bool TryGet(out string? token)
    {
        lock (_lock)
        {
            token = _token;
            return _token is not null && DateTimeOffset.UtcNow < _expiresAt;
        }
    }

    public void Set(string token, int expiresInSeconds)
    {
        lock (_lock)
        {
            _token = token;
            // 60s safety buffer; never cache for less than 30s.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
        }
    }
}

sealed record WorkerMatch(
    string? Wid,
    string? Name,
    string? EmployeeId,
    string? BusinessTitle,
    string? SupervisoryOrganization);


sealed record ChatRequest(string Message, string? ConversationId, ChatAttachment[]? Attachments);

sealed record ChatAttachment(string? Name, string? Mime, string DataBase64);

sealed record ContextRequest(string BusinessObjectType, string BusinessObjectId, bool? OnlyMine);

sealed record SystemConfigRequest(string FriendlyName);

sealed record UploadRequest(string BusinessObjectType, string BusinessObjectId, string EcmContentTypeName, ChatAttachment[]? Attachments);

// Workday capture: file(s) + the documentType and the record-identifying business-object attributes.
// BusinessObjectAttributes is passed through verbatim as a JSON array to the MCP capture_document tool.
sealed record CaptureRequest(
    string? BusinessObjectType,
    string DocumentTypeId,
    JsonElement[]? BusinessObjectAttributes,
    string? BusinessObjectId,
    string? DocumentId,
    bool? CreateNewVersion,
    ChatAttachment[]? Attachments);

sealed record ViewerRequest(string DocId);

sealed record ExchangeRequest(string Code, string CodeVerifier, string RedirectUri);

// IAM token endpoint response (snake_case to match the JSON).
sealed record TokenResponse(
    string access_token,
    string? refresh_token,
    int expires_in,
    string? token_type,
    string? id_token,
    string? scope);
