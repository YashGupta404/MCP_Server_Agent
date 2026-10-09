// Strongly-typed options bound from configuration (appsettings + user-secrets) in Program.cs via
// Configure<T>(...). Secrets (client secrets, API keys) are kept in user-secrets, never in appsettings.

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
