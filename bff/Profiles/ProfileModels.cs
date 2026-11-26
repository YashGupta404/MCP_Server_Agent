using System.Text.Json.Nodes;

// Integration profile: identifies a LOB tenant (by URL) and holds one solution configuration per UCEB
// system config. UCEB has no LOB-tenant concept yet, so each profile should use its own system configs.
sealed class IntegrationProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Lob { get; set; } = "";
    public TenantIdentifier Identifier { get; set; } = new();
    public string LobTenantName { get; set; } = "";
    // LOB property that carries the record id (Salesforce "Id", ServiceNow "number"); written as import inputSource 1.
    public string RecordIdProperty { get; set; } = "";
    public List<BusinessObjectRef> BusinessObjects { get; set; } = new();
    public List<string> Systems { get; set; } = new();
    public string DefaultSystem { get; set; } = "";
    public Dictionary<string, SystemSolution> Solutions { get; set; } = new();
    // Wizard review state (section signatures / confidences); owned by the panel, stored as-is.
    public JsonObject? Wizard { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Dictionary<string, ApplyResult>? Applied { get; set; }
}

sealed class TenantIdentifier
{
    public string HostName { get; set; } = "";
    public string PathParam { get; set; } = "";
    public string QueryParam { get; set; } = "";
}

sealed class BusinessObjectRef
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Custom { get; set; }
}

sealed class SystemSolution
{
    public Dictionary<string, ObjectSolution> BusinessObjects { get; set; } = new();
}

sealed class ObjectSolution
{
    public List<DocTypeMapping> DocumentTypes { get; set; } = new();
}

sealed class DocTypeMapping
{
    public string DocumentTypeId { get; set; } = "";
    public string DocumentTypeName { get; set; } = "";
    public string? RecordKeywordId { get; set; }
    public List<KeywordMapping> Keywords { get; set; } = new();
    public string? QueryId { get; set; }
    public string? QueryName { get; set; }
}

sealed class KeywordMapping
{
    public string KeywordId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "String";
    public bool Required { get; set; }
    // lob | document | manual
    public string Source { get; set; } = "document";
}

sealed class ApplyResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Backup { get; set; }
    public DateTimeOffset At { get; set; }
}

sealed record ProfileMatchRequest(string? Url);
sealed record ProfileCatalogRequest(string? System, bool? Refresh);
sealed record ProfileSuggestRequest(
    string? Lob,
    string? RecordIdProperty,
    BusinessObjectRef[]? BusinessObjects,
    string[]? Systems,
    Dictionary<string, SystemSolution>? Solutions);
sealed record DuplicateSystemRequest(string? Source, string? FriendlyName, string? Description);
