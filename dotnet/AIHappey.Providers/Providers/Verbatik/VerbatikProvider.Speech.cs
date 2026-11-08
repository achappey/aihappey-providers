using System.Globalization;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Verbatik;

public partial class VerbatikProvider
{
    private static readonly Uri SpeechApiBase = new("https://app.verbatik.com/api/v1/");

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        var req = options.ToSpeechRequest();
        var result = await this.SpeechRequest(req, cancellationToken);

        return result.ToOpenAISpeechAudio();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
        => this.SpeechStreamingAsync(options, cancellationToken);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        if (IsTextToMusicModel(request.Model))
            return await MusicRequest(request, cancellationToken);

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var (model, modelVoice, isLegacyModel) = ResolveSpeechModel(request.Model);
        var metadataVoice = ReadMetadataString(metadata, "voice");
        var voiceId = modelVoice ?? request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            voiceId = metadataVoice;
        if (isLegacyModel && string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required. Use model 'tts/{voiceId}', request.Voice, or provider metadata voice.");

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected voice model" });

        if (!string.IsNullOrWhiteSpace(request.Voice) && modelVoice is not null
            && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var speed = request.Speed ?? ReadMetadataNumber(metadata, "speed");
        var volume = ReadMetadataNumber(metadata, "volume");

        if (speed is { } requestSpeed && (requestSpeed < 0.5f || requestSpeed > 2f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Verbatik speed must be between 0.5 and 2.0.");

        if (volume is { } requestVolume && (requestVolume < 0.1f || requestVolume > 10f))
            throw new ArgumentOutOfRangeException(nameof(request), "Verbatik volume must be between 0.1 and 10.0.");

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat", reason = "The Speech API returns audio in the format indicated by its output media type." });

        var payload = BuildSpeechPayload(request, metadata, model, voiceId, speed, volume, warnings);
        using var synthRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(SpeechApiBase, "speech"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };
        ApplyAuthHeader(synthRequest);

        using var synthResp = await _client.SendAsync(synthRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var createBody = await synthResp.Content.ReadAsStringAsync(cancellationToken);
        if (!synthResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} speech failed ({(int)synthResp.StatusCode}): {createBody}");

        using var createDoc = JsonDocument.Parse(createBody);
        var createRoot = createDoc.RootElement.Clone();
        var result = await WaitForSpeechResultAsync(createRoot, synthResp.GetHeaders(), cancellationToken);
        var audioUrl = ReadAudioUrl(result.Body);
        var audioUri = new Uri(audioUrl, UriKind.Absolute);
        if (!string.Equals(audioUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} returned a non-HTTPS audio URL.");

        using var audioRequest = new HttpRequestMessage(HttpMethod.Get, audioUri);
        using var audioResp = await _client.SendAsync(audioRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        var responseContentType = ReadAudioMediaType(result.Body) ?? audioResp.Content.Headers.ContentType?.MediaType;
        var mimeType = ResolveMimeType(responseContentType, audioUrl, null);
        var resolvedFormat = ResolveFormat(mimeType, audioUrl, null);
        var costCents = ReadDecimal(result.Body, "cost_cents") ?? ReadDecimal(createRoot, "cost_cents");
        if (costCents is null
            && synthResp.Headers.TryGetValues("X-Cost-Cents", out var costValues)
            && decimal.TryParse(
                costValues.FirstOrDefault(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var headerCostCents))
            costCents = headerCostCents;

        decimal? cost;
        if (costCents is { } reportedCost)
        {
            cost = reportedCost / 100m;
        }
        else
        {
            var modelItem = GetIdentifier().GetModels()
                .FirstOrDefault(model => string.Equals(model.Id, $"{ProviderId}/tts", StringComparison.OrdinalIgnoreCase));
            cost = modelItem?.Pricing?.Input is { } inputPrice && request.Text.Length > 0
                ? inputPrice * request.Text.Length
                : null;
        }

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                costs: cost
            ),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Body
            }
        };
    }

    private async Task<(JsonElement Body, Dictionary<string, string> Headers)> WaitForSpeechResultAsync(
        JsonElement createResponse,
        Dictionary<string, string> createHeaders,
        CancellationToken cancellationToken)
    {
        var status = ReadMetadataString(createResponse, "status");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} speech generation failed: {createResponse.GetRawText()}");
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return (createResponse, createHeaders);

        var id = ReadMetadataString(createResponse, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"{ProviderName} speech response did not contain a generation id.");

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(SpeechApiBase, $"speech/{Uri.EscapeDataString(id)}"));
            ApplyAuthHeader(statusRequest);
            using var statusResponse = await _client.SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBody = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!statusResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} speech status failed ({(int)statusResponse.StatusCode}): {responseBody}");

            using var responseDoc = JsonDocument.Parse(responseBody);
            var body = responseDoc.RootElement.Clone();
            status = ReadMetadataString(body, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return (body, statusResponse.GetHeaders());
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{ProviderName} speech generation failed: {body.GetRawText()}");
            if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{ProviderName} returned an unsupported speech status '{status}'.");
        }

        throw new TimeoutException($"{ProviderName} speech generation did not complete within two minutes.");
    }

    private static string ReadAudioUrl(JsonElement response)
    {
        if (response.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray())
            {
                var url = ReadMetadataString(output, "url");
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        throw new InvalidOperationException($"{ProviderName} completed speech response contained no output URL.");
    }

    private static string? ReadAudioMediaType(JsonElement response)
        => response.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Array
            ? outputs.EnumerateArray()
                .Select(output => ReadMetadataString(output, "media_type"))
                .FirstOrDefault(static mediaType => !string.IsNullOrWhiteSpace(mediaType))
            : null;

    private static Dictionary<string, object?> BuildSpeechPayload(
        SpeechRequest request,
        JsonElement metadata,
        string model,
        string? voice,
        float? speed,
        float? volume,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var settingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "speed", "volume", "loudness_normalization", "text_normalization", "tag_compatible"
        };
        var legacyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "contentType", "format", "pitch", "emotion", "englishNormalization", "sampleRate",
            "bitrate", "languageBoost", "voiceModifyPitch", "voiceModifyIntensity", "voiceModifyTimbre"
        };

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (settingNames.Contains(property.Name))
                    settings[property.Name] = property.Value.Clone();
                else if (string.Equals(property.Name, "settings", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var setting in property.Value.EnumerateObject())
                        settings[setting.Name] = setting.Value.Clone();
                }
                else if (!string.Equals(property.Name, "voice", StringComparison.OrdinalIgnoreCase)
                    && !legacyNames.Contains(property.Name))
                {
                    payload[property.Name] = property.Value.Clone();
                }
                else if (legacyNames.Contains(property.Name))
                {
                    warnings.Add(new { type = "unsupported", feature = property.Name });
                }
            }
        }

        if (speed is { } resolvedSpeed)
            settings["speed"] = resolvedSpeed;
        if (volume is { } resolvedVolume)
            settings["volume"] = resolvedVolume;

        payload["model"] = model;
        payload["blocks"] = string.IsNullOrWhiteSpace(voice)
            ? new[] { new Dictionary<string, object?> { ["text"] = request.Text } }
            : new[] { new Dictionary<string, object?> { ["text"] = request.Text, ["voice"] = voice } };
        if (settings.Count > 0)
            payload["settings"] = settings;

        return payload;
    }

    private static (string Model, string? Voice, bool IsLegacy) ResolveSpeechModel(string model)
    {
        var value = model.Trim();
        if (value.StartsWith($"{ProviderId}/", StringComparison.OrdinalIgnoreCase))
            value = value[(ProviderId.Length + 1)..];

        if (string.Equals(value, "tts", StringComparison.OrdinalIgnoreCase))
            return ("verbatik/speech-lite", null, true);

        if (value.StartsWith("tts/", StringComparison.OrdinalIgnoreCase))
        {
            var voice = value["tts/".Length..].Trim();
            if (string.IsNullOrWhiteSpace(voice) || voice.Contains('/'))
                throw new ArgumentException("Model must contain a voice id after 'tts/'.", nameof(model));
            return ("verbatik/speech-lite", voice, true);
        }

        if (string.Equals(value, "speech-lite", StringComparison.OrdinalIgnoreCase))
            return ("verbatik/speech-lite", null, false);

        return (value, null, false);
    }

    private static string? ReadMetadataString(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }

    private static float? ReadMetadataNumber(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetSingle(out var value))
                return value;
        }

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, "settings", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var setting in property.Value.EnumerateObject())
                {
                    if (string.Equals(setting.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                        && setting.Value.ValueKind == JsonValueKind.Number
                        && setting.Value.TryGetSingle(out var nestedValue))
                        return nestedValue;
                }
            }
        }

        return null;
    }

    private static string ResolveMimeType(string? contentType, string? audioUrl, string? requestedOutputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var requested = NormalizeFormat(requestedOutputFormat);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested switch
            {
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "opus" => "audio/ogg",
                "flac" => "audio/flac",
                "aac" => "audio/aac",
                "pcm" => "audio/pcm",
                _ => "audio/mpeg"
            };

        var ext = Path.GetExtension(audioUrl ?? string.Empty).Trim('.').ToLowerInvariant();
        return ext switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "pcm" => "audio/pcm",
            "mp3" => "audio/mpeg",
            _ => "audio/mpeg"
        };
    }

    private static string ResolveFormat(string mimeType, string? audioUrl, string? requestedOutputFormat)
    {
        var requested = NormalizeFormat(requestedOutputFormat);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("wav")) return "wav";
        if (mt.Contains("ogg")) return "ogg";
        if (mt.Contains("flac")) return "flac";
        if (mt.Contains("aac")) return "aac";
        if (mt.Contains("pcm")) return "pcm";

        var ext = Path.GetExtension(audioUrl ?? string.Empty).Trim('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ext))
            return ext switch
            {
                "mpeg" => "mp3",
                "wave" => "wav",
                _ => ext
            };

        return "mp3";
    }

    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => value
        };
    }

    private static string NormalizeLocalModelId(string model)
    {
        var value = model.Trim();

        var slashIndex = value.IndexOf('/');
        if (slashIndex > 0)
        {
            // Allows both:
            // tts
            // tts/voice-id
            // verbatik/tts
            // verbatik/tts/voice-id
            var firstPart = value[..slashIndex];

            if (!string.Equals(firstPart, "tts", StringComparison.OrdinalIgnoreCase))
                value = value[(slashIndex + 1)..];
        }

        return value.Trim();
    }

    private static bool IsTextToMusicModel(string model)
        => string.Equals(NormalizeLocalModelId(model), "text-to-music", StringComparison.OrdinalIgnoreCase);

}
