using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model;
      
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (!string.Equals(model, "qwen3-asr-flash", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Alibaba transcription model '{model}' is not supported.");
        }

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (!MediaContentHelpers.TryParseDataUrl(audioString, out _, out _))
            throw new ArgumentException("Audio must be provided as a data URL (data:audio/...;base64,...).", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = audioString }
                        }
                    }
                }
            },
            ["stream"] = false
        };

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata is { } providerOptions)
        {
            if (providerOptions.ValueKind != JsonValueKind.Object)
                throw new JsonException("Alibaba provider options must be a JSON object.");

            var asrOptions = new Dictionary<string, object?>(StringComparer.Ordinal);
            JsonElement? nonObjectAsrOptions = null;
            if (TryGetPropertyIgnoreCase(providerOptions, "asr_options", out var asrOptionsElement))
            {
                if (asrOptionsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in asrOptionsElement.EnumerateObject())
                        asrOptions[property.Name] = property.Value.Clone();
                }
                else
                {
                    nonObjectAsrOptions = asrOptionsElement.Clone();
                }
            }

            if (TryGetPropertyIgnoreCase(providerOptions, "language", out var languageElement))
            {
                var language = languageElement.Deserialize<string>(JsonOpts);
                if (!string.IsNullOrWhiteSpace(language))
                    asrOptions["language"] = language;
            }

            if (TryGetPropertyIgnoreCase(providerOptions, "enable_itn", out var enableItnElement))
            {
                var enableItn = enableItnElement.Deserialize<bool?>(JsonOpts);
                if (enableItn is not null)
                    asrOptions["enable_itn"] = enableItn.Value;
            }

            if (nonObjectAsrOptions is { } rawAsrOptionsValue)
            {
                if (asrOptions.Count > 0)
                    throw new JsonException("Alibaba asr_options must be a JSON object when mapped options are supplied.");

                payload["asr_options"] = rawAsrOptionsValue;
            }

            foreach (var property in providerOptions.EnumerateObject())
            {
                if (string.Equals(property.Name, "language", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "enable_itn", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "asr_options", StringComparison.OrdinalIgnoreCase))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }

            if (asrOptions.Count > 0)
                payload["asr_options"] = asrOptions;
        }

        var requestBody = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, "compatible-mode/v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Alibaba STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model, requestBody);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        var found = false;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model, string requestBody)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string text = string.Empty;
        string? language = null;
        float? durationSeconds = null;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content))
                    text = content.GetString() ?? string.Empty;

                if (message.TryGetProperty("annotations", out var annotations) &&
                    annotations.ValueKind == JsonValueKind.Array &&
                    annotations.GetArrayLength() > 0)
                {
                    var annotation = annotations[0];
                    if (annotation.TryGetProperty("language", out var langEl))
                        language = langEl.GetString();
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("seconds", out var secondsEl) &&
            secondsEl.ValueKind == JsonValueKind.Number)
        {
            durationSeconds = secondsEl.GetSingle();
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = durationSeconds,
            ProviderMetadata = "alibaba".CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = root.Clone()
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }
}
