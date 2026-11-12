using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Gnani;

public partial class GnaniProvider
{
    private static readonly JsonSerializerOptions GnaniJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> SpeechRequestCore(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        ValidateMetadata(metadata);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var plan = CreateSpeechPlan(request, metadata, warnings);
        using var httpRequest = CreateAuthenticatedRequest(HttpMethod.Post, "api/v1/tts/inference");
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(plan.Payload, GnaniJson),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        var timestamp = DateTime.UtcNow;
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Gnani TTS failed ({(int)response.StatusCode}): {error}");
        }
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException($"Gnani TTS returned JSON instead of audio: {await response.Content.ReadAsStringAsync(cancellationToken)}");

        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0)
            throw new InvalidOperationException("Gnani TTS returned empty audio.");

        var mimeType = ResolveAudioMimeType(plan.Container, plan.Encoding, response.Content.Headers.ContentType?.MediaType);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = ResolveAudioFormat(plan.Container, mimeType)
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = plan.Payload },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = response.GetHeaders(),
                ModelId = plan.Model.ToModelId(GetIdentifier()),
                Body = JsonSerializer.SerializeToElement(new
                {
                    contentType = response.Content.Headers.ContentType?.ToString(),
                    audioBytes = audio.Length
                }, GnaniJson)
            }
        };
    }

    private static GnaniSpeechPlan CreateSpeechPlan(
        SpeechRequest request,
        JsonElement metadata,
        List<object> warnings,
        int? sampleRate = null)
    {
        var model = NormalizeModel(request.Model);
        if (model is not ("timbre-v2.5" or "timbre-v3.0" or "vachana-vc-v1"))
            throw new NotSupportedException($"Gnani speech model '{model}' is not supported.");

        foreach (var name in new[] { "voice", "language" })
            if (TryGetPropertyIgnoreCase(metadata, name, out var value)
                && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new JsonException($"Gnani option '{name}' must be a string.");
        if (TryGetPropertyIgnoreCase(metadata, "speed", out var speedValue)
            && speedValue.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
            throw new JsonException("Gnani option 'speed' must be a number.");

        var payload = CopyObject(metadata);
        var voice = request.Voice ?? ReadString(metadata, "voice");
        var language = request.Language ?? ReadString(metadata, "language");
        var speed = request.Speed is { } requestedSpeed
            ? (object)requestedSpeed
            : ReadNumber(metadata, "speed");
        foreach (var key in payload.Keys
                     .Where(key => new[] { "text", "voice", "model", "language", "speed", "audio_config" }
                         .Contains(key, StringComparer.OrdinalIgnoreCase))
                     .ToArray())
            payload.Remove(key);
        payload["text"] = request.Text;
        payload["voice"] = voice;
        payload["model"] = model;
        payload["language"] = language;
        payload["speed"] = speed;

        var config = ReadObject(metadata, "audio_config");
        var container = NormalizeContainer(request.OutputFormat);
        if (!string.IsNullOrWhiteSpace(request.OutputFormat) && container is null)
            warnings.Add(new { type = "unsupported", feature = "outputFormat", details = request.OutputFormat });
        if (container is not null)
            config["container"] = container;
        if (request.OutputFormat?.Trim().ToLowerInvariant() is "pcm" or "audio/pcm")
            config["encoding"] = "linear_pcm";
        if (sampleRate is not null)
            config["sample_rate"] = sampleRate.Value;
        if (!config.ContainsKey("container"))
            config["container"] = "wav";
        if (config.Count > 0)
            payload["audio_config"] = config;

        return new GnaniSpeechPlan(
            model, payload,
            config.TryGetValue("container", out var containerValue) ? containerValue?.ToString() : container,
            config.TryGetValue("encoding", out var encoding) ? encoding?.ToString() : null);
    }

    private async IAsyncEnumerable<StreamingSpeechPart> StreamSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var rate = request.OutputFormat?.Rate;
        if (rate is not null && (rate <= 0 || rate > int.MaxValue || rate != Math.Truncate(rate.Value)))
            throw new ArgumentOutOfRangeException(nameof(request), "Gnani output sample rate must be a positive integer.");
        if (request.Text is not { Length: > 0 }
            || request.Text.Any(part => part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null))
            throw new ArgumentException("Gnani speech streaming requires non-empty text parts.", nameof(request));

        var text = string.Concat(request.Text.Select(part => part.Text));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Gnani speech text cannot be empty.", nameof(request));
        var metadata = MergeSpeechMetadata(request);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var speechRequest = new SpeechRequest
        {
            Model = request.Model,
            Text = text,
            Voice = ResolveStreamingVoice(request),
            OutputFormat = request.OutputFormat?.Type,
            Language = request.Language,
            Speed = request.Speed,
            ProviderOptions = metadata is { ValueKind: JsonValueKind.Object }
                ? new() { [GetIdentifier()] = metadata.Clone() }
                : null
        };
        var plan = CreateSpeechPlan(speechRequest, metadata, warnings, rate is null ? null : (int)rate.Value);
        var container = plan.Container ?? "wav";
        var mimeType = ResolveAudioMimeType(container, plan.Encoding, null);
        var events = new List<JsonElement>();
        var receivedAudio = false;
        var completed = false;

        yield return new SpeechStreamStartPart { Warnings = warnings };
        using var httpRequest = CreateAuthenticatedRequest(HttpMethod.Post, "api/v1/tts/sse");
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(plan.Payload, GnaniJson),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Gnani streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = plan.Model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? eventName = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            var reachedEnd = line is null;
            line ??= string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    var raw = string.Join("\n", dataLines);
                    using var document = ParseSseJson(raw);
                    var root = document.RootElement.Clone();
                    events.Add(root);
                    if (request.IncludeRawChunks == true)
                        yield return new SpeechRawPart { RawValue = root };

                    var type = string.Equals(ReadString(root, "type"), "error", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(ReadString(root, "status"), "error", StringComparison.OrdinalIgnoreCase)
                               || (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
                        ? "error"
                        : eventName ?? ReadString(root, "event") ?? ReadString(root, "type");
                    switch (type?.Trim().ToLowerInvariant())
                    {
                        case "start":
                            yield return new SpeechResponseMetadataPart
                            {
                                Timestamp = DateTime.UtcNow,
                                ModelId = plan.Model.ToModelId(GetIdentifier()),
                                Body = root
                            };
                            break;
                        case "chunk":
                        case "complete":
                            var encodedAudio = ReadString(root, "audio");
                            if (!string.IsNullOrWhiteSpace(encodedAudio))
                            {
                                byte[] bytes;
                                try
                                {
                                    bytes = Convert.FromBase64String(encodedAudio);
                                }
                                catch (FormatException exception)
                                {
                                    throw new JsonException("Gnani returned invalid base64 audio in a streaming chunk.", exception);
                                }

                                if (bytes.Length > 0)
                                {
                                    receivedAudio = true;
                                    yield return new SpeechAudioPart
                                    {
                                        Audio = Convert.ToBase64String(bytes),
                                        AudioFormat = new AudioFormat { Type = mimeType, Rate = request.OutputFormat?.Rate },
                                        ProviderMetadata = new() { [GetIdentifier()] = root }
                                    };
                                }
                            }
                            if (string.Equals(type, "complete", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!receivedAudio)
                                    throw new InvalidOperationException("Gnani streaming TTS completed without audio.");
                                completed = true;
                                yield return new SpeechFinishPart
                                {
                                    ProviderMetadata = new()
                                    {
                                        [GetIdentifier()] = JsonSerializer.SerializeToElement(new { events })
                                    }
                                };
                            }
                            break;
                        case "error":
                            throw new InvalidOperationException($"Gnani streaming TTS failed: {ReadString(root, "message") ?? root.GetRawText()}");
                        case null:
                        case "":
                            throw new JsonException("Gnani returned an SSE event without a type.");
                        default:
                            throw new InvalidOperationException($"Gnani returned unsupported SSE event '{type}'.");
                    }

                    dataLines.Clear();
                    eventName = null;
                    if (completed)
                        yield break;
                }
                if (reachedEnd)
                    break;
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var fieldValue = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
            if (field == "event")
                eventName = fieldValue;
            else if (field == "data")
                dataLines.Add(fieldValue);
        }

        if (!completed)
            throw new InvalidOperationException("Gnani streaming TTS ended before its complete event.");
    }

    private static JsonDocument ParseSseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Gnani returned invalid streaming TTS JSON: {json}", exception);
        }
    }

    private static JsonElement GetSpeechMetadata(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return default;
        var key = providerOptions.Keys.FirstOrDefault(name => string.Equals(name, "gnani", StringComparison.OrdinalIgnoreCase));
        if (key is null)
            return default;
        if (providerOptions[key].ValueKind != JsonValueKind.Object)
            throw new JsonException("Gnani provider options must be a JSON object.");
        return providerOptions[key];
    }

    private static JsonElement MergeSpeechMetadata(StreamingSpeechRequest request)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        AddOptions(GetSpeechMetadata(request.ProviderOptions));
        foreach (var part in request.Text)
            AddOptions(GetSpeechMetadata(part.ProviderOptions));
        return values.Count == 0 ? default : JsonSerializer.SerializeToElement(values, GnaniJson);

        void AddOptions(JsonElement options)
        {
            if (options.ValueKind != JsonValueKind.Object)
                return;
            foreach (var property in options.EnumerateObject())
            {
                if (values.TryGetValue(property.Name, out var existing)
                    && !JsonElement.DeepEquals(existing, property.Value))
                    throw new NotSupportedException($"Gnani speech streaming requires the same '{property.Name}' provider option for every text part.");
                values[property.Name] = property.Value.Clone();
            }
        }
    }

    private static string? ResolveStreamingVoice(StreamingSpeechRequest request)
    {
        var voices = request.Text
            .Select(part => part.Voice)
            .Where(voice => !string.IsNullOrWhiteSpace(voice))
            .Select(voice => voice!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (voices.Length > 1)
            throw new NotSupportedException("Gnani speech streaming requires one voice across all text parts.");
        if (voices.Length == 1
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(voices[0], request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Gnani speech streaming requires one voice across all text parts.");
        return request.Voice ?? voices.FirstOrDefault();
    }

    private static void ValidateMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Gnani provider options must be a JSON object.");
    }

    private static Dictionary<string, object?> CopyObject(JsonElement metadata)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        return result;
    }

    private static Dictionary<string, object?> ReadObject(JsonElement metadata, string name)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (TryGetPropertyIgnoreCase(metadata, name, out var value) && value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        else if (TryGetPropertyIgnoreCase(metadata, name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException($"Gnani option '{name}' must be an object.");
        return result;
    }

    private static string? ReadString(JsonElement metadata, string name)
        => TryGetPropertyIgnoreCase(metadata, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static object? ReadNumber(JsonElement metadata, string name)
        => TryGetPropertyIgnoreCase(metadata, name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.Clone()
            : null;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string NormalizeModel(string model)
    {
        var normalized = model.Trim();
        const string prefix = "gnani/";
        return (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized).ToLowerInvariant();
    }

    private static string? NormalizeContainer(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;
        return format.Trim().ToLowerInvariant() switch
        {
            "mpeg" or "audio/mpeg" or "audio/mp3" => "mp3",
            "wave" or "audio/wav" or "audio/x-wav" => "wav",
            "pcm" or "audio/pcm" => "raw",
            "mp3" or "wav" or "raw" or "mulaw" or "alaw" => format.Trim().ToLowerInvariant(),
            _ => null
        };
    }

    private static string ResolveAudioMimeType(string? container, string? encoding, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return contentType;
        return container?.ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "raw" when encoding == "oggopus" => "audio/ogg",
            "raw" when encoding is "pcm_mulaw" or "pcm_alaw" => "audio/basic",
            "raw" => "audio/pcm",
            "mulaw" or "alaw" => "audio/basic",
            "mp3" => "audio/mpeg",
            _ => "audio/wav"
        };
    }

    private static string ResolveAudioFormat(string? container, string mimeType)
        => NormalizeContainer(container)
           ?? (mimeType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? "wav"
               : mimeType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? "ogg"
               : mimeType.Contains("flac", StringComparison.OrdinalIgnoreCase) ? "flac"
               : mimeType.Contains("pcm", StringComparison.OrdinalIgnoreCase) ? "pcm"
               : mimeType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? "mp3"
               : "wav");

    private sealed record GnaniSpeechPlan(
        string Model,
        Dictionary<string, object?> Payload,
        string? Container,
        string? Encoding);
}
