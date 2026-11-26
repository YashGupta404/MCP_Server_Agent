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
builder.Services.Configure<ContextAgentOptions>(builder.Configuration.GetSection("ContextAgent"));
builder.Services.Configure<MappingAgentOptions>(builder.Configuration.GetSection("MappingAgent"));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));
builder.Services.Configure<WorkdayOptions>(builder.Configuration.GetSection("Workday"));
builder.Services.Configure<KeContextOptions>(builder.Configuration.GetSection("Ke"));
builder.Services.Configure<DataCurationOptions>(builder.Configuration.GetSection("DataCuration"));
builder.Services.Configure<IdpOptions>(builder.Configuration.GetSection("Idp"));
builder.Services.Configure<ViewerOptions>(builder.Configuration.GetSection("Viewer"));
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
var contextAgent = app.Services.GetRequiredService<IOptions<ContextAgentOptions>>().Value;
var mappingAgent = app.Services.GetRequiredService<IOptions<MappingAgentOptions>>().Value;
var mcp = app.Services.GetRequiredService<IOptions<McpOptions>>().Value;
var workday = app.Services.GetRequiredService<IOptions<WorkdayOptions>>().Value;
var idp = app.Services.GetRequiredService<IOptions<IdpOptions>>().Value;
var viewer = app.Services.GetRequiredService<IOptions<ViewerOptions>>().Value;
var ke = app.Services.GetRequiredService<IOptions<KeContextOptions>>().Value;
var dataCuration = app.Services.GetRequiredService<IOptions<DataCurationOptions>>().Value;
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

string UserInfoEndpoint()
{
    if (!string.IsNullOrWhiteSpace(auth.TokenEndpoint) && auth.TokenEndpoint.Contains("/connect/token", StringComparison.OrdinalIgnoreCase))
        return auth.TokenEndpoint.Replace("/connect/token", "/connect/userinfo", StringComparison.OrdinalIgnoreCase);
    return auth.AuthorizeEndpoint.Replace("/connect/authorize", "/connect/userinfo", StringComparison.OrdinalIgnoreCase);
}

async Task<UserSession?> EnsureFreshSessionAsync(string sessionId, UserSession session)
{
    if (DateTimeOffset.UtcNow < session.ExpiresAt || string.IsNullOrEmpty(session.RefreshToken))
        return session;

    var http = httpFactory.CreateClient();
    var refreshResp = await http.PostAsync(auth.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = session.RefreshToken!,
        ["client_id"] = auth.ClientId,
        ["client_secret"] = auth.ClientSecret,
    }));
    var refreshBody = await refreshResp.Content.ReadAsStringAsync();
    if (!refreshResp.IsSuccessStatusCode)
    {
        log.LogWarning("Refresh failed {Status}: {Body}", (int)refreshResp.StatusCode, refreshBody);
        return null;
    }

    return sessions.Save(sessionId, JsonSerializer.Deserialize<TokenResponse>(refreshBody)!);
}

static string ComputeInitials(string? name, string? email, string? givenName = null, string? familyName = null)
{
    if (!string.IsNullOrWhiteSpace(givenName) && !string.IsNullOrWhiteSpace(familyName))
        return string.Concat(givenName.Trim()[0], familyName.Trim()[0]).ToUpperInvariant();

    if (!string.IsNullOrWhiteSpace(name) && !name.Contains('@'))
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(parts.First()[0], parts.Last()[0]).ToUpperInvariant();
    }

    var local = (email ?? string.Empty).Split('@', 2)[0];
    var plusIdx = local.IndexOf('+');
    if (plusIdx >= 0) local = local[..plusIdx];
    var segs = local.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (segs.Length == 1) return segs[0][..Math.Min(2, segs[0].Length)].ToUpperInvariant();
    return string.Concat(segs.First()[0], segs.Last()[0]).ToUpperInvariant();
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

app.MapGet("/api/profile", async (HttpContext ctx) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out var session))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    session = await EnsureFreshSessionAsync(sessionId, session!);
    if (session is null)
        return Results.Json(new { error = "session_expired" }, statusCode: 401);

    try
    {
        var http = httpFactory.CreateClient();
        using var reqMsg = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint());
        reqMsg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.AccessToken}");
        reqMsg.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await http.SendAsync(reqMsg);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            log.LogError("/api/profile userinfo failed {Status}: {Body}", (int)resp.StatusCode, body);
            return Results.Json(new { error = "userinfo_failed", detail = body }, statusCode: 502);
        }

        var root = JsonNode.Parse(body);
        var name = root?["name"]?.ToString() ?? string.Empty;
        var email = root?["preferred_username"]?.ToString() ?? root?["email"]?.ToString() ?? string.Empty;
        var givenName = root?["given_name"]?.ToString() ?? string.Empty;
        var familyName = root?["family_name"]?.ToString() ?? string.Empty;

        return Results.Json(new
        {
            name,
            email,
            givenName,
            familyName,
            initials = ComputeInitials(name, email, givenName, familyName)
        });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/profile failed");
        return Results.Json(new { error = "profile_failed", detail = ex.Message }, statusCode: 502);
    }
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
                    $"filterOperator=\"ContainsCaseInsensitive\", maxResults=25. Use ONLY the queryIds and fieldIds listed above — " +
                    $"never invent them — and report every returned document.]").Trim();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "/api/chat: search catalog hint skipped for {Type}", req.BusinessObjectType);
        }
    }

    // Tag each listed document with a machine marker the plugin turns into an "Open" button that
    // shows the document in the in-panel viewer. The docId comes from the tool result.
    effectiveMessage = (effectiveMessage +
        "\n\n[When you list, show, or mention documents, append to the END of each document's line a marker in the " +
        "EXACT form [[doc:<docId>|<documentName>]] using that document's docId and name from the tool result " +
        "(never alter or invent the docId). Keep your normal readable answer; these markers are hidden and render " +
        "as clickable \"Open\" buttons that open the document in the panel. Add exactly one marker per document. " +
        "If there are many documents, list AT MOST the 20 most recent (newest first) and then state the total count " +
        "(e.g. \"Showing 20 of 100\") so the reply stays within the output limit.]").Trim();

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
        JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = effectiveMessage } },
        }),
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

