using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferenceClub;

public partial class InferenceClubProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, object?> Object(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("InferenceClub provider options must be a JSON object.");
        return value.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private Dictionary<string, object?> ProviderOptions(Dictionary<string, JsonElement>? options)
        => options is not null && options.TryGetValue(GetIdentifier(), out var value) ? Object(value) : [];

    private static void Set(Dictionary<string, object?> payload, string key, object? value)
    {
        if (value is not null)
            payload[key] = value;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        foreach (var (name, value) in CreateRequestHeaders())
            request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path,
            payload is null ? null : JsonContent.Create(payload, options: JsonOptions));
        using var response = await _client.SendAsync(request, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"InferenceClub API error ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string JobId(JsonElement root)
    {
        if (root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var value = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        throw new InvalidOperationException($"InferenceClub response has no job id: {root}");
    }

    private Task<JsonElement> GetJobAsync(string id, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Get, $"jobs/{Uri.EscapeDataString(id)}", null, cancellationToken);

    private async Task<JsonElement> WaitForResultAsync(JsonElement result, CancellationToken cancellationToken)
    {
        if (ReadString(result, "object") != "inference.job")
            return result;
        var id = JobId(result);
        var delay = TimeSpan.FromSeconds(3);
        while (true)
        {
            result = await GetJobAsync(id, cancellationToken);
            switch (ReadString(result, "status"))
            {
                case "PROCESSED":
                    return result;
                case "QUEUED":
                case "PROCESSING":
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 1.5));
                    break;
                case "FAILED":
                case "CANCELED":
                    throw new InvalidOperationException($"InferenceClub job did not complete: {result}");
                default:
                    throw new InvalidOperationException($"Unknown InferenceClub job status: {result}");
            }
        }
    }

    private static IEnumerable<JsonElement> Outputs(JsonElement result)
    {
        if (result.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            return media.EnumerateArray().ToArray();
        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().ToArray();
        throw new InvalidOperationException($"InferenceClub response has no media: {result}");
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("InferenceClub media must have an HTTP(S) URL.", nameof(url));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Private assets are owner-gated; never send the key to external storage or reference URLs.
        if (uri.Scheme == "https" && uri.Host == "api.inference.club" && uri.IsDefaultPort
            && uri.AbsolutePath.StartsWith("/api/inference/assets/", StringComparison.Ordinal))
            foreach (var (name, value) in CreateRequestHeaders())
                request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _mediaClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            throw new InvalidOperationException("InferenceClub media download was empty.");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    private async Task<(byte[] Bytes, string MediaType)> ReadFileAsync(string? data, string? mediaType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return await DownloadAsync(data, cancellationToken);
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = data.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new ArgumentException("InferenceClub input data URLs must be base64 encoded.");
            mediaType ??= data[5..marker];
        }
        return (Convert.FromBase64String(data.RemoveDataUrlPrefix()), mediaType ?? "application/octet-stream");
    }

    private HeaderResponseData ResponseInfo(string model) => new()
    {
        Timestamp = DateTime.UtcNow,
        ModelId = model.ToModelId(GetIdentifier())
    };

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync(HttpMethod.Get, "models", null, cancellationToken);
        var models = new List<Model>();
        foreach (var item in result.GetProperty("data").EnumerateArray())
        {
            var id = ReadString(item, "id") ?? throw new InvalidOperationException("InferenceClub model has no id.");
            var service = ReadString(item, "service_type");
            var type = service switch
            {
                "llm" => "language",
                "image" => "image",
                "stt" => "transcription",
                "tts" or "music" => "speech",
                "video" => "video",
                _ => null
            };
            if (type is null)
                continue;
            var tags = new List<string> { service! };
            foreach (var name in new[] { "supported_features", "input_modalities", "output_modalities" })
                if (item.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
                    tags.AddRange(values.EnumerateArray().Select(v => v.GetString()!));
            models.Add(new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = id,
                OwnedBy = ReadString(item, "owned_by") ?? GetIdentifier(),
                Type = type,
                ContextWindow = item.TryGetProperty("context_length", out var context)
                    && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out var length) ? length : null,
                Tags = tags.Distinct().ToArray()
            });
        }
        return models;
    }
}
