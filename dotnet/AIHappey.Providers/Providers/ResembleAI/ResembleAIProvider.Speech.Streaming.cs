using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{
    private async IAsyncEnumerable<StreamingSpeechPart> StreamResembleSpeechAsync(
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
        string? voiceUuid = null;
        string? format = null;
        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Speech parts must contain text.", nameof(request));

            var options = MergeResembleStreamingOptions(request.ProviderOptions, part.ProviderOptions);
            var current = BuildResembleStreamingPayload(
                request,
                part.Voice ?? request.Voice,
                options,
                "validation");
            var normalized = JsonSerializer.SerializeToElement(current, SpeechJson);
            if (configuration is not null && !JsonElement.DeepEquals(configuration.Value, normalized))
                throw new NotSupportedException(
                    "ResembleAI HTTP streaming requires one voice and provider-options configuration across text parts.");

            configuration = normalized;
            payload ??= current;
            voiceUuid ??= (string?)current["voice_uuid"];
            format ??= request.OutputFormat?.Type
                ?? ReadProviderOption<string>(JsonSerializer.SerializeToElement(options), "output_format")
                ?? "wav";
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Speech text cannot be empty.", nameof(request));

        payload!["data"] = text.ToString();
        var effectiveFormat = NormalizeResembleOutputFormat(format) ?? "wav";
        int? sampleRate = payload.TryGetValue("sample_rate", out var rate)
            ? JsonSerializer.SerializeToElement(rate).GetInt32()
            : null;

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://f.cluster.resemble.ai/stream"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        ApplyAuthHeader(httpRequest);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ResembleAI streaming TTS failed ({(int)response.StatusCode}): {error}");
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
            var metadata = new Dictionary<string, object?>
            {
                ["voice_uuid"] = voiceUuid,
                ["output_format"] = effectiveFormat
            };
            if (sampleRate is not null)
                metadata["sample_rate"] = sampleRate.Value;

            yield return new SpeechAudioPart
            {
                Audio = audio,
                AudioFormat = new AudioFormat
                {
                    Type = effectiveFormat == "mp3" ? "audio/mpeg" : "audio/wav",
                    Rate = sampleRate
                },
                ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata) }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("ResembleAI returned no speech audio.");
        yield return new SpeechFinishPart();
    }

    private static Dictionary<string, JsonElement> MergeResembleStreamingOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue("resembleai", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.resembleai must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }

    private Dictionary<string, object?> BuildResembleStreamingPayload(
        StreamingSpeechRequest request,
        string? partVoice,
        Dictionary<string, JsonElement> options,
        string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var (_, modelVoiceUuid) = ParseSpeechModelAndVoice(request.Model);
        var metadata = JsonSerializer.SerializeToElement(options, SpeechJson);
        var voiceUuid = (modelVoiceUuid ?? partVoice
            ?? ReadProviderOption<string>(metadata, "voice_uuid"))?.Trim();
        if (string.IsNullOrWhiteSpace(voiceUuid))
            throw new ArgumentException(
                "ResembleAI requires a voice UUID in the model id, voice, or providerOptions.resembleai.voice_uuid.",
                nameof(request));

        var format = NormalizeResembleOutputFormat(
            request.OutputFormat?.Type
            ?? ReadProviderOption<string>(metadata, "output_format"));
        if (format is not null and not "wav")
            throw new NotSupportedException(
                $"ResembleAI HTTP streaming supports only wav output; received '{format}'.");

        var payload = options.ToDictionary(
            property => property.Key,
            property => (object?)property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        payload.Remove("model");
        payload.Remove("output_format");
        payload["voice_uuid"] = voiceUuid;
        payload["data"] = text;
        if (request.OutputFormat?.Rate is { } sampleRate)
            payload["sample_rate"] = sampleRate;
        return payload;
    }
}
