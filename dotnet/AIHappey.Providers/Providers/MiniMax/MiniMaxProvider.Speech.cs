using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);      

        ApplyAuthHeader();

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        // ---- voice_setting ----
        var payload = BuildSpeechPayload(request, metadata, stream: false);
        var format = ((Dictionary<string, object?>)payload["audio_setting"]!)["format"]!.ToString()!;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/t2a_v2")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax t2a_v2 failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);

        // ---- MiniMax error surface (base_resp) ----
        if (doc.RootElement.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.ValueKind == JsonValueKind.Object &&
            baseResp.TryGetProperty("status_code", out var statusCodeEl) &&
            statusCodeEl.ValueKind == JsonValueKind.Number &&
            statusCodeEl.GetInt32() != 0)
        {
            var traceId = doc.RootElement.TryGetProperty("trace_id", out var traceEl) && traceEl.ValueKind == JsonValueKind.String
                ? traceEl.GetString()
                : null;

            var msg = baseResp.TryGetProperty("status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : "MiniMax request failed";

            throw new InvalidOperationException($"MiniMax t2a_v2 failed (status_code={statusCodeEl.GetInt32()}, status_msg={msg}, trace_id={traceId}).");
        }

        // ---- Extract audio hex ----
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("audio", out var audioEl) ||
            audioEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"MiniMax t2a_v2 response missing data.audio: {raw}");
        }

        var hex = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("MiniMax t2a_v2 returned empty audio.");

        var bytes = DecodeHexStringToBytes(hex);
        var mime = GuessAudioMimeType(format);
        var audioDataUrl = Convert.ToBase64String(bytes);

        var providerKey = GetIdentifier();
        var currentModel = await this.GetModel(request.Model, cancellationToken);

        var providerMetadata = new Dictionary<string, JsonElement>();

        decimal? usageCharacters = null;
        decimal? cost = null;

        if (doc.RootElement.TryGetProperty("extra_info", out var extraInfoEl)
            && extraInfoEl.ValueKind == JsonValueKind.Object)
        {
            providerMetadata[providerKey] = JsonSerializer.SerializeToElement(new
            {
                extra_info = extraInfoEl.Clone()
            }, JsonSerializerOptions.Web);

            if (extraInfoEl.TryGetProperty("usage_characters", out var usageCharactersEl)
                && usageCharactersEl.ValueKind == JsonValueKind.Number
                && usageCharactersEl.TryGetDecimal(out var parsedUsageCharacters))
            {
                usageCharacters = parsedUsageCharacters;
            }
        }

        if (usageCharacters is not null && currentModel?.Pricing?.Input is not null)
        {
            cost = usageCharacters.Value * currentModel.Pricing.Input;
        }

        if (cost is not null)
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioDataUrl,
                MimeType = mime,
                Format = format
            },
            ProviderMetadata = providerMetadata,
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static string? MapLanguageToLanguageBoost(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var l = language.Trim();

        if (l.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "auto";

        var primary = l.Split('-', '_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return primary switch
        {
            "zh" => "Chinese",
            "yue" => "Chinese,Yue",
            "en" => "English",
            "ar" => "Arabic",
            "ru" => "Russian",
            "es" => "Spanish",
            "fr" => "French",
            "pt" => "Portuguese",
            "de" => "German",
            "tr" => "Turkish",
            "nl" => "Dutch",
            "uk" => "Ukrainian",
            "vi" => "Vietnamese",
            "id" => "Indonesian",
            "ja" => "Japanese",
            "it" => "Italian",
            "ko" => "Korean",
            "th" => "Thai",
            "pl" => "Polish",
            "ro" => "Romanian",
            "el" => "Greek",
            "cs" => "Czech",
            "fi" => "Finnish",
            "hi" => "Hindi",
            "bg" => "Bulgarian",
            "da" => "Danish",
            "he" => "Hebrew",
            "ms" => "Malay",
            "fa" => "Persian",
            "sk" => "Slovak",
            "sv" => "Swedish",
            "hr" => "Croatian",
            "fil" => "Filipino",
            "hu" => "Hungarian",
            "no" or "nb" => "Norwegian",
            "sl" => "Slovenian",
            "ca" => "Catalan",
            "nn" => "Nynorsk",
            "ta" => "Tamil",
            "af" => "Afrikaans",
            _ => null
        };
    }

    private static Dictionary<string, object?> BuildSpeechPayload(
        SpeechRequest request,
        JsonElement metadata,
        bool stream)
    {
        ValidateMetadata(metadata);

        var voiceSetting = GetObject(metadata, "voice_setting");
        var audioSetting = GetObject(metadata, "audio_setting");
        var voiceId = request.Voice ?? GetValue<string>(voiceSetting, "voice_id") ?? "English_Insightful_Speaker";
        var speed = Math.Clamp(request.Speed ?? GetValue<float?>(voiceSetting, "speed") ?? 1.0f, 0.5f, 2.0f);
        var volume = Math.Clamp(GetValue<double?>(voiceSetting, "vol") ?? 1.0, double.Epsilon, 10);
        var pitch = Math.Clamp(GetValue<int?>(voiceSetting, "pitch") ?? 0, -12, 12);
        var format = (request.OutputFormat ?? GetValue<string>(audioSetting, "format") ?? "mp3").Trim().ToLowerInvariant();
        format = format is "mp3" or "wav" or "flac" or "pcm" ? format : "mp3";

        var payload = CopyObjectProperties(metadata);
        RemoveNullProperties(
            payload,
            metadata,
            "language_boost",
            "subtitle_enable",
            "pronunciation_dict",
            "voice_modify",
            "lyrics",
            "lyrics_optimizer",
            "is_instrumental",
            "audio_base64",
            "audio_url",
            "cover_feature_id");
        payload["model"] = request.Model;
        payload["text"] = request.Text;
        payload["stream"] = stream;
        payload["output_format"] = "hex";
        payload["language_boost"] = GetValue<string>(metadata, "language_boost") ?? MapLanguageToLanguageBoost(request.Language);
        payload["subtitle_enable"] = GetValue<bool?>(metadata, "subtitle_enable");
        payload["pronunciation_dict"] = BuildRawObject(metadata, "pronunciation_dict", "tone");
        payload["voice_modify"] = BuildRawObject(metadata, "voice_modify", "pitch", "intensity", "timbre", "sound_effects");
        payload["voice_setting"] = BuildVoiceSetting(voiceSetting, voiceId, speed, volume, pitch);
        payload["audio_setting"] = BuildAudioSetting(audioSetting, format);
        return payload;
    }

    private static Dictionary<string, object?> BuildVoiceSetting(
        JsonElement metadata,
        string voiceId,
        float speed,
        double volume,
        int pitch)
    {
        var payload = CopyObjectProperties(metadata);
        payload["voice_id"] = voiceId;
        payload["speed"] = speed;
        payload["vol"] = volume;
        payload["pitch"] = pitch;
        payload["emotion"] = GetValue<string>(metadata, "emotion");
        payload["text_normalization"] = GetValue<bool?>(metadata, "text_normalization");
        payload["latex_read"] = GetValue<bool?>(metadata, "latex_read");
        return payload;
    }

    private static Dictionary<string, object?> BuildAudioSetting(JsonElement metadata, string format)
    {
        var payload = CopyObjectProperties(metadata);
        payload["format"] = format;
        payload["sample_rate"] = GetValue<long?>(metadata, "sample_rate");
        payload["bitrate"] = GetValue<long?>(metadata, "bitrate");
        payload["channel"] = GetValue<long?>(metadata, "channel");
        payload["force_cbr"] = GetValue<bool?>(metadata, "force_cbr");
        return payload;
    }

    private static Dictionary<string, object?> CopyObjectProperties(JsonElement element)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return properties;

        foreach (var property in element.EnumerateObject())
            properties[property.Name] = property.Value.Clone();

        return properties;
    }

    private static object? BuildRawObject(JsonElement metadata, string propertyName, params string[] nullableProperties)
    {
        var element = GetObject(metadata, propertyName);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var payload = CopyObjectProperties(element);
        RemoveNullProperties(payload, element, nullableProperties);
        return payload;
    }

    private static void RemoveNullProperties(
        Dictionary<string, object?> payload,
        JsonElement element,
        params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Null)
            {
                payload.Remove(name);
            }
        }
    }

    private static JsonElement GetObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return default;

        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"The JSON value for '{propertyName}' must be an object.");

        return value;
    }

    private static T? GetValue<T>(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return value.Deserialize<T>(SpeechJson);
    }

    private static void ValidateMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("MiniMax provider options must be a JSON object.");

        var voiceSetting = GetObject(metadata, "voice_setting");
        _ = GetValue<string>(voiceSetting, "voice_id");
        _ = GetValue<float?>(voiceSetting, "speed");
        _ = GetValue<double?>(voiceSetting, "vol");
        _ = GetValue<int?>(voiceSetting, "pitch");
        _ = GetValue<string>(voiceSetting, "emotion");
        _ = GetValue<bool?>(voiceSetting, "text_normalization");
        _ = GetValue<bool?>(voiceSetting, "latex_read");

        var audioSetting = GetObject(metadata, "audio_setting");
        _ = GetValue<long?>(audioSetting, "sample_rate");
        _ = GetValue<long?>(audioSetting, "bitrate");
        _ = GetValue<string>(audioSetting, "format");
        _ = GetValue<long?>(audioSetting, "channel");
        _ = GetValue<bool?>(audioSetting, "force_cbr");

        var pronunciationDict = default(JsonElement);
        if (metadata.TryGetProperty("pronunciation_dict", out pronunciationDict)
            && pronunciationDict.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
        {
            throw new JsonException("The JSON value for 'pronunciation_dict' must be an object.");
        }

        var tone = default(JsonElement);
        if (pronunciationDict.ValueKind == JsonValueKind.Object
            && pronunciationDict.TryGetProperty("tone", out tone)
            && tone.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
        {
            throw new JsonException("The JSON value for 'tone' must be an array.");
        }

        if (tone.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tone.EnumerateArray())
            {
                if (item.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    throw new JsonException("Values in 'tone' must be strings.");
            }
        }

        var voiceModify = GetObject(metadata, "voice_modify");
        _ = GetValue<int?>(voiceModify, "pitch");
        _ = GetValue<int?>(voiceModify, "intensity");
        _ = GetValue<int?>(voiceModify, "timbre");
        _ = GetValue<string>(voiceModify, "sound_effects");

        _ = GetValue<string>(metadata, "language_boost");
        _ = GetValue<bool?>(metadata, "subtitle_enable");
        _ = GetValue<string>(metadata, "lyrics");
        _ = GetValue<bool?>(metadata, "lyrics_optimizer");
        _ = GetValue<bool?>(metadata, "is_instrumental");
        _ = GetValue<string>(metadata, "audio_base64");
        _ = GetValue<string>(metadata, "audio_url");
        _ = GetValue<string>(metadata, "cover_feature_id");
    }

    private static string GuessAudioMimeType(string? format)
    {
        var fmt = (format ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            // PCM returned by MiniMax is typically raw; expose as L16 to signal PCM.
            "pcm" => "audio/L16",
            _ => "application/octet-stream"
        };
    }

    private static byte[] DecodeHexStringToBytes(string hex)
    {
        var s = hex.Trim();
        if (s.Length % 2 != 0)
            throw new InvalidOperationException("Hex audio length must be even.");

        var bytes = new byte[s.Length / 2];
        for (int i = 0, j = 0; i < s.Length; i += 2, j++)
        {
            var hi = FromHexNibble(s[i]);
            var lo = FromHexNibble(s[i + 1]);
            bytes[j] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int FromHexNibble(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        throw new InvalidOperationException($"Invalid hex character: '{c}'.");
    }

    // VideoRequest implementation lives in MiniMaxProvider.Videos.cs
}
