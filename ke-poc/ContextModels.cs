using System.Text.Json.Serialization;

namespace KePoc;

// DTOs for the KE Context API (from the verified Staging swagger-context-api.yaml).

public sealed class PresignedUrlResult
{
    [JsonPropertyName("presignedUrl")] public string? PresignedUrl { get; set; }
    [JsonPropertyName("objectKey")] public string? ObjectKey { get; set; }
}

// Request body for POST /content/process (context.api/v2, direct-upload path flow).
public sealed class ProcessRequest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "context.api/v2";
    [JsonPropertyName("objectKeys")] public List<ObjectKeyRef> ObjectKeys { get; set; } = new();
    [JsonPropertyName("actions")] public ProcessActions Actions { get; set; } = new();
}

public sealed class ObjectKeyRef
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public sealed class ProcessActions
{
    [JsonPropertyName("textClassification")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextClassificationAction? TextClassification { get; set; }

    [JsonPropertyName("namedEntityRecognitionText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? NamedEntityRecognitionText { get; set; }

    [JsonPropertyName("textSummarization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextSummarizationAction? TextSummarization { get; set; }
}

public sealed class TextClassificationAction
{
    [JsonPropertyName("classes")] public string[] Classes { get; set; } = Array.Empty<string>();
    [JsonPropertyName("instructions")] public object Instructions { get; set; } = new { };
}

public sealed class TextSummarizationAction
{
    [JsonPropertyName("maxWordCount")] public int MaxWordCount { get; set; } = 50;
}

public sealed class ProcessAccepted
{
    [JsonPropertyName("processingId")] public string? ProcessingId { get; set; }
}

// GET /content/process/{id}/results envelope.
public sealed class ContentsProcessingResults
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset? Timestamp { get; set; }
    [JsonPropertyName("results")] public List<S3ContentProcessingResult>? Results { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("inProgress")] public bool InProgress { get; set; }
}

public sealed class S3ContentProcessingResult
{
    [JsonPropertyName("objectKey")] public string? ObjectKey { get; set; }
    [JsonPropertyName("textMetadata")] public StringObjectDictionaryResult? TextMetadata { get; set; }
    [JsonPropertyName("textSummary")] public StringResult? TextSummary { get; set; }
    [JsonPropertyName("textClassification")] public StringResult? TextClassification { get; set; }
    [JsonPropertyName("namedEntityText")] public NerResult? NamedEntityText { get; set; }
}

public sealed class StringResult
{
    [JsonPropertyName("isSuccess")] public bool IsSuccess { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("error")] public ProcessingError? Error { get; set; }
}

public sealed class StringObjectDictionaryResult
{
    [JsonPropertyName("isSuccess")] public bool IsSuccess { get; set; }
    [JsonPropertyName("result")] public Dictionary<string, object>? Result { get; set; }
    [JsonPropertyName("error")] public ProcessingError? Error { get; set; }
}

// NER result: dictionary of entityType -> list of values.
public sealed class NerResult
{
    [JsonPropertyName("isSuccess")] public bool IsSuccess { get; set; }
    [JsonPropertyName("result")] public Dictionary<string, List<string>?>? Result { get; set; }
    [JsonPropertyName("error")] public ProcessingError? Error { get; set; }
}

public sealed class ProcessingError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
}
