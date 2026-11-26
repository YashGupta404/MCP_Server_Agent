using System.Text.Json.Serialization;

namespace KePoc;

// The "known" ECM keyword catalog — simulates UCEB list_document_types?fetchMetadata=true
// (or the set the customer provides). This is the target vocabulary the generator maps to.

public sealed class EcmCatalog
{
    [JsonPropertyName("doctypes")] public List<EcmDoctype> Doctypes { get; set; } = new();
}

public sealed class EcmDoctype
{
    [JsonPropertyName("contentTypeId")] public string ContentTypeId { get; set; } = "";
    [JsonPropertyName("contentTypeLabel")] public string ContentTypeLabel { get; set; } = "";
    [JsonPropertyName("keywords")] public List<EcmKeyword> Keywords { get; set; } = new();
}

public sealed class EcmKeyword
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("dataType")] public string DataType { get; set; } = "String";
    // Optional role hint for opaque keyword ids (OnBase numeric): "recordId" | "owner" | "documentName".
    [JsonPropertyName("role")] public string? Role { get; set; }
}
