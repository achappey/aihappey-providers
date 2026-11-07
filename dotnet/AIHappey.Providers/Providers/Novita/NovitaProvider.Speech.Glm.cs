using AIHappey.Common.Model.Providers.Novita;
using System.Text.Json;
using System.Text;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    private async Task<SpeechResponse> SpeechRequestGlmTts(
         SpeechRequest request,
         CancellationToken cancellationToken)
    {
        var metadata =
            request.GetProviderMetadata<NovitaSpeechProviderMetadata>(GetIdentifier());
        var glmMetadata = metadata?.Glm is { ValueKind: JsonValueKind.Object } options
            ? options
            : default;

        if (metadata?.Glm is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            throw new JsonException("Novita provider metadata 'glm' must be an object or null.");

        var payload = new Dictionary<string, object?>();
        if (glmMetadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in glmMetadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        var voice =
            request.Voice
            ?? GetNovitaGlmMetadataValue<string>(glmMetadata, "voice")
            ?? "tongtong"; // docs default :contentReference[oaicite:1]{index=1}

        var responseFormat =
            (request.OutputFormat
                ?? GetNovitaGlmMetadataValue<string>(glmMetadata, "response_format")
                ?? "pcm").ToLowerInvariant();

        // GLM endpoint only supports wav|pcm :contentReference[oaicite:2]{index=2}
        if (responseFormat is not ("wav" or "pcm"))
            responseFormat = "pcm";

        var speed = request.Speed
            ?? GetNovitaGlmMetadataValue<double?>(glmMetadata, "speed")
            ?? 1.0;
        if (speed < 0.5) speed = 0.5;
        if (speed > 2.0) speed = 2.0; // docs range :contentReference[oaicite:3]{index=3}

        var volume = GetNovitaGlmMetadataValue<double?>(glmMetadata, "volume") ?? 1.0;
        if (volume <= 0) volume = 1.0;
        if (volume > 10) volume = 10; // docs range :contentReference[oaicite:4]{index=4}

        SetNovitaGlmPayloadValue(payload, "input", request.Text ?? "");
        SetNovitaGlmPayloadValue(payload, "voice", voice);
        SetNovitaGlmPayloadValue(payload, "speed", speed);
        SetNovitaGlmPayloadValue(payload, "volume", volume);
        SetNovitaGlmPayloadValue(payload, "response_format", responseFormat);
        SetNovitaGlmPayloadValue(
            payload,
            "watermark_enabled",
            GetNovitaGlmMetadataValue<bool?>(glmMetadata, "watermark_enabled"));

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl + request.Model)
        {
            Content = content
        };
        ApplyAuthHeader(httpRequest);
        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Novita GLM-TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        // Response is binary audio :contentReference[oaicite:5]{index=5}
        var warnings = new List<object>();

        // For browser playback: prefer wav. PCM is raw; many players won’t like a data-url of it.
        var mime = responseFormat == "wav"
            ? "audio/wav"
            : "application/octet-stream";

        if (responseFormat == "pcm")
            warnings.Add("GLM-TTS returned pcm (raw). If you want easy browser playback, request response_format=wav.");

        var base64 = Convert
            .ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = responseFormat ?? "pcm"
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
            }
        };
    }

    private static bool IsGlmTtsModel(string? model)
           => string.Equals(model, "glm-tts", StringComparison.OrdinalIgnoreCase);

    private static T? GetNovitaGlmMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return default;

        JsonElement value = default;
        var found = false;

        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
        }

        return found
            ? value.Deserialize<T>(JsonSerializerOptions.Web)
            : default;
    }

    private static void SetNovitaGlmPayloadValue(
        Dictionary<string, object?> payload,
        string name,
        object? value)
    {
        var existingName = payload.Keys.FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existingName is not null)
            payload.Remove(existingName);

        payload[name] = value;
    }

}
