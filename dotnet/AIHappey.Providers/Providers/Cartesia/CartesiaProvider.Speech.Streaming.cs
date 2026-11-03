using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one Cartesia speech text part is required.", nameof(request));
        if (request.OutputFormat?.Rate is <= 0)
            throw new ArgumentException("Speech output sample rate must be positive.", nameof(request));

        var (model, voiceId) = ParseTtsModelAndVoiceFromModel(request.Model);
        var text = new StringBuilder();
        JsonElement? selectedConfiguration = null;
        JsonElement metadata = default;
        Dictionary<string, object?> payload = [];
        string? apiVersion = null;
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Voice) &&
            !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Cartesia speech parts must be text with non-null content.", nameof(request));

            var currentMetadata = MergeCartesiaOptions(request.ProviderOptions, part.ProviderOptions);
            var currentPayload = BuildStreamingSpeechPayload(request, currentMetadata, voiceId);
            var currentConfiguration = JsonSerializer.SerializeToElement(currentPayload.Payload);
            if (selectedConfiguration is not null &&
                (!JsonElement.DeepEquals(selectedConfiguration.Value, currentConfiguration) ||
                 !string.Equals(apiVersion, currentPayload.ApiVersion, StringComparison.Ordinal)))
                throw new NotSupportedException("Cartesia streaming speech requires one effective provider-options configuration across text parts.");

            selectedConfiguration = currentConfiguration;
            metadata = currentMetadata;
            payload = currentPayload.Payload;
            apiVersion = currentPayload.ApiVersion;
            text.Append(part.Text);
            if (!string.IsNullOrWhiteSpace(part.Voice) &&
                !string.Equals(part.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
                warnings.Add(new { type = "ignored", feature = "text-part voice", reason = "voice is derived from model id" });
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Cartesia speech text cannot be empty.", nameof(request));
        payload["transcript"] = text.ToString();
        if (!model.StartsWith("sonic-3", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Speed is not null || GetFloatOption(metadata, "speed") is not null)
                warnings.Add(new { type = "ignored", feature = "speed", reason = "generation_config.speed only affects sonic-3 models" });
            if (GetFloatOption(metadata, "volume") is not null)
                warnings.Add(new { type = "ignored", feature = "volume", reason = "generation_config.volume only affects sonic-3 models" });
            if (!string.IsNullOrWhiteSpace(GetStringOption(metadata, "emotion")))
                warnings.Add(new { type = "ignored", feature = "emotion", reason = "generation_config.emotion only affects sonic-3 models" });
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/sse")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        ApplyVersionHeader(httpRequest, apiVersion);
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{ProviderName} streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        yield return new SpeechStreamStartPart
        {
            Warnings = warnings.DistinctBy(warning => JsonSerializer.Serialize(warning))
        };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        var sampleRate = GetIntOption(metadata, "sampleRate") ??
            ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate") ?? 44100;
        var events = new List<JsonElement>();
        var receivedAudio = false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            using var document = ParseCartesiaSpeechStreamEvent(data);
            var root = document.RootElement;
            var eventType = GetCartesiaSpeechStreamString(root, "type");
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = root.Clone() };

            switch (eventType)
            {
                case "chunk":
                    var audio = GetCartesiaSpeechStreamString(root, "data");
                    if (string.IsNullOrWhiteSpace(audio))
                        throw new InvalidOperationException("Cartesia speech SSE chunk event did not include audio data.");
                    receivedAudio = true;
                    var partMetadata = root.EnumerateObject()
                        .Where(property => property.Name != "data")
                        .ToDictionary(property => property.Name, property => property.Value.Clone());
                    yield return new SpeechAudioPart
                    {
                        Audio = NormalizeCartesiaSpeechStreamAudio(audio),
                        AudioFormat = new AudioFormat { Type = "audio/pcm", Rate = sampleRate },
                        ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(partMetadata) }
                    };
                    break;

                case "done":
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!receivedAudio)
                        throw new InvalidOperationException("Cartesia streaming TTS ended without audio.");
                    yield return new SpeechFinishPart
                    {
                        ProviderMetadata = CartesiaStreamMetadata(events)
                    };
                    yield break;

                case "error":
                    throw new InvalidOperationException(
                        $"{ProviderName} streaming TTS failed: {ReadCartesiaSpeechStreamError(root)}");

                case "timestamps":
                case "phoneme_timestamps":
                    events.Add(root.Clone());
                    break;

                default:
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(eventType)
                            ? $"Cartesia speech SSE event did not include a type: {data}"
                            : $"Unsupported Cartesia speech SSE event type '{eventType}'.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("Cartesia streaming TTS ended without audio.");
        yield return new SpeechFinishPart { ProviderMetadata = CartesiaStreamMetadata(events) };
    }

    private static (Dictionary<string, object?> Payload, string? ApiVersion) BuildStreamingSpeechPayload(
        StreamingSpeechRequest request,
        JsonElement metadata,
        string voiceId)
    {
        ValidateMetadataObject(metadata);
        var apiVersion = GetStringOption(metadata, "apiVersion");
        var container = NormalizeContainer(request.OutputFormat?.Type ?? GetStringOption(metadata, "container")) ?? "raw";
        if (container != "raw")
            throw new NotSupportedException("Cartesia SSE streaming supports only raw PCM output.");

        var sampleRate = ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate") ??
            GetIntOption(metadata, "sampleRate") ?? 44100;
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Cartesia sample rate must be positive.");
        var encoding = GetStringOption(metadata, "encoding");
        var speed = request.Speed ?? GetFloatOption(metadata, "speed");
        if (speed is { } speedValue && (speedValue < 0.6f || speedValue > 1.5f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Cartesia speed must be between 0.6 and 1.5.");
        var volume = GetFloatOption(metadata, "volume");
        if (volume is { } volumeValue && (volumeValue < 0.5f || volumeValue > 2.0f))
            throw new ArgumentOutOfRangeException("Volume", "Cartesia volume must be between 0.5 and 2.0.");
        var emotion = GetStringOption(metadata, "emotion");
        var language = (request.Language ?? GetStringOption(metadata, "language"))?.Trim();
        var payload = CopyAdditionalOptions(metadata);
        payload["model_id"] = ParseTtsModelAndVoiceFromModel(request.Model).TtsModelId;
        payload["transcript"] = "validation";
        SetMappedObject(payload, "voice", new Dictionary<string, object?>
        {
            ["mode"] = "id",
            ["id"] = voiceId
        });
        SetMappedObject(payload, "output_format", new Dictionary<string, object?>
        {
            ["container"] = "raw",
            ["encoding"] = NormalizeEncoding(encoding) ?? "pcm_s16le",
            ["sample_rate"] = sampleRate
        });
        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        var isSonic3 = payload["model_id"] is string modelId &&
            modelId.StartsWith("sonic-3", StringComparison.OrdinalIgnoreCase);
        if (isSonic3)
        {
            var generationConfig = new Dictionary<string, object?>();
            if (speed is not null)
                generationConfig["speed"] = speed.Value;
            if (volume is not null)
                generationConfig["volume"] = volume.Value;
            if (!string.IsNullOrWhiteSpace(emotion))
                generationConfig["emotion"] = emotion.Trim();
            if (generationConfig.Count > 0)
                SetMappedObject(payload, "generation_config", generationConfig);
        }
        return (payload, apiVersion);
    }

    private static JsonElement MergeCartesiaOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue(ProviderId, out var metadata) != true)
                continue;
            ValidateMetadataObject(metadata);
            if (metadata.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in metadata.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(values);
    }

    private static Dictionary<string, JsonElement> CartesiaStreamMetadata(IReadOnlyCollection<JsonElement> events)
        => new()
        {
            [ProviderId] = JsonSerializer.SerializeToElement(new { events })
        };
}