// ---------- AI context extraction: detect the business object on ANY page via the Agent Builder agent ----------
// The extension captures the active tab (url + title + visible text) and posts it here. We invoke the
// context_extractor Task agent (invoke-task) which returns a structured businessContext outcome. The agent
// has NO web access, so the page content MUST be supplied by the caller (the extension).
app.MapPost("/api/context/extract", async (HttpContext ctx, ContextExtractRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out var session))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(req.PageUrl) && string.IsNullOrWhiteSpace(req.PageContent))
        return Results.Json(new { error = "missing_fields", detail = "pageUrl or pageContent is required." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(contextAgent.AgentId) || string.IsNullOrWhiteSpace(contextAgent.ApiBaseUrl))
        return Results.Json(new { error = "context_agent_not_configured", detail = "ContextAgent:AgentId / ApiBaseUrl is not set." }, statusCode: 500);

    var http = httpFactory.CreateClient();
    http.Timeout = Timeout.InfiniteTimeSpan;

    // Refresh the access token if it's expired (same pattern as /api/chat).
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
            session = sessions.Save(sessionId, JsonSerializer.Deserialize<TokenResponse>(refreshBody)!);
        else
            return Results.Json(new { error = "session_expired" }, statusCode: 401);
    }

    // Trim the page content to keep the request within the model's context window and control token cost.
    var pageContent = req.PageContent ?? "";
    const int maxChars = 12000;
    if (pageContent.Length > maxChars) pageContent = pageContent.Substring(0, maxChars);

    // The cleaned DOM structure (for authoring xpath/css extraction rules) is larger; cap it generously.
    var pageHtml = req.PageHtml ?? "";
    const int maxHtmlChars = 120000;
    if (pageHtml.Length > maxHtmlChars) pageHtml = pageHtml.Substring(0, maxHtmlChars);

    var invokeUrl = $"{contextAgent.ApiBaseUrl}/v1/agents/{contextAgent.AgentId}/versions/{contextAgent.VersionId}/invoke-task";
    var invokeJson = JsonSerializer.Serialize(new
    {
        inputs = new
        {
            pageUrl = req.PageUrl ?? "",
            pageTitle = req.PageTitle ?? "",
            pageContent,
            pageHtml,
        },
    });
    HttpRequestMessage BuildInvoke()
    {
        var r = new HttpRequestMessage(HttpMethod.Post, invokeUrl);
        r.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session!.AccessToken}");
        // Agent Builder requires X-Session-ID to be a valid UUID (the BFF session id is rejected with a 400).
        r.Headers.TryAddWithoutValidation("X-Session-ID", Guid.NewGuid().ToString());
        r.Headers.TryAddWithoutValidation("User-Agent", "UcebAgentBff/0.1");
        r.Headers.TryAddWithoutValidation("Accept", "application/json");
        r.Content = new StringContent(invokeJson, Encoding.UTF8, "application/json");
        return r;
    }

    HttpResponseMessage invokeResp;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        invokeResp = await SendWithConnectRetryAsync(http, BuildInvoke, log, cts.Token);
    }
    catch (OperationCanceledException)
    {
        log.LogError("/api/context/extract timed out url={Url}", invokeUrl);
        return Results.Json(new { error = "invoke_timeout", status = 504 }, statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        log.LogError(ex, "/api/context/extract could not connect url={Url}", invokeUrl);
        return Results.Json(new { error = "agent_unreachable", detail = $"Could not connect to Agent Builder: {ex.InnerException?.Message ?? ex.Message}. Try again." }, statusCode: 502);
    }
    var body = await invokeResp.Content.ReadAsStringAsync();

    if (!invokeResp.IsSuccessStatusCode)
    {
        log.LogError("/api/context/extract invoke failed {Status} url={Url}: {Body}", (int)invokeResp.StatusCode, invokeUrl, body);
        return Results.Json(new { error = "invoke_failed", status = (int)invokeResp.StatusCode, detail = body }, statusCode: (int)invokeResp.StatusCode);
    }

    // Best-effort parse of the structured businessContext outcome; include raw until the exact
    // invoke-task response shape is confirmed on a live call.
    var context = ExtractTaskOutcome(body);
    JsonElement raw = default;
    try { raw = JsonSerializer.Deserialize<JsonElement>(body); } catch { }
    return Results.Json(new { context, raw });
});

// ---------- Data Curation (KE): full document text (OCR for images) for the capture/mapping agent ----------
app.MapPost("/api/datacuration/extract", async (HttpContext ctx, AutoConfigRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (!dataCuration.IsConfigured)
        return Results.Json(new { error = "datacuration_not_configured", detail = "Set DataCuration:ClientId and DataCuration:ClientSecret in the BFF user-secrets (cin-data-curation-api client)." }, statusCode: 500);

    if (req.Attachments is null || req.Attachments.Length == 0)
        return Results.Json(new { error = "missing_files", detail = "Attach at least one document." }, statusCode: 400);

    var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(dataCuration.TimeoutSeconds + 30);
    var dc = new DataCurationClient(http, dataCuration);

    var files = new List<object>();
    foreach (var a in req.Attachments)
    {
        if (string.IsNullOrWhiteSpace(a.DataBase64)) continue;
        try
        {
            var r = await dc.ExtractAsync(Convert.FromBase64String(a.DataBase64), ctx.RequestAborted);
            files.Add(new { name = a.Name, r.JobId, r.Status, r.FileType, r.PageCount, text = r.Text, markdown = r.Markdown });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/datacuration/extract failed for {Name}", a.Name);
            files.Add(new { name = a.Name, error = ex.Message });
        }
    }
    return Results.Json(new { files });
});

