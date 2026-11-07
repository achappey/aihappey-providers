using AIHappey.Common.Model;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Gladia;

public partial class GladiaProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = realtimeRequest.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildRealtimePayload(metadata, realtimeRequest.Model);
        var resp = await _client.GetRealtimeResponse<GladiaiRealtimeResponse>(payload,
            relativeUrl: "v2/live",
            ct: cancellationToken);

        return new RealtimeResponse()
        {
            Value = resp.Url,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
    }

    private static JsonElement BuildRealtimePayload(JsonElement? metadata, string? model)
    {
        if (metadata is { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null })
            metadata = null;

        if (metadata is { } suppliedMetadata)
            ValidateMetadata(suppliedMetadata);

        var payload = metadata is { } rawMetadata
            ? JsonSerializer.Deserialize<JsonObject>(rawMetadata.GetRawText(), JsonOpts) ?? new JsonObject()
            : new JsonObject();

        SetDefault(payload, "encoding", JsonValue.Create("wav/pcm"));
        SetDefault(payload, "bit_depth", JsonValue.Create(16));
        SetDefault(payload, "sample_rate", JsonValue.Create(16000));
        SetDefault(payload, "channels", JsonValue.Create(1));
        SetDefault(payload, "endpointing", JsonValue.Create(0.05));
        SetDefault(payload, "maximum_duration_without_endpointing", JsonValue.Create(5));

        if (model is null)
            payload.Remove("model");
        else
            payload["model"] = JsonValue.Create(model);

        RemoveKnownNulls(payload,
            "custom_metadata", "language_config", "pre_processing", "realtime_processing", "post_processing");

        if (payload["language_config"] is JsonObject languageConfig)
        {
            SetDefault(languageConfig, "code_switching", JsonValue.Create(false));
            RemoveKnownNulls(languageConfig, "languages");
        }

        if (payload["pre_processing"] is JsonObject preProcessing)
        {
            SetDefault(preProcessing, "audio_enhancer", JsonValue.Create(false));
            SetDefault(preProcessing, "speech_threshold", JsonValue.Create(0.6));
        }

        if (payload["realtime_processing"] is JsonObject realtimeProcessing)
        {
            SetDefault(realtimeProcessing, "custom_vocabulary", JsonValue.Create(false));
            SetDefault(realtimeProcessing, "custom_spelling", JsonValue.Create(false));
            SetDefault(realtimeProcessing, "translation", JsonValue.Create(false));
            SetDefault(realtimeProcessing, "named_entity_recognition", JsonValue.Create(false));
            SetDefault(realtimeProcessing, "sentiment_analysis", JsonValue.Create(false));
            RemoveKnownNulls(realtimeProcessing,
                "custom_vocabulary_config", "custom_spelling_config", "translation_config");

            if (realtimeProcessing["custom_vocabulary_config"] is JsonObject vocabularyConfig)
            {
                SetDefault(vocabularyConfig, "default_intensity", JsonValue.Create(0.5));
                RemoveKnownNulls(vocabularyConfig, "vocabulary");

                if (vocabularyConfig["vocabulary"] is JsonArray vocabulary)
                {
                    foreach (var vocabularyItem in vocabulary.OfType<JsonObject>())
                        RemoveKnownNulls(vocabularyItem, "value", "intensity", "pronunciations", "language");
                }
            }
        }

        if (payload["post_processing"] is JsonObject postProcessing)
        {
            SetDefault(postProcessing, "summarization", JsonValue.Create(false));
            SetDefault(postProcessing, "chapterization", JsonValue.Create(false));
            RemoveKnownNulls(postProcessing, "summarization_config");

            if (postProcessing["summarization_config"] is JsonObject summarizationConfig)
                SetDefault(summarizationConfig, "type", JsonValue.Create("general"));
        }

        return JsonSerializer.SerializeToElement(payload, JsonOpts);
    }

    private static void ValidateMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Gladia realtime provider options must be a JSON object.");

        ValidateProperty(metadata, "encoding", JsonValueKind.String);
        ValidateInt32Property(metadata, "bit_depth");
        ValidateInt32Property(metadata, "sample_rate");
        ValidateInt32Property(metadata, "channels");
        ValidateProperty(metadata, "custom_metadata", JsonValueKind.Object);
        ValidateProperty(metadata, "model", JsonValueKind.String);
        ValidateNumberProperty(metadata, "endpointing");
        ValidateNumberProperty(metadata, "maximum_duration_without_endpointing");

        if (TryGetObjectProperty(metadata, "language_config", out var languageConfig))
        {
            ValidateStringArrayProperty(languageConfig, "languages");
            ValidateProperty(languageConfig, "code_switching", JsonValueKind.True, JsonValueKind.False);
        }

        if (TryGetObjectProperty(metadata, "pre_processing", out var preProcessing))
        {
            ValidateProperty(preProcessing, "audio_enhancer", JsonValueKind.True, JsonValueKind.False);
            ValidateNumberProperty(preProcessing, "speech_threshold");
        }

        if (TryGetObjectProperty(metadata, "realtime_processing", out var realtimeProcessing))
        {
            foreach (var name in new[]
                     {
                         "custom_vocabulary", "custom_spelling", "translation",
                         "named_entity_recognition", "sentiment_analysis"
                     })
            {
                ValidateProperty(realtimeProcessing, name, JsonValueKind.True, JsonValueKind.False);
            }

            if (TryGetObjectProperty(realtimeProcessing, "custom_vocabulary_config", out var vocabularyConfig))
            {
                ValidateNumberProperty(vocabularyConfig, "default_intensity");
                if (TryGetProperty(vocabularyConfig, "vocabulary", out var vocabulary)
                    && vocabulary.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
                {
                    throw new JsonException("Gladia custom vocabulary must be an array or null.");
                }

                if (vocabulary.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in vocabulary.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            continue;
                        if (item.ValueKind != JsonValueKind.Object)
                            throw new JsonException("Gladia custom vocabulary entries must be strings or objects.");

                        ValidateProperty(item, "value", JsonValueKind.String);
                        ValidateNumberProperty(item, "intensity");
                        ValidateStringArrayProperty(item, "pronunciations");
                        ValidateProperty(item, "language", JsonValueKind.String);
                    }
                }
            }
        }

        if (TryGetObjectProperty(metadata, "post_processing", out var postProcessing))
        {
            ValidateProperty(postProcessing, "summarization", JsonValueKind.True, JsonValueKind.False);
            ValidateProperty(postProcessing, "chapterization", JsonValueKind.True, JsonValueKind.False);
            if (TryGetObjectProperty(postProcessing, "summarization_config", out var summarizationConfig))
                ValidateProperty(summarizationConfig, "type", JsonValueKind.String);
        }
    }

    private static bool TryGetObjectProperty(JsonElement target, string propertyName, out JsonElement value)
    {
        if (!TryGetProperty(target, propertyName, out value))
            return false;
        if (value.ValueKind == JsonValueKind.Null)
            return false;
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Gladia '{propertyName}' must be an object or null.");
        return true;
    }

    private static bool TryGetProperty(JsonElement target, string propertyName, out JsonElement value)
        => target.TryGetProperty(propertyName, out value);

    private static void ValidateProperty(JsonElement target, string propertyName, params JsonValueKind[] allowedKinds)
    {
        if (!TryGetProperty(target, propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (!allowedKinds.Contains(value.ValueKind))
            throw new JsonException($"Gladia '{propertyName}' has an invalid JSON value type.");
    }

    private static void ValidateInt32Property(JsonElement target, string propertyName)
    {
        if (!TryGetProperty(target, propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))
            throw new JsonException($"Gladia '{propertyName}' must be a 32-bit integer or null.");
    }

    private static void ValidateNumberProperty(JsonElement target, string propertyName)
        => ValidateProperty(target, propertyName, JsonValueKind.Number);

    private static void ValidateStringArrayProperty(JsonElement target, string propertyName)
    {
        if (!TryGetProperty(target, propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        if (value.ValueKind != JsonValueKind.Array
            || value.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            throw new JsonException($"Gladia '{propertyName}' must be an array of strings or null.");
        }
    }

    private static void SetDefault(JsonObject target, string propertyName, JsonNode? defaultValue)
    {
        if (!target.TryGetPropertyValue(propertyName, out var value))
            target[propertyName] = defaultValue;
        else if (value is null)
            target.Remove(propertyName);
    }

    private static void RemoveKnownNulls(JsonObject target, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (target.TryGetPropertyValue(propertyName, out var value) && value is null)
                target.Remove(propertyName);
        }
    }
}

public class GladiaiRealtimeResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("url")]
    public string Url { get; set; } = null!;

}
