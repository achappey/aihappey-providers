using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HumeAI;

public partial class HumeAIProvider
{
    private const string BaseSpeechModel = "octave";

    private static readonly JsonSerializerOptions HumeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var (payload, outputFormat, warnings) = BuildSpeechPayload(request, streaming: false);
        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v0/tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, HumeJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var requestId = HumeReadString(root, "request_id");

        if (!root.TryGetProperty("generations", out var generationsEl)
            || generationsEl.ValueKind != JsonValueKind.Array
            || generationsEl.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"{ProviderName} TTS response did not contain any generations.");
        }

        var firstGeneration = generationsEl.EnumerateArray().First();
        var audio = HumeReadString(firstGeneration, "audio");
        if (string.IsNullOrWhiteSpace(audio))
            throw new InvalidOperationException($"{ProviderName} TTS response did not contain generated audio.");

        var generationId = HumeReadString(firstGeneration, "generation_id");
        var generationIds = generationsEl.EnumerateArray()
            .Select(g => HumeReadString(g, "generation_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = audio,
                MimeType = ResolveSpeechMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private (Dictionary<string, object?> Payload, string OutputFormat, List<object> Warnings) BuildSpeechPayload(
        SpeechRequest request,
        bool streaming)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("HumeAI provider options must be a JSON object.");

        var (baseModelId, modelVoiceProvider, modelVoiceId) = ParseSpeechModel(request.Model);

        if (!string.Equals(baseModelId, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported {ProviderName} speech model '{baseModelId}'. Use '{BaseSpeechModel}'.", nameof(request));

        var outputFormat = NormalizeOutputFormat(request.OutputFormat ?? ReadMetadataValue<string>(metadata, "output_format")) ?? "mp3";
        if (outputFormat is not "mp3" and not "wav" and not "pcm")
            throw new NotSupportedException($"HumeAI TTS response format '{outputFormat}' is not supported.");

        var metadataVoiceProvider = ReadMetadataValue<string>(metadata, "voice_provider");
        var metadataVoiceId = ReadMetadataValue<string>(metadata, "voice_id");
        var voiceProvider = NormalizeVoiceProvider(modelVoiceProvider ?? metadataVoiceProvider);
        var voiceId = FirstNonEmpty(modelVoiceId, request.Voice, metadataVoiceId);
        var voiceName = string.IsNullOrWhiteSpace(voiceId) ? FirstNonEmpty(ReadMetadataValue<string>(metadata, "voice_name")) : null;
        var voice = BuildVoiceRef(voiceId, voiceName, voiceProvider);
        var version = NormalizeOctaveVersion(ReadMetadataValue<string>(metadata, "version"));

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

            if (!string.IsNullOrWhiteSpace(metadataVoiceId)
                && !string.Equals(metadataVoiceId.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
                warnings.Add(new { type = "ignored", feature = "providerOptions.humeai.voice_id", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoiceProvider)
            && !string.IsNullOrWhiteSpace(metadataVoiceProvider)
            && !string.Equals(NormalizeVoiceProvider(metadataVoiceProvider), modelVoiceProvider, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "providerOptions.humeai.voice_provider", reason = "voice provider is derived from model id" });

        if (string.Equals(version, "2", StringComparison.Ordinal) && voice is null)
            throw new ArgumentException("HumeAI Octave version 2 requires a voice. Supply request.voice, providerOptions.humeai.voice_id, providerOptions.humeai.voice_name, or a voice shortcut model.", nameof(request));
        var instantMode = ReadMetadataValue<bool?>(metadata, "instant_mode");
        var numGenerations = ReadMetadataValue<int?>(metadata, "num_generations");
        if (streaming && instantMode is not false && voice is null)
            throw new ArgumentException("HumeAI instant-mode streaming requires a voice. Supply request.voice or use a voice shortcut model.", nameof(request));
        if (streaming && instantMode is not false && numGenerations is > 1)
            throw new ArgumentException("HumeAI instant-mode streaming supports only one generation.", nameof(request));

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language", reason = "HumeAI TTS language is voice-dependent" });
        if (!streaming && numGenerations is > 1)
            warnings.Add(new { type = "partial", feature = "num_generations", reason = "Only the first generation is returned as the normalized audio response; all generation ids are included in provider metadata." });
        if (!streaming && instantMode is true)
            warnings.Add(new { type = "ignored", feature = "providerOptions.humeai.instant_mode", reason = "instant_mode is only supported by HumeAI streaming TTS endpoints" });

        var utterance = new Dictionary<string, object?>
        {
            ["text"] = request.Text
        };
        AddIfNotNull(utterance, "description", FirstNonEmpty(request.Instructions, ReadMetadataValue<string>(metadata, "description")));
        AddIfNotNull(utterance, "speed", request.Speed is null ? null : (double?)request.Speed.Value);
        AddIfNotNull(utterance, "trailing_silence", ReadMetadataValue<double?>(metadata, "trailing_silence"));
        AddIfNotNull(utterance, "voice", voice);

        var payload = CopyUnmappedMetadata(metadata);
        payload["utterances"] = new[] { utterance };
        payload["format"] = new Dictionary<string, object?> { ["type"] = outputFormat };
        AddIfNotNull(payload, "version", version);
        AddIfNotNull(payload, "temperature", ReadMetadataValue<double?>(metadata, "temperature"));
        AddIfNotNull(payload, "num_generations", numGenerations);
        AddIfNotNull(payload, "split_utterances", ReadMetadataValue<bool?>(metadata, "split_utterances"));
        AddIfNotNull(payload, "strip_headers", ReadMetadataValue<bool?>(metadata, "strip_headers"));
        AddIfNotNull(payload, "include_timestamp_types", NormalizeTimestampTypes(ReadMetadataValue<IReadOnlyList<string>>(metadata, "include_timestamp_types")));
        AddIfNotNull(payload, "context", BuildContext(metadata));

        if (streaming)
            payload["instant_mode"] = instantMode ?? true;
        else if (instantMode is false)
            payload["instant_mode"] = false;

        return (payload, outputFormat, warnings);
    }

    private static Dictionary<string, object?> CopyUnmappedMetadata(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
        {
            if (IsMappedMetadataProperty(property.Name))
                continue;

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static bool IsMappedMetadataProperty(string name)
        => name.Equals("voice_id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("voice_name", StringComparison.OrdinalIgnoreCase)
            || name.Equals("voice_provider", StringComparison.OrdinalIgnoreCase)
            || name.Equals("description", StringComparison.OrdinalIgnoreCase)
            || name.Equals("version", StringComparison.OrdinalIgnoreCase)
            || name.Equals("output_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("temperature", StringComparison.OrdinalIgnoreCase)
            || name.Equals("num_generations", StringComparison.OrdinalIgnoreCase)
            || name.Equals("split_utterances", StringComparison.OrdinalIgnoreCase)
            || name.Equals("strip_headers", StringComparison.OrdinalIgnoreCase)
            || name.Equals("include_timestamp_types", StringComparison.OrdinalIgnoreCase)
            || name.Equals("context_generation_id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("context_utterances", StringComparison.OrdinalIgnoreCase)
            || name.Equals("trailing_silence", StringComparison.OrdinalIgnoreCase)
            || name.Equals("instant_mode", StringComparison.OrdinalIgnoreCase);

    private static T? ReadMetadataValue<T>(JsonElement metadata, string propertyName)
    {
        if (!TryGetMetadataProperty(metadata, propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static bool TryGetMetadataProperty(JsonElement metadata, string propertyName, out JsonElement value)
    {
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static (string BaseModelId, string? VoiceProvider, string? VoiceId) ParseSpeechModel(string model)
    {
        var raw = model.Trim();
        var providerPrefix = ProviderId + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        if (string.IsNullOrWhiteSpace(raw))
            return (BaseSpeechModel, null, null);

        var parts = raw.Split('/', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
            return (parts[0], null, null);

        if (parts.Length == 2)
            return (parts[0], null, parts[1]);

        return (parts[0], NormalizeVoiceProvider(parts[1]), parts[2]);
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" or "mpga" => "mp3",
            "wave" => "wav",
            var fmt when fmt is "mp3" or "wav" or "pcm" => fmt,
            _ => outputFormat.Trim().ToLowerInvariant()
        };
    }

    private static string? NormalizeOctaveVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var normalized = version.Trim();
        return normalized switch
        {
            "1" or "2" => normalized,
            _ => throw new ArgumentException("HumeAI Octave version must be '1' or '2'.", nameof(version))
        };
    }

    private static string? NormalizeVoiceProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        return provider.Trim().Replace('-', '_').ToUpperInvariant() switch
        {
            "HUME" or "HUMEAI" or "HUME_AI" => "HUME_AI",
            "CUSTOM" or "CUSTOMVOICE" or "CUSTOM_VOICE" => "CUSTOM_VOICE",
            var p => p
        };
    }

    private static IReadOnlyList<string>? NormalizeTimestampTypes(IReadOnlyList<string>? timestampTypes)
    {
        if (timestampTypes is null)
            return null;

        return [.. timestampTypes
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t is "word" or "phoneme")
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, object?>? BuildVoiceRef(string? voiceId, string? voiceName, string? voiceProvider)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var voice = new Dictionary<string, object?> { ["id"] = voiceId.Trim() };
            if (!string.IsNullOrWhiteSpace(voiceProvider))
                voice["provider"] = voiceProvider;
            return voice;
        }

        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            var voice = new Dictionary<string, object?> { ["name"] = voiceName.Trim() };
            if (!string.IsNullOrWhiteSpace(voiceProvider))
                voice["provider"] = voiceProvider;
            return voice;
        }

        return null;
    }

    private static void AddIfNotNull(Dictionary<string, object?> dictionary, string key, object? value)
    {
        if (value is not null)
            dictionary[key] = value;
    }

    private static Dictionary<string, object?>? BuildContext(JsonElement metadata)
    {
        var generationId = ReadMetadataValue<string>(metadata, "context_generation_id");
        if (!string.IsNullOrWhiteSpace(generationId))
            return new Dictionary<string, object?> { ["generation_id"] = generationId.Trim() };

        if (!TryGetMetadataProperty(metadata, "context_utterances", out var contextUtterances)
            || contextUtterances.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (contextUtterances.ValueKind != JsonValueKind.Array)
            throw new JsonException("HumeAI context_utterances must be a JSON array.");

        var utterances = new List<Dictionary<string, object?>>();
        foreach (var contextUtterance in contextUtterances.EnumerateArray())
        {
            if (contextUtterance.ValueKind != JsonValueKind.Object)
                throw new JsonException("HumeAI context_utterances entries must be JSON objects.");

            var text = ReadMetadataValue<string>(contextUtterance, "text");
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var utterance = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in contextUtterance.EnumerateObject())
            {
                if (property.Name.Equals("text", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("description", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("speed", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("trailing_silence", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("voice_id", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("voice_name", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("voice_provider", StringComparison.OrdinalIgnoreCase))
                    continue;

                utterance[property.Name] = property.Value.Clone();
            }

            utterance["text"] = text;
            AddIfNotNull(utterance, "description", ReadMetadataValue<string>(contextUtterance, "description"));
            AddIfNotNull(utterance, "speed", ReadMetadataValue<double?>(contextUtterance, "speed"));
            AddIfNotNull(utterance, "trailing_silence", ReadMetadataValue<double?>(contextUtterance, "trailing_silence"));
            AddIfNotNull(utterance, "voice", BuildVoiceRef(
                ReadMetadataValue<string>(contextUtterance, "voice_id"),
                ReadMetadataValue<string>(contextUtterance, "voice_name"),
                NormalizeVoiceProvider(ReadMetadataValue<string>(contextUtterance, "voice_provider"))));
            utterances.Add(utterance);
        }

        return utterances.Count == 0
            ? null
            : new Dictionary<string, object?> { ["utterances"] = utterances };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ResolveSpeechMimeType(string outputFormat)
        => outputFormat.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
}
