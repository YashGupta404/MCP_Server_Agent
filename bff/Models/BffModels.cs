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

sealed record ContextRequest(string BusinessObjectType, string BusinessObjectId, bool? OnlyMine);

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
