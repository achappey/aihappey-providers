using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
    private async IAsyncEnumerable<StreamingSpeechPart> StreamMurfSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one speech text part is required.", nameof(request));
        if (request.OutputFormat?.Rate is <= 0)
            throw new ArgumentException("Speech output sample rate must be positive.", nameof(request));

        var text = new StringBuilder();
        JsonElement? configuration = null;
        Dictionary<string, object?>? payload = null;
        string format = "mp3";
        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Speech parts must contain text.", nameof(request));

            var options = MergeMurfStreamingOptions(request.ProviderOptions, part.ProviderOptions);
            var current = BuildMurfStreamingPayload(request, part.Voice ?? request.Voice, options, "validation");
            var normalized = JsonSerializer.SerializeToElement(current, MurfJson);
            if (configuration is not null && !JsonElement.DeepEquals(configuration.Value, normalized))
                throw new NotSupportedException(
                    "MurfAI HTTP streaming requires one voice and provider-options configuration across text parts.");

            configuration = normalized;
            payload ??= current;
            format = (string)current["format"]!;
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Speech text cannot be empty.", nameof(request));

        payload!["text"] = text.ToString();
        var sampleRate = payload.TryGetValue("sampleRate", out var rate)
            ? JsonSerializer.SerializeToElement(rate).GetInt32()
            : 24000;
        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No MurfAI API key.");

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://global.api.murf.ai/v1/speech/stream"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, MurfJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        httpRequest.Headers.TryAddWithoutValidation("api-key", apiKey);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"MurfAI streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        yield return new SpeechStreamStartPart
        {
            Warnings = string.IsNullOrWhiteSpace(request.Instructions) ? [] : [new { type = "unsupported", feature = "instructions" }]
        };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model,
            Headers = response.GetHeaders()
        };

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        var receivedAudio = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            var audio = Convert.ToBase64String(buffer.AsSpan(0, read));
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = audio };
            yield return new SpeechAudioPart
            {
                Audio = audio,
                AudioFormat = new AudioFormat
                {
                    Type = MapMurfFormatToMimeType(format),
                    Rate = sampleRate
                },
                ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(new { format, sample_rate = sampleRate }) }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("MurfAI returned no speech audio.");
        yield return new SpeechFinishPart();
    }

    private static Dictionary<string, JsonElement> MergeMurfStreamingOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue("murfai", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.murfai must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }

    private static Dictionary<string, object?> BuildMurfStreamingPayload(
        StreamingSpeechRequest request,
        string? partVoice,
        Dictionary<string, JsonElement> options,
        string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var (modelFromModel, voiceFromModel) = ParseSpeechModelAndVoice(request.Model);
        var metadata = JsonSerializer.SerializeToElement(options, MurfJson);
        var model = (modelFromModel
            ?? GetOption<string?>(metadata, "model")
            ?? GetOption<string?>(metadata, "modelVersion")
            ?? "falcon-2.1").Trim();
        if (!model.Equals("falcon-2.1", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("MurfAI HTTP streaming supports Falcon 2.1 only.");

        var voiceId = (voiceFromModel ?? partVoice ?? GetOption<string?>(metadata, "voiceId"))?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException(
                "MurfAI requires a voiceId in the model id, voice, or providerOptions.murfai.voiceId.",
                nameof(request));

        var format = NormalizeMurfFormat(
            request.OutputFormat?.Type
            ?? GetOption<string?>(metadata, "format")) ?? "mp3";
        var locale = request.Language
            ?? GetOption<string?>(metadata, "locale")
            ?? GetOption<string?>(metadata, "multiNativeLocale");
        var rate = request.Speed is { } speed
            ? Math.Clamp((int)Math.Round((speed - 1.0f) * 50.0f, MidpointRounding.AwayFromZero), -50, 50)
            : GetOption<int?>(metadata, "rate");
        var sampleRate = request.OutputFormat?.Rate
            ?? GetOption<int?>(metadata, "sampleRate");
        if (sampleRate is <= 0)
            throw new ArgumentException("MurfAI sampleRate must be positive.", nameof(request));

        var payload = CopyProviderOptions(metadata);
        foreach (var option in new[]
                 {
                     "modelVersion", "encodeAsBase64", "audioDuration", "channelType", "multiNativeLocale",
                     "pitch", "variation", "wordDurationsAsOriginalText"
                 })
        {
            payload.Remove(option);
        }
        SetOption(payload, "text", text);
        SetOption(payload, "voiceId", voiceId);
        SetOption(payload, "model", "falcon-2.1");
        SetOption(payload, "format", format.ToUpperInvariant());
        SetOption(payload, "locale", string.IsNullOrWhiteSpace(locale) ? null : locale);
        SetOption(payload, "rate", rate);
        SetOption(payload, "sampleRate", sampleRate);
        return payload;
    }
}
