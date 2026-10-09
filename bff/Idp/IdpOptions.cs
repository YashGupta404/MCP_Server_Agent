// IDP (Intelligent Document Processing) configuration. Bound from the "Idp" section of configuration
// (appsettings + user-secrets) in Program.cs via Configure<IdpOptions>(...).
sealed class IdpOptions
{
    // IDP (Intelligent Document Processing) REST API base, e.g. https://api.idp.staging.app.hyland.com.
    public string BaseUrl { get; set; } = "https://api.idp.staging.app.hyland.com";
    // General Purpose zero-shot classification execution profile (name=class, inline definitions).
    public string ClassificationProfileId { get; set; } = "078abc02-212b-42fa-92fb-f42edd6bb42d";
    public string ClassificationProfileVersion { get; set; } = "3.0";
    // Recognition (OCR) "Core Profile" — required before extraction.
    public string RecognitionProfileId { get; set; } = "fe81797f-2d99-450a-92f1-fe4cde5bfc79";
    public string RecognitionProfileVersion { get; set; } = "1.0";
    // Publicly reachable base URL of THIS BFF (e.g. a devtunnel) so IDP's cloud service can fetch
    // the staged file bytes as the classification sourceUrl. Empty => classify is disabled.
    public string PublicBaseUrl { get; set; } = "";
    // File the IDP bearer token is read from (Option C demo: produced by bff/idp-feature-test.ps1).
    // Empty => %TEMP%/idp_token.txt.
    public string TokenFile { get; set; } = "";
    // For silent auto-refresh when the token expires (no browser): the saved refresh token file +
    // the IDP app credentials + token endpoint. ClientId/ClientSecret come from user-secrets.
    public string RefreshTokenFile { get; set; } = "";
    public string TokenEndpoint { get; set; } = "https://auth.staging.app.hyland.com/idp/connect/token";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