// ---------- Agent mapping: LOB context + document text + live ECM catalog -> solution_config_mapper_poc ----------
// Inputs match the agent's v4 prompt: businessContext, lobPageContent, ecmCatalog, documentInsights, ecmQueries.
// documentInsights carries the Data Curation full text per file, plus KE Context classification/entities when
// configured. The outcome is validated against the live catalog before it is returned.
app.MapPost("/api/autoconfig/agent-map", async (HttpContext ctx, AgentMapRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out var session))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (req.Attachments is null || !req.Attachments.Any(a => !string.IsNullOrWhiteSpace(a.DataBase64)))
        return Results.Json(new { error = "missing_files", detail = "Attach at least one sample document." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(mappingAgent.AgentId) || string.IsNullOrWhiteSpace(mappingAgent.ApiBaseUrl))
        return Results.Json(new { error = "mapping_agent_not_configured", detail = "MappingAgent:AgentId / ApiBaseUrl is not set." }, statusCode: 500);
    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured", detail = "Mcp:BaseUrl / Mcp:ApiKey is not configured on the BFF." }, statusCode: 500);
    if (!dataCuration.IsConfigured)
        return Results.Json(new { error = "datacuration_not_configured", detail = "Set DataCuration:ClientId and DataCuration:ClientSecret in the BFF user-secrets." }, statusCode: 500);

    var http = httpFactory.CreateClient();
    http.Timeout = Timeout.InfiniteTimeSpan;

    // Refresh the access token if it's expired (same pattern as /api/context/extract).
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
            session = sessions.Save(sessionId, JsonSerializer.Deserialize<TokenResponse>(refreshBody)!);
        else
            return Results.Json(new { error = "session_expired" }, statusCode: 401);
    }

    var ct = ctx.RequestAborted;
    var files = req.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.DataBase64))
        .Select(a => (Att: a, Bytes: Convert.FromBase64String(a.DataBase64))).ToList();

    // Catalogs and document text are independent: start them together.
    var dcHttp = httpFactory.CreateClient();
    dcHttp.Timeout = TimeSpan.FromSeconds(dataCuration.TimeoutSeconds + 30);
    var dc = new DataCurationClient(dcHttp, dataCuration);
    var catalogTask = EcmDoctypeCatalog.FetchAsync(httpFactory, mcp, log, ct);
    var queriesTask = EcmQueryCatalog.FetchAsync(httpFactory, mcp, log, ct);
    var textTasks = files.Select(f => dc.ExtractAsync(f.Bytes, ct)).ToList();

    EcmCatalog catalog;
    try { catalog = await catalogTask; }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/autoconfig/agent-map: ECM catalog failed");
        return Results.Json(new { error = "catalog_failed", detail = ex.Message }, statusCode: 502);
    }
    if (catalog.Doctypes.Count == 0)
        return Results.Json(new { error = "no_doctypes", detail = "The active system returned no document types." }, statusCode: 400);

    // KE Context is an optional extra signal (classification against the live doctype names + entities).
    var classes = catalog.Doctypes.Select(d => d.ContentTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
    var keTasks = files.Select(f => ke.IsConfigured
        ? EnrichOrNullAsync(f.Bytes, string.IsNullOrWhiteSpace(f.Att.Mime) ? KeContextClient.GuessContentType(f.Att.Name ?? "") : f.Att.Mime!)
        : Task.FromResult<KeContentsProcessingResults?>(null)).ToList();

    async Task<KeContentsProcessingResults?> EnrichOrNullAsync(byte[] bytes, string mime)
    {
        try
        {
            var keHttp = httpFactory.CreateClient();
            keHttp.Timeout = TimeSpan.FromSeconds(180);
            return await new KeContextClient(keHttp, ke).EnrichAsync(bytes, mime, classes, ke.SummaryMaxWords, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "/api/autoconfig/agent-map: KE Context failed; continuing with Data Curation text only");
            return null;
        }
    }

    JsonArray queries;
    DataCurationResult[] texts;
    KeContentsProcessingResults?[] keResults;
    try
    {
        queries = await queriesTask;
        texts = await Task.WhenAll(textTasks);
        keResults = await Task.WhenAll(keTasks);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/autoconfig/agent-map: query catalog or Data Curation failed");
        return Results.Json(new { error = "inputs_failed", detail = ex.Message }, statusCode: 502);
    }

    var insights = new JsonArray();
    for (var i = 0; i < files.Count; i++)
    {
        var item = new JsonObject
        {
            ["fileName"] = files[i].Att.Name,
            ["documentText"] = texts[i].Text,
        };
        var r = keResults[i]?.Results?.FirstOrDefault();
        if (r is not null)
        {
            item["classification"] = r.TextClassification?.Result;
            item["summary"] = r.TextSummary?.Result;
            item["entities"] = JsonSerializer.SerializeToNode(r.NamedEntityText?.Result);
        }
        insights.Add(item);
    }

    var catalogJson = new JsonArray(catalog.Doctypes.Select(d => (JsonNode)new JsonObject
    {
        ["id"] = d.ContentTypeId,
        ["name"] = d.ContentTypeName,
        ["attributes"] = new JsonArray(d.Keywords.Select(k => (JsonNode)new JsonObject
        {
            ["id"] = k.Id,
            ["name"] = k.Label,
            ["dataType"] = k.DataType,
        }).ToArray()),
    }).ToArray()).ToJsonString();

    var businessContext = req.BusinessContext is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } bc
        ? bc.GetRawText() : "{}";
    var lobPageContent = req.PageContent ?? "";
    if (lobPageContent.Length > 12000) lobPageContent = lobPageContent[..12000];
    var documentInsights = insights.ToJsonString();
    var ecmQueries = queries.ToJsonString();

    var invokeUrl = $"{mappingAgent.ApiBaseUrl}/v1/agents/{mappingAgent.AgentId}/versions/{mappingAgent.VersionId}/invoke-task";
    var invokeJson = JsonSerializer.Serialize(new
    {
        inputs = new { businessContext, lobPageContent, ecmCatalog = catalogJson, documentInsights, ecmQueries },
    });
    HttpRequestMessage BuildInvoke()
    {
        var r = new HttpRequestMessage(HttpMethod.Post, invokeUrl);
        r.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session!.AccessToken}");
        r.Headers.TryAddWithoutValidation("X-Session-ID", Guid.NewGuid().ToString());
        r.Headers.TryAddWithoutValidation("User-Agent", "UcebAgentBff/0.1");
        r.Headers.TryAddWithoutValidation("Accept", "application/json");
        r.Content = new StringContent(invokeJson, Encoding.UTF8, "application/json");
        return r;
    }

    HttpResponseMessage invokeResp;
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));
        invokeResp = await SendWithConnectRetryAsync(http, BuildInvoke, log, cts.Token);
    }
    catch (OperationCanceledException)
    {
        log.LogError("/api/autoconfig/agent-map: mapping agent timed out url={Url}", invokeUrl);
        return Results.Json(new { error = "invoke_timeout", status = 504 }, statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        log.LogError(ex, "/api/autoconfig/agent-map: could not connect url={Url}", invokeUrl);
        return Results.Json(new { error = "agent_unreachable", detail = $"Could not connect to Agent Builder: {ex.InnerException?.Message ?? ex.Message}. Try again." }, statusCode: 502);
    }
    var body = await invokeResp.Content.ReadAsStringAsync();
    if (!invokeResp.IsSuccessStatusCode)
    {
        log.LogError("/api/autoconfig/agent-map invoke failed {Status}: {Body}", (int)invokeResp.StatusCode, body);
        return Results.Json(new { error = "invoke_failed", status = (int)invokeResp.StatusCode, detail = body }, statusCode: (int)invokeResp.StatusCode);
    }

    try
    {
        var dumpDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "logs", "agent-map");
        Directory.CreateDirectory(dumpDir);
        await File.WriteAllTextAsync(Path.Combine(dumpDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), body);
    }
    catch (Exception ex) { log.LogWarning(ex, "/api/autoconfig/agent-map: could not save the raw agent response"); }

    var outcome = ExtractNamedOutcome(body, "solutionMapping");
    if (outcome is null)
    {
        JsonElement raw = default;
        try { raw = JsonSerializer.Deserialize<JsonElement>(body); } catch { }
        return Results.Json(new { error = "no_outcome", detail = "The agent response had no solutionMapping outcome.", raw }, statusCode: 502);
    }

    var validation = AgentMappingValidator.Validate(outcome.Value, catalog, queries);
    var recordId = req.BusinessContext is { ValueKind: JsonValueKind.Object } ctxObj
                   && ctxObj.TryGetProperty("businessObjectId", out var idEl) ? idEl.ToString() : null;
    var captureConfidence = AgentMappingValidator.CaptureConfidence(validation.Mapping, catalog,
        keResults.Select(r => r?.Results?.FirstOrDefault()?.TextClassification?.Result).ToList(), recordId);
    return Results.Json(new
    {
        mappings = validation.Mapping,
        captureConfidence,
        validation = new { valid = validation.Issues.Count == 0, issues = validation.Issues },
        inputsSent = new
        {
            businessContext = JsonNode.Parse(businessContext),
            ecmCatalog = JsonNode.Parse(catalogJson),
            documentInsights = insights,
            ecmQueries = queries,
        },
    });
});

