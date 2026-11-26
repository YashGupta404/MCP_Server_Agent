using System.Text.Json;

// Request/response DTOs for the BFF endpoints and the IAM token response. Moved out of Program.cs;
// records are unchanged so JSON binding and all call sites keep working exactly as before.

sealed record WorkerMatch(
    string? Wid,
    string? Name,
    string? EmployeeId,
    string? BusinessTitle,
    string? SupervisoryOrganization);

sealed record ChatRequest(string Message, string? ConversationId, ChatAttachment[]? Attachments,
    string? BusinessObjectType, string? BusinessObjectId);

sealed record ChatAttachment(string? Name, string? Mime, string DataBase64);

sealed record ContextRequest(string BusinessObjectType, string BusinessObjectId, bool? OnlyMine, bool? Direct);

// AI context extraction: the extension captures the active tab and posts it here; the BFF invokes the
// context_extractor Task agent (Agent Builder) which returns the detected business object + type + id.
// PageHtml is a cleaned DOM structure used by the agent to author xpath/css extraction rules.
sealed record ContextExtractRequest(string? PageUrl, string? PageTitle, string? PageContent, string? PageHtml);

// KE auto-config (PoC): sample documents the admin drops in the plugin; the BFF runs KE classify/NER
// and generates a solution-config mapping fragment from the known ECM keyword catalog.
sealed record AutoConfigRequest(ChatAttachment[]? Attachments, string? BusinessObjectType, string? BusinessObjectId);

// Agent-driven mapping: sample docs + the context agent's businessContext + the captured LOB page text.
sealed record AgentMapRequest(ChatAttachment[]? Attachments, JsonElement? BusinessContext, string? PageContent);

// Zero-setup capture: the reviewed agent mapping (doctype + record field + field values) + the file + the record.
// The BFF writes the capture-only config entry, then uploads with these values.
sealed record CaptureCommitRequest(
    ChatAttachment[]? Attachments,
    string? BusinessObjectType,
    string? BusinessObjectId,
    string? DocumentTypeId,
    string? RecordFieldId,
    CaptureFieldValue[]? Fields);

sealed record CaptureFieldValue(string? Id, string? Value);

sealed record SystemConfigRequest(string FriendlyName);

sealed record UploadRequest(string BusinessObjectType, string BusinessObjectId, string EcmContentTypeName, ChatAttachment[]? Attachments, JsonElement[]? AdditionalAttributes);

// Server-side query execution (native keyword search). One optional keyword filter for now.
sealed record QueryExecuteRequest(
    string BusinessObjectType,
    string? QueryId,
    string? BusinessObjectId,
    string? FilterFieldId,
    string? FilterValue,
    string? FilterOperator);

// Workday capture: file(s) + the documentType and the record-identifying business-object attributes.
// BusinessObjectAttributes is passed through verbatim as a JSON array to the MCP capture_document tool.
sealed record CaptureRequest(
    string? BusinessObjectType,
    string DocumentTypeId,
    JsonElement[]? BusinessObjectAttributes,
    JsonElement[]? AdditionalAttributes,
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
