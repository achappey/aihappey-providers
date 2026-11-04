using System.Text.Json;
using System.Text;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
           TranscriptionRequest request,
           CancellationToken cancellationToken = default)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        // Novita expects:
        // - file: base64 string OR URL
        // - application/json (not multipart)

        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            _ = metadata.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web);

        var payload = new Dictionary<string, object?>();

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (IsMappedTranscriptionOption(property.Name))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }

            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, "prompt", StringComparison.OrdinalIgnoreCase))
                {
                    var prompt = property.Value.Deserialize<string>(JsonSerializerOptions.Web);
                    if (string.IsNullOrWhiteSpace(prompt))
                        payload.Remove("prompt");
                    else
                        payload["prompt"] = prompt;
                }
                else if (string.Equals(property.Name, "hotwords", StringComparison.OrdinalIgnoreCase))
                {
                    var hotwords = property.Value.Deserialize<IEnumerable<string>>(JsonSerializerOptions.Web);
                    if (hotwords?.Any() != true)
                        payload.Remove("hotwords");
                    else
                        payload["hotwords"] = hotwords.ToArray();
                }
            }
        }

        payload["file"] = request.Audio?.ToString()
            ?? throw new InvalidOperationException("Audio is required");

        var requestBody = JsonSerializer.Serialize(payload);

        using var content = new StringContent(
            requestBody,
            Encoding.UTF8,
            "application/json"
        );

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.novita.ai/v3/glm-asr")
        {
            Content = content
        };
        ApplyAuthHeader(httpRequest);
        using var resp = await _client.SendAsync(httpRequest, cancellationToken);

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Novita STT failed ({(int)resp.StatusCode}): {json}");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return ConvertNovitaResponse(root, requestBody,
            GetIdentifier(), request.Model, resp.GetHeaders());
    }

    private static bool IsMappedTranscriptionOption(string name)
        => string.Equals(name, "prompt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "hotwords", StringComparison.OrdinalIgnoreCase);

    private static TranscriptionResponse ConvertNovitaResponse(JsonElement root,
        string requestBody, string providerId, string model, IDictionary<string, string>? headers = null)
    {
        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : "",

            // Novita returns no segments
            Segments = [],
            ProviderMetadata = providerId
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(providerId),
                Body = root.Clone()
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }
}
