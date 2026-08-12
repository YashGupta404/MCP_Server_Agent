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
var sessions = app.Services.GetRequiredService<SessionStore>();
var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
var log = app.Logger;

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
    return Results.Json(new { session = sessionId });
});

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
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "list_documents", new
        {
            businessObjectId = req.BusinessObjectId,
            businessObjectType = req.BusinessObjectType,
            onlyMine = req.OnlyMine ?? false,
        }, log, cts.Token);

        var documents = McpJsonRpc.ParseDocumentList(text);
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
        return sb.ToString();
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

            var name = attributes.Count > 0 ? attributes.Values.First() : docId;
            docs.Add(new { docId, name, attributes });
        }

        return docs.ToArray();
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

sealed record ChatRequest(string Message, string? ConversationId, ChatAttachment[]? Attachments);

sealed record ChatAttachment(string? Name, string? Mime, string DataBase64);

sealed record ContextRequest(string BusinessObjectType, string BusinessObjectId, bool? OnlyMine);

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
