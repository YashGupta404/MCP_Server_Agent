namespace KePoc;

/// Binds the "Ke" section from user-secrets / appsettings / environment.
public sealed class KeOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string TokenEndpoint { get; set; } = "https://auth.staging.app.hyland.com/idp/connect/token";
    public string Scope { get; set; } = "environment_authorization";

    // KE Staging base URLs (CIN Service Endpoint Catalog, verified 2026-09-03).
    // If Context calls return 401, try the KE IAM token host instead:
    //   https://auth.iam.staging.experience.hyland.com/idp/connect/token
    public string ContextBaseUrl { get; set; } = "https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/context-enrichment";
    public string DataCurationBaseUrl { get; set; } = "https://knowledge-enrichment.ai.staging.app.hyland.com/latest/api/data-curation";

    // Candidate classes for textClassification. For the real pipeline these come from the UCEB
    // document-type catalog (list_document_types?fetchMetadata=true); hard-coded here for the PoC.
    public string[] ContextClasses { get; set; } =
        { "change request", "incident", "invoice", "purchase order", "contract", "resume", "other" };
    public int SummaryMaxWords { get; set; } = 50;

    public string[] Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientId)) missing.Add("Ke:ClientId");
        if (string.IsNullOrWhiteSpace(ClientSecret)) missing.Add("Ke:ClientSecret");
        if (string.IsNullOrWhiteSpace(TokenEndpoint)) missing.Add("Ke:TokenEndpoint");
        if (string.IsNullOrWhiteSpace(Scope)) missing.Add("Ke:Scope");
        return missing.ToArray();
    }
}