// ---------- Zero-setup capture: write the capture-only config entry, then upload with the reviewed values ----------
// UCEB rejects an upload unless the active system's solution config has an entry for (busObject, doctype)
// (422 code 3422), so the entry is upserted just before the upload. The previous config is saved first.
app.MapPost("/api/capture/commit", async (HttpContext ctx, CaptureCommitRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    var att = req.Attachments?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.DataBase64));
    if (att is null)
        return Results.Json(new { error = "no_file", detail = "Attach the document to capture." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(req.BusinessObjectType) || string.IsNullOrWhiteSpace(req.BusinessObjectId))
        return Results.Json(new { error = "missing_fields", detail = "businessObjectType and businessObjectId are required." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(req.DocumentTypeId))
        return Results.Json(new { error = "missing_docType", detail = "documentTypeId is required." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured", detail = "Mcp:BaseUrl / Mcp:ApiKey is not configured on the BFF." }, statusCode: 500);

    var ct = ctx.RequestAborted;
    var busObject = req.BusinessObjectType!;
    var system = await ActiveSystemFriendlyNameAsync(ct);

    // 1) the doctype's real name + attributes, so we only send fields that exist on it.
    EcmDoctype doctype;
    try
    {
        var metaText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_document_type_metadata", new { documentTypeId = req.DocumentTypeId }, log, ct);
        var meta = metaText.TrimStart().StartsWith('{') ? JsonNode.Parse(metaText)?["data"] : null;
        doctype = meta is null ? null! : KeContextClient.ParseCatalog(new JsonObject { ["data"] = new JsonArray(meta.DeepClone()) }.ToJsonString()).Doctypes.FirstOrDefault()!;
        if (doctype is null)
            return Results.Json(new { error = "unknown_docType", detail = $"Document type '{req.DocumentTypeId}' was not found on '{system}': {metaText}" }, statusCode: 400);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/capture/commit: doctype metadata failed");
        return Results.Json(new { error = "doctype_failed", detail = ex.Message }, statusCode: 502);
    }

    var attrs = doctype.Keywords.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);
    var skipped = new List<string>();
    var fields = new List<(EcmKeyword Kw, string Value)>();
    foreach (var f in req.Fields ?? Array.Empty<CaptureFieldValue>())
    {
        if (string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Value)) continue;
        if (!attrs.TryGetValue(f.Id, out var kw)) { skipped.Add(f.Id); continue; }
        if (string.Equals(f.Id, req.RecordFieldId, StringComparison.OrdinalIgnoreCase)) continue;
        var value = CaptureConfigWriter.CoerceValue(kw.DataType, f.Value!);
        if (value is null) { skipped.Add($"{f.Id} ({kw.DataType}: '{f.Value}' is not a number)"); continue; }
        fields.Add((kw, value));
    }

    // 2) the doctype's group (OnBase/CFS rejects the archive without it).
    string? groupId;
    try { groupId = await DoctypeGroupResolver.ResolveAsync(httpFactory, mcp, system, doctype.ContentTypeId, log, ct); }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/capture/commit: doctype group lookup failed");
        return Results.Json(new { error = "group_failed", detail = ex.Message }, statusCode: 502);
    }

    // 3) read config, back it up, upsert the capture-only entry, write it.
    var spec = new CaptureConfigWriter.Spec(busObject, doctype.ContentTypeName, doctype.ContentTypeId, groupId,
        string.IsNullOrWhiteSpace(req.RecordFieldId) ? null : req.RecordFieldId,
        fields.Select(f => new CaptureConfigWriter.Field(f.Kw.Id, f.Kw.Label, f.Kw.DataType)).ToList());
    JsonObject entry;
    string? originalData;
    try
    {
        var current = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
        if (!current.TrimStart().StartsWith('{'))
            return Results.Json(new { error = "config_read_failed", detail = current }, statusCode: 502);

        var backupDir = Path.Combine(app.Environment.ContentRootPath, "config-backups", "jit");
        Directory.CreateDirectory(backupDir);
        var safeSystem = string.Concat((string.IsNullOrWhiteSpace(system) ? "default" : system).Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        await File.WriteAllTextAsync(Path.Combine(backupDir, $"{safeSystem}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), current, ct);

        var currentNode = JsonNode.Parse(current)!;
        originalData = (currentNode["data"] ?? currentNode).ToJsonString();
        var data = CaptureConfigWriter.Upsert(currentNode, spec);
        var writeResult = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "set_solution_configuration",
            new { dataJson = data.ToJsonString() }, log, ct);
        if (!writeResult.Contains("saved", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { error = "config_write_failed", detail = writeResult }, statusCode: 502);
        entry = CaptureConfigWriter.BuildEntry(spec);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/capture/commit: config write failed");
        return Results.Json(new { error = "config_write_failed", detail = ex.Message }, statusCode: 502);
    }

    // The entry is only needed for this upload: always put the config back exactly as it was before.
    (object Body, int Status) outcome;
    bool configRestored;
    try { outcome = await StageAndUploadAsync(); }
    finally { configRestored = await RestoreConfigAsync(); }
    var response = JsonSerializer.SerializeToNode(outcome.Body)!.AsObject();
    response["configRestored"] = configRestored;
    return Results.Json(response, statusCode: outcome.Status);

    async Task<bool> RestoreConfigAsync()
    {
        try
        {
            var r = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "set_solution_configuration",
                new { dataJson = originalData }, log, CancellationToken.None);
            var ok = r.Contains("saved", StringComparison.OrdinalIgnoreCase);
            if (!ok) log.LogError("/api/capture/commit: config restore failed: {Result}", r);
            return ok;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/capture/commit: config restore failed");
            return false;
        }
    }

    // 4) stage the file on the MCP and upload it with the record id + the reviewed values.
    async Task<(object Body, int Status)> StageAndUploadAsync()
    {
        var originalName = string.IsNullOrWhiteSpace(att.Name) ? "upload" : Path.GetFileName(att.Name);
        var stagedName = NormalizeUploadExtension(originalName);
        string? stagingId;
        try
        {
            var stageHttp = httpFactory.CreateClient();
            stageHttp.Timeout = TimeSpan.FromSeconds(120);
            using var stageReq = new HttpRequestMessage(HttpMethod.Post, $"{mcp.BaseUrl.TrimEnd('/')}/staging/upload");
            stageReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);
            stageReq.Content = new StringContent(
                JsonSerializer.Serialize(new { fileName = stagedName, mime = att.Mime, dataBase64 = att.DataBase64 }),
                Encoding.UTF8, "application/json");
            using var stageResp = await stageHttp.SendAsync(stageReq, ct);
            var stageBody = await stageResp.Content.ReadAsStringAsync(ct);
            if (!stageResp.IsSuccessStatusCode)
                return (new { error = "staging_failed", status = (int)stageResp.StatusCode, detail = stageBody }, 502);
            using var stageDoc = JsonDocument.Parse(stageBody);
            stagingId = stageDoc.RootElement.TryGetProperty("stagingId", out var idEl) ? idEl.GetString() : null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/capture/commit: staging failed");
            return (new { error = "staging_failed", detail = ex.Message }, 502);
        }
        if (string.IsNullOrEmpty(stagingId))
            return (new { error = "staging_failed", detail = "No stagingId returned." }, 502);

        var uploadAttrs = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.RecordFieldId)) uploadAttrs.Add(new { name = req.RecordFieldId, value = req.BusinessObjectId });
        uploadAttrs.AddRange(fields.Select(f => (object)new { name = f.Kw.Id, value = f.Value }));

        string uploadText;
        try
        {
            uploadText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "upload_staged_file", new
            {
                stagingId,
                businessObjectId = req.BusinessObjectId,
                businessObjectType = busObject,
                ecmContentTypeName = doctype.ContentTypeName,
                documentName = Path.GetFileNameWithoutExtension(originalName),
                additionalAttributesJson = JsonSerializer.Serialize(uploadAttrs),
            }, log, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/capture/commit: upload failed");
            return (new { error = "upload_failed", detail = ex.Message, configEntry = entry }, 502);
        }

        var match = System.Text.RegularExpressions.Regex.Match(uploadText ?? "", @"documentId:\s*(\S+)");
        if (UploadTextIndicatesFailure(uploadText) || !match.Success)
            return (new { error = "upload_failed", detail = uploadText, configEntry = entry }, 502);

        return (new
        {
            documentId = match.Groups[1].Value,
            system,
            documentType = new { id = doctype.ContentTypeId, name = doctype.ContentTypeName, groupId },
            recordField = req.RecordFieldId,
            fields = fields.Select(f => new { id = f.Kw.Id, label = f.Kw.Label, value = f.Value }),
            skippedFields = skipped,
            configEntry = entry,
            uploadResult = uploadText,
        }, 200);
    }
});

