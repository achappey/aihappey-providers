using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechify;

public partial class SpeechifyProvider
{
    private async IAsyncEnumerable<StreamingSpeechPart> StreamSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one Speechify speech text part is required.", nameof(request));

        var (model, modelVoiceId) = ResolveModelAndVoice(request.Model);
        var text = new StringBuilder();
        JsonElement? effectiveConfiguration = null;
        SpeechifySpeechStreamPlan? plan = null;
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("Speechify speech parts must be text with non-null content.", nameof(request));

            var metadata = MergeSpeechifyOptions(request.ProviderOptions, part.ProviderOptions);
            var voiceFromOptions = ReadSpeechifyString(metadata, "voice_id");
            var voiceId = (modelVoiceId ?? voiceFromOptions ?? part.Voice ?? request.Voice)?.Trim();
            if (string.IsNullOrWhiteSpace(voiceId))
                throw new ArgumentException("Speechify requires a voice_id in the model id, provider options, or request voice.", nameof(request));

            var languageValue = request.Language ?? ReadSpeechifyString(metadata, "language");
            var language = string.IsNullOrWhiteSpace(languageValue) ? null : languageValue.Trim();
            var nativeFormat = ReadSpeechifyString(metadata, "output_format")
                ?? ReadSpeechifyString(metadata, "audio_format");
            var requestedFormat = request.OutputFormat?.Type ?? nativeFormat;
            var outputFormat = NormalizeSpeechifyOutputFormat(requestedFormat);
            var normalizedRequestedFormat = requestedFormat?.Trim().ToLowerInvariant();
            if (normalizedRequestedFormat is "wav" or "wave" or "audio/wav" or "audio/x-wav")
                throw new NotSupportedException("Speechify's streaming endpoint does not support WAV output.");
            if (!string.IsNullOrWhiteSpace(requestedFormat) && outputFormat is null)
                throw new NotSupportedException($"Speechify streaming does not document output format '{requestedFormat}'.");
            if (ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate") is { } requestedRate
                && GetSpeechifyOutputSampleRate(outputFormat, null) is { } formatRate
                && requestedRate != formatRate)
                throw new NotSupportedException("Speechify outputFormat.rate must match the selected native output format.");

            var options = PrepareOptions(new Dictionary<string, JsonElement>(metadata, StringComparer.OrdinalIgnoreCase));
            var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in metadata)
            {
                if (!name.Equals("voice_id", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("audio_format", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("output_format", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("language", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("options", StringComparison.OrdinalIgnoreCase))
                    payload[name] = value.Clone();
            }

            payload["input"] = string.Empty;
            payload["voice_id"] = voiceId;
            payload["model"] = model;
            if (language is not null)
                payload["language"] = language;
            if (outputFormat is not null)
                payload["output_format"] = outputFormat;
            if (options is not null)
                payload["options"] = options;

            var configuration = JsonSerializer.SerializeToElement(new
            {
                payload,
                voiceId,
                outputFormat,
                language
            }, SpeechJson);
            if (effectiveConfiguration is not null && !JsonElement.DeepEquals(effectiveConfiguration.Value, configuration))
                throw new NotSupportedException("Speechify streaming requires one effective voice and options configuration across text parts.");

            effectiveConfiguration = configuration;
            plan = new SpeechifySpeechStreamPlan(payload, voiceId, outputFormat);
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Speechify speech text cannot be empty.", nameof(request));

        plan!.Payload["input"] = text.ToString();
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.speechify.ai/v1/audio/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(plan.Payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (plan.OutputFormat is null)
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Speechify streaming TTS failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        var responseFormat = response.Content.Headers.ContentType?.MediaType
            ?? GetSpeechifyOutputMimeType(plan.OutputFormat);
        var sampleRate = GetSpeechifyOutputSampleRate(plan.OutputFormat, response.Content.Headers.ContentType);

        yield return new SpeechStreamStartPart { Warnings = warnings };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = BuildResolvedSpeechifyModelId(model, modelVoiceId ?? plan.VoiceId),
            Headers = response.GetHeaders()
        };

        var receivedAudio = false;
        var buffer = new byte[16 * 1024];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            var chunk = buffer.AsSpan(0, read).ToArray();
            var encoded = Convert.ToBase64String(chunk);
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = encoded };
            yield return new SpeechAudioPart
            {
                Audio = encoded,
                AudioFormat = new AudioFormat { Type = responseFormat, Rate = sampleRate }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("Speechify returned no speech audio.");
        yield return new SpeechFinishPart();
    }

    private static Dictionary<string, JsonElement> MergeSpeechifyOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            var key = options?.Keys.FirstOrDefault(name => string.Equals(name, ProviderName, StringComparison.OrdinalIgnoreCase));
            if (key is null)
                continue;
            var native = options![key];
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.speechify must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }

    private static string? ReadSpeechifyString(IReadOnlyDictionary<string, JsonElement> metadata, string name)
    {
        var key = FindMetadataKey(metadata, name);
        if (key is null || metadata[key].ValueKind == JsonValueKind.Null)
            return null;
        if (metadata[key].ValueKind != JsonValueKind.String)
            throw new JsonException($"Speechify speech option '{name}' must be a string.");
        return metadata[key].GetString();
    }

    private static string? NormalizeSpeechifyOutputFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;
        var normalized = format.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "mpeg" => "mp3",
            "audio/aac" => "aac",
            "audio/ogg" or "audio/opus" => "ogg",
            "audio/pcm" or "audio/l16" => "pcm",
            "audio/basic" or "audio/ulaw" or "mulaw" or "mu-law" or "ulaw" or "u-law" => "ulaw",
            "wave" => "wav",
            var value => value
        };
        return NormalizeSpeechifyStreamingOutputFormat(normalized);
    }

    private static int? GetSpeechifyOutputSampleRate(
        string? outputFormat,
        MediaTypeHeaderValue? contentType)
    {
        var rateParameter = contentType?.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, "rate", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (int.TryParse(rateParameter, out var contentRate))
            return contentRate;

        if (outputFormat is null)
            return 24000;
        foreach (var component in outputFormat.Split('_'))
        {
            if (int.TryParse(component, out var rate) && rate >= 8000)
                return rate;
        }
        return outputFormat.Equals("ulaw", StringComparison.OrdinalIgnoreCase) ? 8000 : 24000;
    }

    private static string GetSpeechifyOutputMimeType(string? outputFormat)
    {
        if (outputFormat is null)
            return "audio/mpeg";
        if (outputFormat.StartsWith("mp3", StringComparison.OrdinalIgnoreCase))
            return "audio/mpeg";
        if (outputFormat.StartsWith("aac", StringComparison.OrdinalIgnoreCase))
            return "audio/aac";
        if (outputFormat.StartsWith("ogg", StringComparison.OrdinalIgnoreCase))
            return "audio/ogg";
        if (outputFormat.StartsWith("ulaw", StringComparison.OrdinalIgnoreCase))
            return "audio/basic";
        if (outputFormat.StartsWith("pcm", StringComparison.OrdinalIgnoreCase))
            return "audio/L16";
        return "application/octet-stream";
    }

    private sealed record SpeechifySpeechStreamPlan(
        Dictionary<string, object?> Payload,
        string VoiceId,
        string? OutputFormat);
}
