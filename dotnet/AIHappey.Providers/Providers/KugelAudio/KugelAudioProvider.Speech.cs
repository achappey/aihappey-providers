using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KugelAudio;

public partial class KugelAudioProvider
{
    private const string DefaultModel = "kugel-3.5";

    public async Task<SpeechResponse> SpeechRequestInternal(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Object)
            throw new JsonException("KugelAudio provider options must be a JSON object.");

        var payload = new JsonObject();
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        var model = NormalizeModel(request.Model);
        if (!string.IsNullOrWhiteSpace(model))
            SetProperty(payload, "model_id", JsonValue.Create(model));
        else if (!HasProperty(payload, "model_id"))
            SetProperty(payload, "model_id", JsonValue.Create(DefaultModel));

        SetProperty(payload, "text", JsonValue.Create(request.Text));

        if (!string.IsNullOrWhiteSpace(request.Voice))
        {
            if (!ulong.TryParse(request.Voice.Trim(), out var voiceId))
                throw new ArgumentException("KugelAudio voice must be a numeric voice_id.", nameof(request));
            SetProperty(payload, "voice_id", JsonValue.Create(voiceId));
        }
        else if (HasProperty(payload, "voice_id")
                 && GetProperty(payload, "voice_id") is JsonValue voiceValue
                 && voiceValue.TryGetValue<string>(out var voiceText))
        {
            if (!ulong.TryParse(voiceText, out var voiceId))
                throw new ArgumentException("KugelAudio providerOptions.voice_id must be numeric.", nameof(request));
            SetProperty(payload, "voice_id", JsonValue.Create(voiceId));
        }
        else if (!HasProperty(payload, "voice_id"))
        {
            throw new ArgumentException("KugelAudio voice is required. Provide voice or providerOptions.kugelaudio.voice_id.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
            SetProperty(payload, "language", JsonValue.Create(request.Language));
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            SetProperty(payload, "output_format", JsonValue.Create(request.OutputFormat));

        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No KugelAudio API key.");

        var requestUri = "v1/tts/generate";
        if (apiKey.StartsWith("eu-", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = apiKey[3..];
            requestUri = "https://api.eu.kugelaudio.com/v1/tts/generate";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        var now = DateTime.UtcNow;
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"KugelAudio TTS failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var responseFormat = response.Headers.TryGetValues("X-Audio-Format", out var formats)
            || response.Content.Headers.TryGetValues("X-Audio-Format", out formats)
                ? formats.FirstOrDefault()
                : null;
        var format = ResolveFormat(responseFormat, ReadPayloadString(payload, "output_format"));
        var mimeType = ResolveMimeType(response.Content.Headers.ContentType?.MediaType, format);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                Format = format,
                MimeType = mimeType
            },
            Warnings = warnings,
            Request = new() { Body = payload },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = $"{GetIdentifier()}/{ReadPayloadString(payload, "model_id")}"
            }
        };
    }

    private static string? NormalizeModel(string? model)
    {
        var normalized = model?.Trim();
        const string prefix = "kugelaudio/";
        return normalized?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static string? ReadPayloadString(JsonObject payload, string name)
        => GetProperty(payload, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? GetProperty(JsonObject payload, string name)
        => payload.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool HasProperty(JsonObject payload, string name)
        => payload.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));

    private static void SetProperty(JsonObject payload, string name, JsonNode? value)
    {
        foreach (var key in payload.Select(pair => pair.Key)
                     .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            payload.Remove(key);
        payload[name] = value;
    }

    private static string ResolveFormat(string? responseFormat, string? requestedFormat)
    {
        var format = (responseFormat ?? requestedFormat ?? "pcm").Trim().ToLowerInvariant();
        if (format.Contains("alaw", StringComparison.Ordinal))
            return "alaw";
        if (format.Contains("mulaw", StringComparison.Ordinal)
            || format.Contains("ulaw", StringComparison.Ordinal)
            || format.Contains("g711", StringComparison.Ordinal))
            return "ulaw";
        if (format.Contains("pcm", StringComparison.Ordinal))
            return "pcm";
        return format;
    }

    private static string ResolveMimeType(string? contentType, string format)
    {
        if (format == "pcm")
            return "audio/pcm";
        if (format is "ulaw" or "alaw")
            return "audio/basic";
        if (format is "wav" or "wave")
            return "audio/wav";
        if (format is "mp3" or "mpeg")
            return "audio/mpeg";
        if (format is "ogg" or "opus")
            return "audio/ogg";
        if (format == "flac")
            return "audio/flac";
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return contentType;
        return "application/octet-stream";
    }
}
