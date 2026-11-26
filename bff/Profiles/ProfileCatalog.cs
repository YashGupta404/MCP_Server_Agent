using System.Collections.Concurrent;
using System.Text.Json.Nodes;

// Tenant identification: the LOB tenant is identified from the page URL (hostname, optional path/query).
static class TenantResolver
{
    public sealed record Tenant(string HostName, string Path, string Query, string Lob, string LobTenantName, string RecordIdProperty);

    public static Tenant? FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;
        var host = uri.Host.ToLowerInvariant();
        var lob = LobFromHost(host);
        return new Tenant(host, uri.AbsolutePath, uri.Query.TrimStart('?'), lob, $"{host.Split('.')[0]} · {LobLabel(lob)}", DefaultRecordIdProperty(lob));
    }

    public static string LobFromHost(string host) =>
        host.EndsWith(".force.com") || host.EndsWith(".salesforce.com") ? "salesforce"
        : host.EndsWith(".service-now.com") || host.EndsWith(".servicenow.com") ? "servicenow"
        : host.Contains("workday") ? "workday"
        : "other";

    public static string LobLabel(string lob) => lob switch
    {
        "salesforce" => "Salesforce",
        "servicenow" => "ServiceNow",
        "workday" => "Workday",
        _ => "Web application",
    };

    public static string DefaultRecordIdProperty(string lob) => lob switch
    {
        "salesforce" => "Id",
        "servicenow" => "number",
        "workday" => "WID",
        _ => "",
    };

    // The most specific profile whose hostname matches and whose optional path/query parts are present in the URL.
    // Two equally specific matches are ambiguous and return null.
    public static IntegrationProfile? Match(IEnumerable<IntegrationProfile> profiles, Tenant t)
    {
        static int Specificity(IntegrationProfile p) =>
            (string.IsNullOrWhiteSpace(p.Identifier.PathParam) ? 0 : 1) + (string.IsNullOrWhiteSpace(p.Identifier.QueryParam) ? 0 : 1);

        var queryParts = t.Query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var candidates = profiles
            .Where(p => string.Equals(p.Identifier.HostName.Trim(), t.HostName, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(p.Identifier.PathParam) || t.Path.Contains(p.Identifier.PathParam.Trim(), StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(p.Identifier.QueryParam) || queryParts.Contains(p.Identifier.QueryParam.Trim(), StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(Specificity)
            .ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count > 1 && Specificity(candidates[0]) == Specificity(candidates[1])) return null;
        return candidates[0];
    }

    public static bool SameIdentifier(TenantIdentifier a, TenantIdentifier b) =>
        string.Equals(a.HostName.Trim(), b.HostName.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.PathParam.Trim(), b.PathParam.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.QueryParam.Trim(), b.QueryParam.Trim(), StringComparison.OrdinalIgnoreCase);
}

// The MCP has ONE process-wide active system. Admin work on another system switches to it and back under a lock.
static class McpSystemScope
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<T> RunAsync<T>(IHttpClientFactory httpFactory, McpOptions mcp, string system, ILogger log,
        Func<Task<T>> work, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        string previous = "";
        try
        {
            previous = await ActiveAsync(httpFactory, mcp, log, ct);
            if (!string.Equals(previous, system, StringComparison.OrdinalIgnoreCase))
                await McpJsonRpc.CallToolAsync(httpFactory, mcp, "set_active_system_configuration", new { friendlyName = system }, log, ct);
            return await work();
        }
        finally
        {
            try
            {
                if (!string.Equals(previous, system, StringComparison.OrdinalIgnoreCase))
                    await McpJsonRpc.CallToolAsync(httpFactory, mcp, "set_active_system_configuration", new { friendlyName = previous }, log, CancellationToken.None);
            }
            catch (Exception ex) { log.LogError(ex, "McpSystemScope: could not restore active system '{System}'", previous); }
            Gate.Release();
        }
    }

    public static async Task<string> ActiveAsync(IHttpClientFactory httpFactory, McpOptions mcp, ILogger log, CancellationToken ct)
    {
        var text = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_active_system_configuration", new { }, log, ct);
        int a = text.IndexOf('\'');
        int b = a >= 0 ? text.IndexOf('\'', a + 1) : -1;
        var name = a >= 0 && b > a ? text[(a + 1)..b] : "";
        return name == "(none)" ? "" : name;
    }
}

// Per-system ECM catalog for the profile wizard: document types + keywords, saved queries, and the current
// solution config (cached 10 minutes; OnBase catalog reads are slow).
sealed record SystemCatalog(string System, EcmCatalog Doctypes, JsonArray Queries, JsonObject? SolutionData, DateTimeOffset At)
{
    private JsonObject? BusinessObjectConfig => SolutionData?["configurations"]?["businessObjectConfig"] as JsonObject;

    public string? RecordKeywordId => BusinessObjectConfig?["contextParams"]?["boContext"]?["ecmPropId"]?.ToString();
    public string? RecordIdProperty => BusinessObjectConfig?["contextParams"]?["boContext"]?["boProp"]?.ToString();

    public IEnumerable<JsonObject> ConfiguredEntries(string busObject) =>
        (BusinessObjectConfig?["additionalConfig"] as JsonArray)?.OfType<JsonObject>()
            .Where(e => string.Equals(e["busObject"]?.ToString(), busObject, StringComparison.OrdinalIgnoreCase))
        ?? Enumerable.Empty<JsonObject>();

    public IEnumerable<JsonObject> ConfiguredQueries(string busObject) =>
        (BusinessObjectConfig?["queryConfig"] as JsonArray)?.OfType<JsonObject>()
            .Where(e => string.Equals(e["busObject"]?.ToString(), busObject, StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => (e["queries"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        ?? Enumerable.Empty<JsonObject>();

    public JsonObject? Query(string? id) =>
        Queries.OfType<JsonObject>().FirstOrDefault(q => string.Equals(q["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase));

    public object ToDto() => new
    {
        system = System,
        doctypes = Doctypes.Doctypes.Select(d => new
        {
            id = d.ContentTypeId,
            name = d.ContentTypeName,
            keywords = d.Keywords.Select(k => new { id = k.Id, label = k.Label, dataType = k.DataType, required = k.Required }),
        }),
        queries = Queries,
        recordKeywordId = RecordKeywordId,
        recordIdProperty = RecordIdProperty,
        at = At,
    };
}

static class ProfileCatalog
{
    private static readonly ConcurrentDictionary<string, SystemCatalog> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static void Invalidate(string system) => Cache.TryRemove(system, out _);

    public static async Task<SystemCatalog> GetAsync(IHttpClientFactory httpFactory, McpOptions mcp, string system, bool refresh,
        ILogger log, CancellationToken ct)
    {
        if (!refresh && Cache.TryGetValue(system, out var cached) && DateTimeOffset.UtcNow - cached.At < Ttl) return cached;

        var catalog = await McpSystemScope.RunAsync(httpFactory, mcp, system, log, async () =>
        {
            var doctypes = await EcmDoctypeCatalog.FetchAsync(httpFactory, mcp, log, ct);
            // CIC "*-base-type" types only carry the LOB mixin; they can't be filed.
            doctypes.Doctypes.RemoveAll(d => d.ContentTypeName.EndsWith("-base-type", StringComparison.OrdinalIgnoreCase));

            JsonArray queries;
            try { queries = await EcmQueryCatalog.FetchAsync(httpFactory, mcp, log, ct); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "ProfileCatalog: query catalog failed for {System}", system);
                queries = new JsonArray();
            }

            var configText = await McpJsonRpc.CallToolAsync(httpFactory, mcp, "get_solution_configurations", new { }, log, ct);
            JsonObject? data = null;
            if (configText.TrimStart().StartsWith('{'))
            {
                var node = JsonNode.Parse(configText);
                data = (node?["data"] ?? node) as JsonObject;
            }
            return new SystemCatalog(system, doctypes, queries, data, DateTimeOffset.UtcNow);
        }, ct);

        Cache[system] = catalog;
        return catalog;
    }
}
