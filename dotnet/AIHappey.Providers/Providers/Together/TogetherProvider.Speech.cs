using AIHappey.Core.AI;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider
{
    private static readonly JsonSerializerOptions SpeechSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Together does not expose these (currently) on /audio/speech.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyTogetherSpeechOptions(metadata);

        // Accept both: "together/<model>" and "<model>".
        var modelName = NormalizeTogetherModelName(request.Model);

        var voice = ResolveVoice(modelName, request, metadata);
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' is required for Together TTS.", nameof(request));

        var responseFormat =
            request.OutputFormat
            ?? GetTogetherSpeechOption<string?>(metadata, "response_format")
            ?? "wav";

        var language = request.Language ?? GetTogetherSpeechOption<string?>(metadata, "language");

        payload["model"] = modelName;
        payload["input"] = request.Text;
        payload["voice"] = voice;
        payload["language"] = language;
        payload["response_encoding"] = GetTogetherSpeechOption<string?>(metadata, "response_encoding");
        payload["sample_rate"] = GetTogetherSpeechOption<int?>(metadata, "sample_rate");
        payload["stream"] = false;

        // Null-ignore settings do not omit dictionary entries. Keep unknown metadata nulls intact.
        foreach (var name in new[] { "language", "response_encoding", "sample_rate" })
        {
            if (payload[name] is null)
                payload.Remove(name);
        }

        if (!string.IsNullOrWhiteSpace(responseFormat))
            payload["response_format"] = responseFormat;
        else
            payload.Remove("response_format");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechSettings),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        // Avoid mutating shared HttpClient headers.
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Together TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        var mime = ResolveSpeechMimeType(responseFormat, resp.Content.Headers.ContentType?.MediaType);
        var audio = Convert.ToBase64String(bytes);

        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata();

        return new SpeechResponse
        {
            Audio = new()
            {
                MimeType = mime,
                Base64 = audio,
                Format = !string.IsNullOrEmpty(responseFormat) ? responseFormat : "wav"
            },
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private string NormalizeTogetherModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        // Only strip prefix if the provider is explicitly present.
        // NOTE: Together model IDs themselves contain '/', e.g. "canopylabs/orpheus-3b-0.1-ft".
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model.SplitModelId().Model
            : model.Trim();
    }

    private static string? ResolveVoice(
        string modelName,
        SpeechRequest request,
        JsonElement? metadata)
    {
        if (!string.IsNullOrWhiteSpace(request.Voice))
            return request.Voice;

        var modelFamily = modelName.Split('/', 2)[0];
        if (metadata is { ValueKind: JsonValueKind.Object }
            && TryGetTogetherSpeechOption(metadata.Value, modelFamily, out var familyOptions)
            && familyOptions.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (familyOptions.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Together speech option '{modelFamily}' must be an object.");

            var voice = GetTogetherSpeechOption<string?>(familyOptions, "voice");
            if (!string.IsNullOrWhiteSpace(voice))
                return voice;
        }

        return GetTogetherSpeechOption<string?>(metadata, "voice");
    }

    private static Dictionary<string, object?> CopyTogetherSpeechOptions(JsonElement? metadata)
    {
        if (metadata is null || metadata.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        if (metadata.Value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Together speech provider options must be a JSON object.");

        var options = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in metadata.Value.EnumerateObject())
            options[property.Name] = property.Value.Clone();
        return options;
    }

    private static T? GetTogetherSpeechOption<T>(JsonElement? metadata, string name)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object }
            || !TryGetTogetherSpeechOption(metadata.Value, name, out var value))
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static bool TryGetTogetherSpeechOption(
        JsonElement metadata,
        string name,
        out JsonElement value)
    {
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ResolveSpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "raw" => contentType ?? "application/octet-stream",
            _ => "audio/wav"
        };
    }
}
