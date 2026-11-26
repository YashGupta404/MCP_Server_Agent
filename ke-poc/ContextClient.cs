using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KePoc;

/// KE Context API client (direct-upload flow): presign -> PUT bytes -> process -> poll results.
public sealed class ContextClient(HttpClient http, TokenService tokens, KeOptions options)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private string Base => options.ContextBaseUrl.TrimEnd('/');

    private async Task<HttpRequestMessage> AuthedAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    /// GET /files/upload/presigned-url?contentType=...
    public async Task<PresignedUrlResult> GetUploadUrlAsync(string contentType, CancellationToken ct = default)
    {
        var url = $"{Base}/files/upload/presigned-url?contentType={Uri.EscapeDataString(contentType)}";
        using var req = await AuthedAsync(HttpMethod.Get, url, ct);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"presigned-url failed ({(int)resp.StatusCode}): {body}");
        return JsonSerializer.Deserialize<PresignedUrlResult>(body, Json)
               ?? throw new InvalidOperationException("Empty presigned-url response.");
    }

    /// PUT raw bytes to the signed storage URL. No Hyland bearer token is sent to storage.
    public async Task UploadAsync(string presignedUrl, string contentType, byte[] bytes, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, presignedUrl)
        {
            Content = new ByteArrayContent(bytes),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Upload PUT failed ({(int)resp.StatusCode}): {body}");
        }
    }

    /// POST /content/process -> processingId.
    public async Task<string> ProcessAsync(string objectKey, string[] classes, int summaryWords, CancellationToken ct = default)
    {
        var request = new ProcessRequest
        {
            ObjectKeys = { new ObjectKeyRef { Path = objectKey } },
            Actions = new ProcessActions
            {
                TextClassification = classes.Length > 0
                    ? new TextClassificationAction { Classes = classes }
                    : null,
                NamedEntityRecognitionText = new { },
                TextSummarization = new TextSummarizationAction { MaxWordCount = summaryWords },
            },
        };

        using var req = await AuthedAsync(HttpMethod.Post, $"{Base}/content/process", ct);
        req.Content = new StringContent(JsonSerializer.Serialize(request, Json), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"/content/process failed ({(int)resp.StatusCode}): {body}");
        var accepted = JsonSerializer.Deserialize<ProcessAccepted>(body, Json);
        return accepted?.ProcessingId
               ?? throw new InvalidOperationException("No processingId in /content/process response.");
    }

    /// GET /content/process/{id}/results — poll until 200 (202 = keep polling).
    public async Task<ContentsProcessingResults> PollResultsAsync(string processingId, CancellationToken ct = default)
    {
        var url = $"{Base}/content/process/{Uri.EscapeDataString(processingId)}/results";
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(300);

        while (true)
        {
            using var req = await AuthedAsync(HttpMethod.Get, url, ct);
            using var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<ContentsProcessingResults>(body, Json)
                       ?? throw new InvalidOperationException("Empty results body.");
            }
            if (resp.StatusCode != HttpStatusCode.Accepted)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"results poll failed ({(int)resp.StatusCode}): {body}");
            }
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Context processing {processingId} did not finish within 300s.");

            await Task.Delay(delay, ct);
            delay = delay < maxDelay ? delay + TimeSpan.FromSeconds(1) : maxDelay;
        }
    }

    public static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };
}