// ---------- KE auto-config (PoC): sample docs -> KE classify/NER -> generated solution-config mapping ----------
// The admin drops a few sample documents in the plugin. The BFF runs them through the KE Context API
// (OCR + classification + NER), picks the known ECM keyword catalog for the detected doctype, and
// generates the solution-config mapping fragment (layers 1-6). No manual config needed.
app.MapPost("/api/autoconfig/analyze", async (HttpContext ctx, AutoConfigRequest req) =>
{
    var sessionId = ctx.Request.Headers["X-BFF-Session"].ToString();
    if (string.IsNullOrEmpty(sessionId) || !sessions.TryGet(sessionId, out _))
        return Results.Json(new { error = "not_authenticated" }, statusCode: 401);

    if (!ke.IsConfigured)
        return Results.Json(new { error = "ke_not_configured", detail = "Set Ke:ClientId and Ke:ClientSecret in the BFF user-secrets (cin-context-api client)." }, statusCode: 500);

    if (req.Attachments is null || req.Attachments.Length == 0)
        return Results.Json(new { error = "missing_files", detail = "Attach at least one sample document." }, statusCode: 400);

    if (string.IsNullOrWhiteSpace(mcp.BaseUrl) || string.IsNullOrWhiteSpace(mcp.ApiKey))
        return Results.Json(new { error = "mcp_not_configured", detail = "Mcp:BaseUrl / Mcp:ApiKey is not configured on the BFF." }, statusCode: 500);

    // Pull the LIVE ECM keyword catalog from UCEB (list_document_types?fetchMetadata=true) for the active
    // system, and derive the classification classes from the real doctype names. No hardcoding.
    EcmCatalog catalog;
    try
    {
        var dtJson = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types", new { fetchMetadata = true }, log, ctx.RequestAborted);
        catalog = KeContextClient.ParseCatalog(dtJson);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/autoconfig/analyze: list_document_types failed");
        return Results.Json(new { error = "catalog_failed", detail = ex.Message }, statusCode: 502);
    }
    if (catalog.Doctypes.Count == 0)
        return Results.Json(new { error = "no_doctypes", detail = "The active system returned no document types to classify against." }, statusCode: 400);

    var classes = catalog.Doctypes.Select(d => d.ContentTypeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();

    var http = httpFactory.CreateClient();
    http.Timeout = TimeSpan.FromSeconds(180);
    var keClient = new KeContextClient(http, ke);

    var files = new List<object>();
    KeContentsProcessingResults? first = null;
    try
    {
        foreach (var a in req.Attachments)
        {
            if (string.IsNullOrWhiteSpace(a.DataBase64)) continue;
            var bytes = Convert.FromBase64String(a.DataBase64);
            var contentType = string.IsNullOrWhiteSpace(a.Mime) ? KeContextClient.GuessContentType(a.Name ?? "") : a.Mime!;
            var results = await keClient.EnrichAsync(bytes, contentType, classes, ke.SummaryMaxWords);
            first ??= results;
            var r = results.Results?.FirstOrDefault();
            files.Add(new
            {
                name = a.Name,
                classification = r?.TextClassification?.Result,
                summary = r?.TextSummary?.Result,
                entities = r?.NamedEntityText?.Result,
            });
        }
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/autoconfig/analyze KE call failed");
        return Results.Json(new { error = "ke_failed", detail = ex.Message }, statusCode: 502);
    }

    object? mappings = null;
    var report = "";
    if (first is not null)
    {
        try
        {
            var busType = req.BusinessObjectType ?? "";
            var lob = new KeMappingGenerator.LobContext(busType, req.BusinessObjectId ?? "", "number", "opened_by", req.Attachments[0].Name ?? "");
            (_, report, mappings) = new KeMappingGenerator().Generate(first, catalog, lob);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "/api/autoconfig/analyze generation failed");
            return Results.Json(new { error = "generation_failed", detail = ex.Message }, statusCode: 502);
        }
    }

    // Mapping-only view: every kind of mapping, nothing else.
    return Results.Json(new { mappings });
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
        string text = "";
        object[] documents = Array.Empty<object>();
        string[]? columns = null;
        // Direct listing (e.g. the CFS Query panel): skip the defaultListQuery path, which runs the
        // native query_documents/queries-execute that 500s on CFS, and go straight to list_documents.
        if (req.Direct != true && dlq is not null && !string.IsNullOrWhiteSpace(dlq.BoContextFieldId))
        {
            async Task<string> RunDlqAsync(string op) => await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", new
            {
                businessObjectType = req.BusinessObjectType,
                queryId = dlq.Id,
                filterFieldId = dlq.BoContextFieldId,
                filterValue = req.BusinessObjectId,
                filterOperator = op,
                maxResults = 500,
            }, log, cts.Token);

            text = await RunDlqAsync("EqualsCaseInsensitive");
            documents = McpJsonRpc.ParseDocumentList(text);
            // CIC only supports case-sensitive operators ("Case-insensitive operator ... not yet supported").
            if (documents.Length == 0 && text.Contains("not yet supported", StringComparison.OrdinalIgnoreCase))
            {
                text = await RunDlqAsync("EqualsCaseSensitive");
                documents = McpJsonRpc.ParseDocumentList(text);
            }
            // Some OnBase/CFS systems reject EqualsCaseInsensitive ("Invalid Operator"); retry with
            // ContainsCaseInsensitive (which they accept and, on a full-id filter value, matches the same doc).
            if (documents.Length == 0 && text.Contains("Invalid Operator", StringComparison.OrdinalIgnoreCase))
            {
                text = await RunDlqAsync("ContainsCaseInsensitive");
                documents = McpJsonRpc.ParseDocumentList(text);
            }
            if (documents.Length > 0 && dlq.Columns.Length > 0) columns = dlq.Columns;
            else if (documents.Length == 0)
                log.LogWarning("[/api/context] defaultListQuery {QueryId} returned nothing; falling back to the merged record listing", dlq.Id);
        }

        // Fallback (no defaultListQuery configured, or that single query failed/returned nothing):
        // merge the record's other configured queries so one broken query can't blank the panel.
        if (documents.Length == 0)
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
            if (documents.Length > 0) columns = null;
        }

        // Last resort (covers Direct listings, e.g. the chatbot "show my documents" fast-path, where the
        // dlq path above was skipped): if nothing came back and a defaultListQuery exists, run it with
        // ContainsCaseInsensitive, which OnBase/CFS systems accept when they reject Equals/StartsWith.
        if (documents.Length == 0 && dlq is not null && !string.IsNullOrWhiteSpace(dlq.BoContextFieldId))
        {
            text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", new
            {
                businessObjectType = req.BusinessObjectType,
                queryId = dlq.Id,
                filterFieldId = dlq.BoContextFieldId,
                filterValue = req.BusinessObjectId,
                filterOperator = "ContainsCaseInsensitive",
                maxResults = 500,
            }, log, cts.Token);
            documents = McpJsonRpc.ParseDocumentList(text);
            if (documents.Length > 0 && dlq.Columns.Length > 0) columns = dlq.Columns;
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
// Prefers the solution config's configured content types for the business object (one fast config read).
// list_document_types enumerates EVERY content-type group in the repo, which on large OnBase systems
// exceeds UCEB's 20s budget (408 "consider using the 'doctypeGroupId' query parameter"), so it is only
// the fallback when no business object is supplied or nothing is configured for it.
app.MapGet("/api/doctypes", async (HttpContext ctx) =>
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
        if (!string.IsNullOrWhiteSpace(boType))
        {
            var configured = await McpJsonRpc.GetConfiguredCicContentTypesAsync(httpFactory, mcp, boType, log, cts.Token);
            if (configured.Count > 0)
            {
                var fromConfig = configured.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
                log.LogInformation("[/api/doctypes] {Count} configured type(s) for {BoType}", fromConfig.Length, boType);
                return Results.Json(new { types = fromConfig, source = "solution-config" });
            }
        }

        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_document_types", new { }, log, cts.Token);
        // "*-base-type" types only carry the LOB mixin for inheritance; they are not uploadable.
        var types = McpJsonRpc.ParseDocumentTypes(text)
            .Where(t => !t.EndsWith("-base-type", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Results.Json(new { types, raw = text, source = "list_document_types" });
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
        async Task<string> RunAsync(string op)
        {
            var args = new Dictionary<string, object?>
            {
                ["businessObjectType"] = req.BusinessObjectType,
                ["businessObjectId"] = req.BusinessObjectId,
                ["queryId"] = req.QueryId,
                ["filterFieldId"] = req.FilterFieldId,
                ["filterValue"] = req.FilterValue,
                ["filterOperator"] = op,
            };
            return await McpJsonRpc.CallToolAsync(httpFactory, mcp, "query_documents", args, log, cts.Token);
        }

        var requested = string.IsNullOrWhiteSpace(req.FilterOperator) ? "ContainsCaseInsensitive" : req.FilterOperator!;
        var text = await RunAsync(requested);
        var documents = McpJsonRpc.ParseDocumentList(text);
        // Some OnBase/CFS systems reject Equals/StartsWith operators ("Invalid Operator") even though the
        // query metadata advertises them; retry with ContainsCaseInsensitive, which they accept.
        if (documents.Length == 0 && text.Contains("Invalid Operator", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requested, "ContainsCaseInsensitive", StringComparison.OrdinalIgnoreCase))
        {
            text = await RunAsync("ContainsCaseInsensitive");
            documents = McpJsonRpc.ParseDocumentList(text);
        }
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

    // The OnBase/CFS capture broker 500s if a typed attribute carries a value it can't parse (e.g. IDP
    // extracting "N/A" into a Date field, or a MM/DD/YYYY date). Validate against the attribute's
    // dataType: drop values that don't fit and normalise dates to ISO (yyyy-MM-dd).
    static string? SanitizeAttrValue(string? value, string? dataType)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        switch ((dataType ?? string.Empty).ToLowerInvariant())
        {
            case "date":
                return DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt)
                    ? dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
            case "integer":
                var digits = new string(v.Where(char.IsDigit).ToArray());
                return digits.Length > 0 ? digits : null;
            default:
                return v;
        }
    }

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
                                            // Only set a value the broker can accept for this field's type.
                                            var clean = SanitizeAttrValue(uVal, o2["dataType"]?.GetValue<string>());
                                            if (clean is not null) o2["value"] = clean;
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

        // CFS-brokered systems (e.g. OnBase via CFS) must use the viewer's /#/cfs/ route with the
        // broker integrationId; the MCP returns the CIC-native /#/documents/ route, which 400s
        // ("Invalid documentId format") for an OnBase handle. Build the correct URL from config.
        var activeSystem = await ActiveSystemFriendlyNameAsync(cts.Token);
        if (!string.IsNullOrEmpty(activeSystem)
            && viewer.CfsSystems.TryGetValue(activeSystem, out var cfs)
            && !string.IsNullOrWhiteSpace(cfs.IntegrationId)
            && !string.IsNullOrWhiteSpace(cfs.Host))
        {
            var part = string.IsNullOrWhiteSpace(cfs.LogicalPartId) ? "1" : cfs.LogicalPartId;
            var cfsUrl = $"{cfs.Host.TrimEnd('/')}/#/cfs/{Uri.EscapeDataString(cfs.IntegrationId)}/" +
                         $"{Uri.EscapeDataString(req.DocId)}/{Uri.EscapeDataString(part)}" +
                         $"?envKey={Uri.EscapeDataString(cfs.EnvKey)}";
            return Results.Json(new { url = cfsUrl, cfs = true });
        }

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

// ---------- Permanently DELETE a document (BFF proxies to MCP DELETE /documents/{id}) ----------
// Session-gated. The MCP removes the document from the active ECM system (CFS/OnBase federated delete).
app.MapPost("/api/document/delete", async (HttpContext ctx) =>
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
        var url = $"{mcp.BaseUrl.TrimEnd('/')}/documents/{Uri.EscapeDataString(docId)}";
        using var delReq = new HttpRequestMessage(HttpMethod.Delete, url);
        delReq.Headers.TryAddWithoutValidation(mcp.HeaderName, mcp.ApiKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var mcpResp = await http.SendAsync(delReq, cts.Token);
        var body = await mcpResp.Content.ReadAsStringAsync(cts.Token);

        if (!mcpResp.IsSuccessStatusCode)
            return Results.Json(new { error = "delete_failed", status = (int)mcpResp.StatusCode, detail = body }, statusCode: (int)mcpResp.StatusCode);

        return Results.Json(new { deleted = true, docId });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "/api/document/delete failed for {DocId}", docId);
        return Results.Json(new { error = "delete_failed", detail = ex.Message }, statusCode: 502);
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
// Retries once when the connection itself fails (no response), e.g. a transient Windows TLS/LSA error.
static async Task<HttpResponseMessage> SendWithConnectRetryAsync(HttpClient http, Func<HttpRequestMessage> build, ILogger log, CancellationToken ct)
{
    try { return await http.SendAsync(build(), ct); }
    catch (HttpRequestException ex) when (ex.StatusCode is null)
    {
        log.LogWarning(ex, "Agent Builder connection failed; retrying once");
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return await http.SendAsync(build(), ct);
    }
}

// Pulls a named Task-agent outcome (e.g. solutionMapping) out of an invoke-task response.
static JsonElement? ExtractNamedOutcome(string json, string name)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty(name, out var a)) return a.Clone();
        if (root.TryGetProperty("outcomes", out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(name, out var b)) return b.Clone();
        var text = ExtractReply(json);
        if (!string.IsNullOrEmpty(text) && text.TrimStart().StartsWith("{"))
        {
            using var inner = JsonDocument.Parse(text);
            return inner.RootElement.TryGetProperty(name, out var c) ? c.Clone() : inner.RootElement.Clone();
        }
    }
    catch { }
    return null;
}

