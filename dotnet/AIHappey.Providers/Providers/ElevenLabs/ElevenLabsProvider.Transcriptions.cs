using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("ElevenLabs provider metadata must be an object.");
        if (TryGetMetadataProperty(metadata, "model_id", out _)
            || TryGetMetadataProperty(metadata, "file", out _))
            throw new JsonException("ElevenLabs provider metadata cannot override the request model_id or file fields.");

        var modelId = !string.IsNullOrWhiteSpace(request.Model)
            ? request.Model
            : "scribe_v2";

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(modelId), "model_id");

        var languageCode = ReadMetadataValue<string>(metadata, "language_code")
            ?? ReadMetadataValue<string>(metadata, "language");
        if (!string.IsNullOrWhiteSpace(languageCode))
            form.Add(new StringContent(languageCode), "language_code");
        AddBooleanMetadata(form, metadata, "tag_audio_events");
        AddIntegerMetadata(form, metadata, "num_speakers");
        var timestampsGranularity = ReadMetadataValue<string>(metadata, "timestamps_granularity");
        if (string.IsNullOrWhiteSpace(timestampsGranularity)
            && ReadMetadataValue<string[]>(metadata, "timestamp_granularities")?
                .Contains("word", StringComparer.OrdinalIgnoreCase) == true)
        {
            timestampsGranularity = "word";
        }

        if (!string.IsNullOrWhiteSpace(timestampsGranularity))
            form.Add(new StringContent(timestampsGranularity), "timestamps_granularity");
        AddBooleanMetadata(form, metadata, "diarize");
        AddFloatMetadata(form, metadata, "diarization_threshold");
        AddStringMetadata(form, metadata, "file_format");
        AddFloatMetadata(form, metadata, "temperature");
        AddIntegerMetadata(form, metadata, "seed");
        AddBooleanMetadata(form, metadata, "use_multi_channel");

        var query = new List<string>();
        if (ReadMetadataValue<bool?>(metadata, "enable_logging") is { } enableLogging)
            query.Add($"enable_logging={enableLogging.ToString().ToLowerInvariant()}");

        AddUnknownMetadataFields(form, metadata);

        var url = "v1/speech-to-text" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        using var resp = await _client.PostAsync(url, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertResponse(json, modelId);
    }

    private static T? ReadMetadataValue<T>(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object
            || !TryGetMetadataProperty(metadata, propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static void AddBooleanMetadata(MultipartFormDataContent form, JsonElement metadata, string propertyName)
    {
        if (ReadMetadataValue<bool?>(metadata, propertyName) is { } value)
            form.Add(new StringContent(value.ToString().ToLowerInvariant()), propertyName);
    }

    private static void AddStringMetadata(MultipartFormDataContent form, JsonElement metadata, string propertyName)
    {
        var value = ReadMetadataValue<string>(metadata, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), propertyName);
    }

    private static void AddIntegerMetadata(MultipartFormDataContent form, JsonElement metadata, string propertyName)
    {
        if (ReadMetadataValue<int?>(metadata, propertyName) is { } value)
            form.Add(new StringContent(value.ToString(System.Globalization.CultureInfo.InvariantCulture)), propertyName);
    }

    private static void AddFloatMetadata(MultipartFormDataContent form, JsonElement metadata, string propertyName)
    {
        if (ReadMetadataValue<float?>(metadata, propertyName) is { } value)
            form.Add(new StringContent(value.ToString(System.Globalization.CultureInfo.InvariantCulture)), propertyName);
    }

    private static void AddUnknownMetadataFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (IsConsumedMetadataField(property.Name))
                continue;

            form.Add(new StringContent(FormatMetadataValue(property.Value)), property.Name);
        }
    }

    private static bool IsConsumedMetadataField(string propertyName)
        => propertyName.Equals("enable_logging", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("language_code", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("language", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("tag_audio_events", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("num_speakers", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("timestamps_granularity", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("timestamp_granularities", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("diarize", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("diarization_threshold", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("file_format", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("temperature", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("seed", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("use_multi_channel", StringComparison.OrdinalIgnoreCase);

    private static string FormatMetadataValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static bool TryGetMetadataProperty(JsonElement metadata, string propertyName, out JsonElement value)
    {
        if (metadata.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static TranscriptionResponse ConvertResponse(string json, string modelId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
        var language = root.TryGetProperty("language_code", out var lang) ? lang.GetString() : null;

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsEl.EnumerateArray())
            {
                var wText = w.TryGetProperty("text", out var wt) ? (wt.GetString() ?? string.Empty) : string.Empty;
                var start = w.TryGetProperty("start", out var ws) ? (float)ws.GetDouble() : 0f;
                var end = w.TryGetProperty("end", out var we) ? (float)we.GetDouble() : start;
                if (!string.IsNullOrWhiteSpace(wText))
                    segments.Add(new TranscriptionSegment { Text = wText, StartSecond = start, EndSecond = end });
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Segments = segments,
            Warnings = [],
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = modelId, Body = json }
        };
    }
}
