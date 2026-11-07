using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider
{
    private const string RunwayPresetVoiceType = "runway-preset";
    private const string RunwaySoundEffectsModel = "eleven_text_to_sound_v2";

    private async Task<SpeechResponse> RunwayTextToSpeechAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Runway TTS currently only accepts promptText + voice preset; other unified knobs do not map.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var voiceOptions = GetObjectProperty(metadata, "voice");

        // Required: presetId
        _ = DeserializeProperty<string?>(GetProperty(voiceOptions, "type"));
        var metadataPresetId = GetStringProperty(voiceOptions, "presetId");
        var presetId = (request.Voice ?? metadataPresetId)?.Trim();
        if (string.IsNullOrWhiteSpace(presetId))
            throw new ArgumentException(
                "Runway Text-to-Speech requires a voice presetId. Provide SpeechRequest.voice or providerOptions.runway.voice.",
                nameof(request));

        var payload = CreateRawMetadataObject(metadata, "voice", "soundEffects");
        var voice = CreateRawMetadataObject(voiceOptions, "type", "presetId");
        voice["type"] = RunwayPresetVoiceType;
        voice["presetId"] = presetId;
        payload["model"] = JsonValue.Create(request.Model);
        payload["promptText"] = JsonValue.Create(request.Text);
        payload["voice"] = voice;

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync(
            "v1/text_to_speech",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runway TTS failed ({(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body);
        var taskId = ExtractTaskId(node);

        var (bytes, mimeType, outputUrl, lastResult) = await WaitForTaskAndDownloadFirstOutputAsync(taskId, cancellationToken);
        var base64 = Convert.ToBase64String(bytes);

        var resolvedMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType!
            : GuessMimeFromUrl(outputUrl) ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = resolvedMime,
                Format = MapMimeToAudioFormat(resolvedMime)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Warnings = warnings,
            Request = new()
            {
                Body = payload,
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = lastResult
            }
        };
    }

    private async Task<SpeechResponse> RunwaySoundEffectAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required (used as promptText).", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Sound effects are prompt-based; other unified knobs do not map.
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var soundEffects = GetObjectProperty(metadata, "soundEffects");
        var durationValue = GetProperty(soundEffects, "duration");
        var duration = DeserializeProperty<double?>(durationValue);
        var loop = DeserializeProperty<bool?>(GetProperty(soundEffects, "loop"));

        if (duration is { } specifiedDuration)
        {
            if (specifiedDuration < 0.5 || specifiedDuration > 30)
                throw new ArgumentOutOfRangeException("SoundEffects", "soundEffects.duration must be between 0.5 and 30 seconds.");
        }

        var payload = CreateRawMetadataObject(metadata, "soundEffects", "voice");
        AddRawProperties(payload, soundEffects, "duration", "loop");
        // Runway requires a fixed model id for this endpoint.
        payload["model"] = RunwaySoundEffectsModel;
        payload["promptText"] = request.Text;

        if (duration is not null)
            payload["duration"] = duration.Value;

        if (loop is not null)
            payload["loop"] = loop.Value;

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync(
            "v1/sound_effect",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runway sound_effect failed ({(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body);
        var taskId = ExtractTaskId(node);

        var (bytes, mimeType, outputUrl, lastResult) = await WaitForTaskAndDownloadFirstOutputAsync(taskId, cancellationToken);
        var base64 = Convert.ToBase64String(bytes);

        var resolvedMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType!
            : GuessMimeFromUrl(outputUrl) ?? "audio/mpeg";

        return new()
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = resolvedMime,
                Format = MapMimeToAudioFormat(resolvedMime)
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = lastResult
            }
        };
    }

    private async Task<(byte[] Bytes, string? MimeType, string? OutputUrl, JsonNode? lastResult)> WaitForTaskAndDownloadFirstOutputAsync(
        string taskId,
        CancellationToken ct = default)
    {
        string? status;
        JsonNode? json;

        do
        {
            using var resp = await _client.GetAsync($"v1/tasks/{taskId}", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{resp.StatusCode}: {text}");

            json = JsonNode.Parse(text);
            status = json?["status"]?.ToString();

            if (status == "SUCCEEDED")
            {
                var outputUrl = ExtractFirstOutputUrl(json);
                if (string.IsNullOrWhiteSpace(outputUrl))
                    throw new Exception("No outputs returned by Runway API.");

                using var outResp = await _client.GetAsync(outputUrl, ct);
                var bytes = await outResp.Content.ReadAsByteArrayAsync(ct);

                if (!outResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Runway output download failed ({(int)outResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

                var mime = outResp.Content.Headers.ContentType?.MediaType;
                return (bytes, mime, outputUrl, json);
            }

            if (status == "FAILED")
            {
                var reason = json?["failure"]?.ToString() ?? "Unknown failure.";
                var code = json?["failureCode"]?.ToString() ?? "";
                throw new Exception($"Runway task failed: {reason} ({code})");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        } while (status != "SUCCEEDED" && status != "FAILED");

        throw new TimeoutException($"Runway task {taskId} did not complete.");
    }

    private static string? ExtractFirstOutputUrl(JsonNode? taskJson)
    {
        var output = taskJson?["output"];
        if (output is null)
            return null;

        // output: ["https://..."] or output: [{"uri":"https://..."}]
        if (output is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is null)
                    continue;

                if (item is JsonObject obj)
                {
                    var uri = obj["uri"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(uri))
                        return uri;

                    var url = obj["url"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;

                    continue;
                }

                var value = item.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        // output: "https://..."
        if (output is JsonValue)
        {
            var direct = output.GetValue<string>();
            return string.IsNullOrWhiteSpace(direct) ? null : direct;
        }

        return null;
    }

    private static string MapMimeToAudioFormat(string mimeType)
    {
        var mt = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        return mt switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" => "m4a",
            "audio/webm" => "webm",
            _ => "mp3"
        };
    }

    private static JsonElement GetObjectProperty(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        if (property.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        if (property.ValueKind != JsonValueKind.Object)
            throw new JsonException($"The JSON value for '{propertyName}' must be an object.");

        return property;
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return default;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
        => DeserializeProperty<string?>(GetProperty(element, propertyName));

    private static T? DeserializeProperty<T>(JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOpts);

    private static JsonObject CreateRawMetadataObject(JsonElement metadata, params string[] excludedProperties)
    {
        var result = new JsonObject();
        AddRawProperties(result, metadata, excludedProperties);
        return result;
    }

    private static void AddRawProperties(
        JsonObject destination,
        JsonElement source,
        params string[] excludedProperties)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in source.EnumerateObject())
        {
            if (!excludedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                destination[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    private static string? GuessMimeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".mp3")) return "audio/mpeg";
        if (u.Contains(".wav")) return "audio/wav";
        if (u.Contains(".ogg")) return "audio/ogg";
        if (u.Contains(".opus")) return "audio/opus";
        if (u.Contains(".flac")) return "audio/flac";
        if (u.Contains(".aac")) return "audio/aac";
        if (u.Contains(".m4a") || u.Contains(".mp4")) return "audio/mp4";
        if (u.Contains(".webm")) return "audio/webm";
        return null;
    }
}