// Best-effort extraction of a Task agent's structured outcome (the `businessContext` object) from the
// invoke-task response. The exact shape isn't documented, so try the known candidates in order and
// return the first JSON object found; the endpoint also returns `raw` so the real shape can be inspected.
static JsonElement? ExtractTaskOutcome(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // 1) { businessContext: {...} }  or  { outcomes: { businessContext: {...} } }
        if (root.TryGetProperty("businessContext", out var bc1)) return bc1.Clone();
        if (root.TryGetProperty("outcomes", out var outc) && outc.ValueKind == JsonValueKind.Object
            && outc.TryGetProperty("businessContext", out var bc2)) return bc2.Clone();
        // 2) output[] -> message -> content[] -> output_text holding a JSON string (same as the chat shape).
        var text = ExtractReply(json);
        if (!string.IsNullOrEmpty(text) && (text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("[")))
        {
            try
            {
                using var inner = JsonDocument.Parse(text);
                // Agent Builder wraps Task outcomes by name: {"businessContext": {...}}.
                if (inner.RootElement.ValueKind == JsonValueKind.Object
                    && inner.RootElement.TryGetProperty("businessContext", out var bc3)) return bc3.Clone();
                return inner.RootElement.Clone();
            }
            catch { }
        }
        // 3) choices[0].message.content as a JSON string.
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var msg = choices[0];
            if (msg.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                if (!string.IsNullOrEmpty(s)) { try { using var inner = JsonDocument.Parse(s); return inner.RootElement.Clone(); } catch { } }
            }
        }
    }
    catch { }
    return null;
}

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

