// Request DTOs for the IDP endpoints (see Idp/IdpEndpoints.cs). ChatAttachment is defined in Program.cs
// and reused here so the plugin sends files in the same shape as /api/chat and /api/upload.

sealed record IdpClassifyRequest(ChatAttachment[]? Attachments, string? BusinessObjectType);

// One file + the metadata fields (id+name/label) to extract values for.
sealed record IdpExtractRequest(ChatAttachment? Attachment, IdpExtractField[]? Fields);
sealed record IdpExtractField(string Id, string Name);
