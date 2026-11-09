using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Verbatik;

public partial class VerbatikProvider
{
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

        var rawOptions = GetRawProviderOptions(request);
        var model = NormalizeLocalModelId(request.Model);
        var nativeModel = ResolveNativeSpeechModel(model);
        var modelVoice = TryParseVoiceIdFromModel(model);
        var voiceId = modelVoice;
        if (string.IsNullOrWhiteSpace(voiceId))
            voiceId = FirstNonEmpty(request.Voice, ReadOption<string>(rawOptions, "voice"));

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (modelVoice is not null
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat) || !string.IsNullOrWhiteSpace(ReadOption<string>(rawOptions, "format")))
            warnings.Add(new { type = "unsupported", feature = "output_format" });

        var metadataSpeed = ReadOption<float?>(rawOptions, "speed");
        var metadataVolume = ReadOption<float?>(rawOptions, "volume");
        var speed = request.Speed ?? metadataSpeed;
        if (speed is < 0.5f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Verbatik speed must be between 0.5 and 2.0.");
        if (metadataVolume is { } volumeOption && (volumeOption < 0.1f || volumeOption > 10f))
            throw new ArgumentOutOfRangeException("Volume", "Verbatik volume must be between 0.1 and 10.0.");

        var settings = ReadRawSettings(rawOptions);
        if (request.Speed is { } requestSpeed)
            settings["speed"] = requestSpeed;
        else if (!settings.ContainsKey("speed") && metadataSpeed is { } resolvedSpeed)
            settings["speed"] = resolvedSpeed;
        if (!settings.ContainsKey("volume") && metadataVolume is { } volume)
            settings["volume"] = volume;

        var payload = CopyRawOptions(rawOptions);
        RemoveOption(payload, "voice");
        RemoveOption(payload, "speed");
        RemoveOption(payload, "volume");
        if (rawOptions.ValueKind == JsonValueKind.Object
            && rawOptions.TryGetProperty("settings", out var rawSettings)
            && rawSettings.ValueKind == JsonValueKind.Object)
            payload.Remove("settings");
        payload["model"] = nativeModel;
        payload["blocks"] = voiceId is { Length: > 0 }
            ? new[] { new { text = request.Text, voice = voiceId } }
            : new[] { new { text = request.Text } };
        if (settings.Count > 0)
            payload["settings"] = settings;
        payload["delivery"] = "signed_url";

        using var synthRequest = CreateApiRequest(HttpMethod.Post, "speech");
        synthRequest.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web),
            Encoding.UTF8,
            "application/json");

        using var synthResp = await _client.SendAsync(synthRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var synthBody = await synthResp.Content.ReadAsStringAsync(cancellationToken);
        if (!synthResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} speech failed ({(int)synthResp.StatusCode}): {synthBody}");

        using var synthDoc = JsonDocument.Parse(synthBody);
        var created = synthDoc.RootElement.Clone();
        var completed = await WaitForCompletionAsync("speech", created, cancellationToken);
        var output = GetFirstOutput(completed);

        using var audioRequest = new HttpRequestMessage(HttpMethod.Get, output.Url);
        using var audioResponse = await _client.SendAsync(audioRequest, cancellationToken);
        var audioBytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!audioResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResponse.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        var mimeType = ResolveMimeType(
            audioResponse.Content.Headers.ContentType?.MediaType ?? output.MediaType,
            output.Url,
            null);
        var resolvedFormat = ResolveFormat(mimeType, output.Url, null);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                Headers = synthResp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = completed
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                new
                {
                    estimated_credits = GetJsonValue(created, "estimated_credits")
                })
        };
    }

    private static JsonElement? GetJsonValue(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
                ? value.Clone()
                : null;

    private static JsonElement GetRawProviderOptions(SpeechRequest request)
    {
        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(ProviderId, out var options)
            && options.ValueKind == JsonValueKind.Object)
            return options;

        return default;
    }

    private static Dictionary<string, object?> ReadRawSettings(JsonElement rawOptions)
    {
        var settings = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rawOptions.ValueKind == JsonValueKind.Object
            && rawOptions.TryGetProperty("settings", out var rawSettings)
            && rawSettings.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rawSettings.EnumerateObject())
                settings[property.Name] = property.Value.Clone();
        }

        return settings;
    }

    private static Dictionary<string, object?> CopyRawOptions(JsonElement rawOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rawOptions.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in rawOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static void RemoveOption(Dictionary<string, object?> options, string name)
    {
        var matchingKeys = options.Keys.Where(key =>
            string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        foreach (var key in matchingKeys.ToArray())
            options.Remove(key);
    }

    private static T? ReadOption<T>(JsonElement rawOptions, string name)
    {
        if (!TryGetOption(rawOptions, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static bool HasNonNullOption(JsonElement rawOptions, string name)
        => TryGetOption(rawOptions, name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static bool TryGetOption(JsonElement rawOptions, string name, out JsonElement value)
    {
        var found = false;
        value = default;
        if (rawOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rawOptions.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                }
            }
        }

        return found;
    }

    private async Task<JsonElement> WaitForCompletionAsync(
        string operation,
        JsonElement initial,
        CancellationToken cancellationToken)
    {
        if (IsFailed(initial))
            throw new InvalidOperationException($"{ProviderName} {operation} failed: {initial.GetRawText()}");
        if (IsCompleted(initial))
            return initial;

        var id = ReadString(initial, "id")
            ?? throw new InvalidOperationException($"{ProviderName} {operation} response did not include an id.");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        var path = $"{operation}/{Uri.EscapeDataString(id)}";

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            using var request = CreateApiRequest(HttpMethod.Get, path);
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} {operation} status failed ({(int)response.StatusCode}): {body}");

            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.Clone();
            if (IsFailed(status))
                throw new InvalidOperationException($"{ProviderName} {operation} failed: {status.GetRawText()}");
            if (IsCompleted(status))
                return status;
            if (!IsPending(status))
                throw new InvalidOperationException($"{ProviderName} returned an unexpected {operation} status: {status.GetRawText()}");
        }

        throw new TimeoutException($"{ProviderName} {operation} timed out waiting for completion.");
    }

    private static bool IsCompleted(JsonElement response)
        => string.Equals(ReadString(response, "status"), "completed", StringComparison.OrdinalIgnoreCase)
            || (response.TryGetProperty("outputs", out var outputs)
                && outputs.ValueKind == JsonValueKind.Array
                && outputs.GetArrayLength() > 0);

    private static bool IsFailed(JsonElement response)
        => string.Equals(ReadString(response, "status"), "failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsPending(JsonElement response)
    {
        var status = ReadString(response, "status");
        return status is null
            || status.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("processing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
            || status.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Url, string? MediaType) GetFirstOutput(JsonElement response)
    {
        if (response.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray())
            {
                var url = ReadString(output, "url");
                if (!string.IsNullOrWhiteSpace(url))
                    return (url, ReadString(output, "media_type") ?? ReadString(output, "mediaType"));
            }
        }

        throw new InvalidOperationException($"{ProviderName} completed without an audio output URL.");
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
        if (slashIndex > 0
            && string.Equals(value[..slashIndex], ProviderId, StringComparison.OrdinalIgnoreCase))
            value = value[(slashIndex + 1)..];

        return value.Trim();
    }

    private static bool IsTextToMusicModel(string model)
    {
        var localModel = NormalizeLocalModelId(model);
        return string.Equals(localModel, "text-to-music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(localModel, "fal-ai/minimax-music/v2", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNativeSpeechModel(string model)
        => model.Equals("tts", StringComparison.OrdinalIgnoreCase)
            || model.Equals("speech-lite", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("tts/", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("speech-lite/", StringComparison.OrdinalIgnoreCase)
                ? "verbatik/speech-lite"
                : model.Equals("fal-ai/minimax/speech-2.8-turbo", StringComparison.OrdinalIgnoreCase)
                    ? "fal-ai/minimax/speech-2.8-turbo"
                    : throw new NotSupportedException(
                        $"{ProviderName} model '{model}' is not supported. Expected 'tts', 'speech-lite', or a documented native speech model.");

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? TryParseVoiceIdFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var value = model.Trim();

        if (string.Equals(value, "tts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "speech-lite", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "fal-ai/minimax/speech-2.8-turbo", StringComparison.OrdinalIgnoreCase))
            return null;

        var prefix = value.StartsWith("tts/", StringComparison.OrdinalIgnoreCase)
            ? "tts/"
            : value.StartsWith("speech-lite/", StringComparison.OrdinalIgnoreCase)
                ? "speech-lite/"
                : null;
        if (prefix is null)
            return null;

        var voiceId = value[prefix.Length..].Trim();

        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must contain a voice id after the speech model name.", nameof(model));

        if (voiceId.Contains('/'))
            throw new ArgumentException("Voice id cannot contain '/'.", nameof(model));

        return voiceId;
    }
}
