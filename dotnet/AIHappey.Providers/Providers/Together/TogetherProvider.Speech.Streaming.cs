using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Together;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one Together speech text part is required.", nameof(request));

        var text = new StringBuilder();
        JsonElement? configuration = null;
        Dictionary<string, object?>? payload = null;
        string? encoding = null;
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("Together speech parts must be text with non-null content.", nameof(request));

            var metadata = MergeTogetherSpeechOptions(request.ProviderOptions, part.ProviderOptions);
            var current = CreateTogetherStreamingSpeechPayload(request, part.Voice, metadata);
            var configurationJson = JsonSerializer.SerializeToElement(current.Payload);
            if (configuration is not null && !JsonElement.DeepEquals(configuration.Value, configurationJson))
                throw new NotSupportedException(
                    "Together streaming speech requires one effective voice and configuration across text parts.");
            configuration = configurationJson;
            payload = current.Payload;
            encoding = current.Encoding;
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Together speech text cannot be empty.", nameof(request));
        payload!["input"] = text.ToString();

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Together)} API key.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Together streaming TTS failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null,
                response.StatusCode);

        var headers = response.GetHeaders();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var receivedAudio = false;
        var receivedDone = false;
        var events = new List<JsonElement>();
        yield return new SpeechStreamStartPart { Warnings = CreateTogetherSpeechWarnings(request) };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier()),
            Headers = headers
        };

        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Data))
                continue;
            if (item.Data.Trim() == "[DONE]")
            {
                receivedDone = true;
                continue;
            }

            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
                throw new JsonException("Together speech event must contain a string type.");

            var eventType = typeElement.GetString()!;
            var rawEvent = root.Clone();
            events.Add(rawEvent);
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = rawEvent };

            if (eventType.Equals("error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Together streaming speech failed: {root.GetRawText()}");
            if (!eventType.Equals("conversation.item.audio_output.delta", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.String)
                throw new JsonException("Together audio delta event must contain base64 audio in delta.");

            var audio = Convert.FromBase64String(delta.GetString()!);
            if (audio.Length == 0)
                continue;
            receivedAudio = true;
            yield return new SpeechAudioPart
            {
                Audio = Convert.ToBase64String(audio),
                AudioFormat = new() { Type = TogetherSpeechMimeType(encoding!), Rate = (int)payload["sample_rate"]! },
                ProviderMetadata = new() { [GetIdentifier()] = rawEvent }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("Together speech stream completed without audio.");
        if (!receivedDone)
            throw new InvalidOperationException("Together speech stream ended without the [DONE] marker.");
        yield return new SpeechFinishPart
        {
            ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(new { events }) }
        };
    }

    private (Dictionary<string, object?> Payload, string Encoding) CreateTogetherStreamingSpeechPayload(
        StreamingSpeechRequest request,
        string? partVoice,
        JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Together speech provider options must be an object.");

        var model = NormalizeTogetherModelName(request.Model);
        var typedMetadata = metadata.Deserialize<TogetherSpeechProviderMetadata>(SpeechSettings);
        var voice = partVoice ?? request.Voice;
        if (string.IsNullOrWhiteSpace(voice))
            voice = ReadTogetherSpeechString(metadata, "voice")
                    ?? ResolveVoice(model, new SpeechRequest(), typedMetadata);
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' is required for Together TTS.", nameof(request));

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        var encoding = ReadTogetherSpeechString(metadata, "response_encoding") ?? "pcm_s16le";
        var format = request.OutputFormat?.Type?.Trim().ToLowerInvariant();
        if (format is not null and not ("" or "raw" or "pcm" or "audio/pcm" or "audio/l16"))
            throw new NotSupportedException(
                $"Together streaming speech requires raw PCM output, not '{request.OutputFormat!.Type}'.");
        if (encoding is not ("pcm_s16le" or "pcm_f32le" or "pcm_mulaw" or "pcm_alaw"))
            throw new NotSupportedException($"Together streaming speech does not support encoding '{encoding}'.");

        var sampleRate = ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate")
                         ?? typedMetadata?.SampleRate
                         ?? 24000;
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.OutputFormat), "Together sample rate must be positive.");

        payload["model"] = model;
        payload["input"] = "validation";
        payload["voice"] = voice;
        payload["stream"] = true;
        payload["response_format"] = "raw";
        payload["response_encoding"] = encoding;
        payload["sample_rate"] = sampleRate;
        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language"] = request.Language;

        return (payload, encoding);
    }

    private static JsonElement MergeTogetherSpeechOptions(
        Dictionary<string, JsonElement>? requestOptions,
        Dictionary<string, JsonElement>? partOptions)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var options in new[] { requestOptions, partOptions })
        {
            if (options?.TryGetValue("together", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.together must be an object.");
            foreach (var property in native.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(merged);
    }

    private static string? ReadTogetherSpeechString(JsonElement metadata, string name)
    {
        foreach (var property in metadata.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new JsonException($"Together speech option '{name}' must be a string.");
            return property.Value.GetString();
        }
        return null;
    }

    private static string TogetherSpeechMimeType(string encoding)
        => encoding switch
        {
            "pcm_mulaw" => "audio/basic",
            "pcm_alaw" => "audio/alaw",
            _ => "audio/pcm"
        };

    private static List<object> CreateTogetherSpeechWarnings(StreamingSpeechRequest request)
    {
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        return warnings;
    }
}
