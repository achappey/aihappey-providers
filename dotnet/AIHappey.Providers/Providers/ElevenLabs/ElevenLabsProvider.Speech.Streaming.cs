using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    private void ApplyAuthHeader(HttpRequestMessage request)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No ElevenLabs API key.");

        _client.BaseAddress ??= new Uri("https://api.elevenlabs.io/");
        request.Headers.Add("xi-api-key", key);
    }

    private async IAsyncEnumerable<StreamingSpeechPart> StreamSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var audioRequest = CreateStreamingAudioRequest(request);
        var outputFormat = ResolveElevenLabsOutputFormat(
            audioRequest.ResponseFormat,
            ElevenLabsOpenAISpeechKind.TextToSpeech);
        var mimeType = ResolveElevenLabsStreamMimeType(outputFormat);
        var rate = request.OutputFormat?.Rate ?? ResolveElevenLabsStreamRate(outputFormat);
        var receivedAudio = false;
        var completed = false;
        var metadataSent = false;

        yield return new SpeechStreamStartPart();

        await foreach (var streamEvent in OpenAISpeechStreamingAsync(audioRequest, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (streamEvent)
            {
                case AudioSpeechStreamDelta delta:
                    if (completed)
                        throw new InvalidOperationException("ElevenLabs returned audio after speech stream completion.");
                    if (string.IsNullOrWhiteSpace(delta.Audio))
                        continue;

                    if (!metadataSent)
                    {
                        yield return new SpeechResponseMetadataPart
                        {
                            Timestamp = DateTime.UtcNow,
                            ModelId = request.Model
                        };
                        metadataSent = true;
                    }
                    receivedAudio = true;
                    if (request.IncludeRawChunks == true)
                        yield return new SpeechRawPart { RawValue = delta.Audio };
                    yield return new SpeechAudioPart
                    {
                        Audio = delta.Audio,
                        AudioFormat = new() { Type = mimeType, Rate = rate }
                    };
                    break;

                case AudioSpeechStreamDone:
                    if (completed)
                        throw new InvalidOperationException("ElevenLabs returned more than one speech completion event.");
                    completed = true;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"ElevenLabs returned unsupported speech event '{streamEvent.GetType().Name}'.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!completed)
            throw new InvalidOperationException("ElevenLabs speech stream ended without a completion event.");
        if (!receivedAudio)
            throw new InvalidOperationException("ElevenLabs speech stream completed without audio data.");
        if (!metadataSent)
            yield return new SpeechResponseMetadataPart
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model
            };

        yield return new SpeechFinishPart();
    }

    private static AudioSpeechRequest CreateStreamingAudioRequest(StreamingSpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one ElevenLabs speech text part is required.", nameof(request));

        var text = new System.Text.StringBuilder();
        string? voice = null;
        Dictionary<string, JsonElement>? effectiveOptions = null;
        JsonElement? effectiveOptionsElement = null;
        var configurationInitialized = false;

        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("ElevenLabs speech parts must be text with non-null content.", nameof(request));

            var partVoice = part.Voice ?? request.Voice;
            var options = MergeElevenLabsProviderOptions(request.ProviderOptions, part.ProviderOptions);
            var optionsElement = options is null ? default : JsonSerializer.SerializeToElement(options);

            if (!configurationInitialized)
            {
                voice = partVoice;
                effectiveOptions = options;
                effectiveOptionsElement = options is null ? null : optionsElement;
                configurationInitialized = true;
            }
            else if (!string.Equals(voice, partVoice, StringComparison.Ordinal)
                     || (effectiveOptionsElement is null) != (options is null)
                     || (effectiveOptionsElement is { } existing
                         && options is not null
                         && !JsonElement.DeepEquals(existing, optionsElement)))
            {
                throw new NotSupportedException(
                    "ElevenLabs speech streaming requires one effective voice and provider-options configuration across text parts.");
            }

            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("ElevenLabs speech text cannot be empty.", nameof(request));

        var responseFormat = request.OutputFormat?.Type;
        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = GetElevenLabsOptionString(effectiveOptions, "output_format");
        if (request.OutputFormat?.Rate is { } requestedRate
            && (string.IsNullOrWhiteSpace(responseFormat)
                || !responseFormat.Contains('_')
                || ResolveElevenLabsStreamRate(responseFormat) is not { } nativeRate
                || nativeRate != requestedRate))
        {
            throw new NotSupportedException(
                $"ElevenLabs requires a native output_format matching requested sample rate '{requestedRate}'.");
        }

        return new AudioSpeechRequest
        {
            Model = request.Model,
            Input = text.ToString(),
            Voice = voice,
            ResponseFormat = responseFormat,
            Speed = request.Speed,
            AdditionalProperties = effectiveOptions
        };
    }

    private static Dictionary<string, JsonElement>? MergeElevenLabsProviderOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue("elevenlabs", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.elevenlabs must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }

        return values.Count == 0 ? null : values;
    }

    private static string? GetElevenLabsOptionString(
        Dictionary<string, JsonElement>? options,
        string name)
        => options?.TryGetValue(name, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ResolveElevenLabsStreamMimeType(string outputFormat)
        => outputFormat.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/ogg",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            "ulaw" => "audio/basic",
            "alaw" => "audio/alaw",
            _ => "application/octet-stream"
        };

    private static int? ResolveElevenLabsStreamRate(string outputFormat)
    {
        var components = outputFormat.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return components.Length > 1
               && int.TryParse(components[1], System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? rate
            : null;
    }
}
