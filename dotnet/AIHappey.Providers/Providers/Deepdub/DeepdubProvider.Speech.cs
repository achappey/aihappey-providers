using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Deepdub;

public partial class DeepdubProvider
{
    private sealed record SpeechPlan(Dictionary<string, object?> Payload, Uri Endpoint, string Format, int? Rate, List<object> Warnings);

    private static SpeechPlan PrepareSpeech(SpeechRequest request, int? rate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var metadata = request.GetProviderMetadata<JsonElement>(ProviderId);
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException("Deepdub provider options must be a JSON object.");

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();

        string? ReadString(string name)
        {
            if (!payload.TryGetValue(name, out var value) || value is null)
                return null;
            if (value is JsonElement element)
                return element.ValueKind == JsonValueKind.Null ? null : element.Deserialize<string>();
            return (string)value;
        }

        void Set(string name, object value)
        {
            payload.Remove(name);
            payload[name] = value;
        }

        var region = ReadString("region")?.Trim().ToLowerInvariant() ?? "us";
        payload.Remove("region");
        var endpoint = region switch
        {
            "us" => new Uri("https://restapi.deepdub.ai/api/v1/tts"),
            "eu" => new Uri("https://restapi.eu.deepdub.ai/api/v1/tts"),
            _ => throw new ArgumentException("Deepdub region must be 'us' or 'eu'.")
        };
        var model = request.Model.StartsWith("deepdub/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["deepdub/".Length..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Set("model", model);
        Set("targetText", request.Text);
        var locale = !string.IsNullOrWhiteSpace(request.Language) ? request.Language : ReadString("locale");
        var voice = !string.IsNullOrWhiteSpace(request.Voice) ? request.Voice : ReadString("voicePromptId");
        if (string.IsNullOrWhiteSpace(locale))
            throw new ArgumentException("Deepdub requires a locale via language or providerOptions.deepdub.locale.");
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Deepdub requires a voicePromptId via voice or providerOptions.deepdub.voicePromptId.");
        Set("locale", locale);
        Set("voicePromptId", voice);

        var format = (request.OutputFormat ?? ReadString("format") ?? "mp3").Trim().ToLowerInvariant() switch
        {
            "mp3" or "mpeg" or "audio/mpeg" or "audio/mp3" => "mp3",
            "opus" or "audio/opus" or "audio/ogg" => "opus",
            "mulaw" or "mu-law" or "ulaw" or "pcmu" or "audio/basic" or "audio/pcmu" => "mulaw",
            _ => throw new NotSupportedException("Deepdub REST supports mp3, opus, and mulaw only.")
        };
        Set("format", format);
        if (request.Speed is { } speed)
            Set("tempo", speed);
        if (rate is not null)
            Set("sampleRate", rate.Value);
        else if (payload.TryGetValue("sampleRate", out var sampleRate) && sampleRate is JsonElement { ValueKind: not JsonValueKind.Null } sampleRateElement)
            rate = sampleRateElement.Deserialize<int>();
        if (rate is null && format == "mulaw")
            rate = 8000;
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        return new(payload, endpoint, format, rate, warnings);
    }

    private HttpRequestMessage CreateSpeechMessage(SpeechPlan plan)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, plan.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(plan.Payload), Encoding.UTF8, "application/json")
        };
        ApplyAuthHeader(message);
        return message;
    }

    private static async Task EnsureSpeechSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Deepdub TTS failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}", null, response.StatusCode);
    }

    private static string MimeType(HttpResponseMessage response, string format) =>
        response.Content.Headers.ContentType?.MediaType is { } contentType && contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        ? contentType
        : format switch
    {
        "opus" => "audio/ogg",
        "mulaw" => "audio/basic",
        _ => "audio/mpeg"
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var plan = PrepareSpeech(request);
        using var message = CreateSpeechMessage(plan);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSpeechSuccessAsync(response, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0) throw new InvalidOperationException("Deepdub returned empty audio.");
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), Format = plan.Format, MimeType = MimeType(response, plan.Format) },
            Warnings = plan.Warnings,
            Request = new() { Body = plan.Payload },
            ProviderMetadata = ProviderId.CreatePrimitiveProviderMetadata(),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = $"{ProviderId}/{plan.Payload["model"]}" }
        };
    }

    private static SpeechRequest ToNativeSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties?.TryGetValue("providerOptions", out var providerOptions) == true)
            request.ProviderOptions = providerOptions.Deserialize<Dictionary<string, JsonElement>>();
        else if (options.AdditionalProperties?.TryGetValue(ProviderId, out var native) == true)
            request.ProviderOptions = new() { [ProviderId] = native.Clone() };
        return request;
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => (await SpeechRequest(ToNativeSpeechRequest(options), cancellationToken)).ToOpenAISpeechAudio();

    private static async IAsyncEnumerable<byte[]> ReadChunks(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16384];
        var received = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            received = true;
            yield return buffer[..count];
        }
        if (!received) throw new InvalidOperationException("Deepdub returned empty audio.");
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var plan = PrepareSpeech(ToNativeSpeechRequest(options));
        using var message = CreateSpeechMessage(plan);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSpeechSuccessAsync(response, cancellationToken);
        await foreach (var chunk in ReadChunks(response, cancellationToken))
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(chunk) };
        yield return new AudioSpeechStreamDone();
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null || request.Text.Length == 0 || request.Text.Any(p => p is null || p.Type != "text" || p.Text is null))
            throw new ArgumentException("Deepdub requires text parts.", nameof(request));
        var text = string.Concat(request.Text.Select(p => p.Text));
        SpeechPlan? plan = null;
        foreach (var part in request.Text.Where(p => p.Text.Length > 0))
        {
            var current = PrepareSpeech(new SpeechRequest
            {
                Model = request.Model, Text = text, Voice = part.Voice ?? request.Voice,
                Language = request.Language, Speed = request.Speed, Instructions = request.Instructions,
                OutputFormat = request.OutputFormat?.Type,
                ProviderOptions = MergeOptions(request.ProviderOptions, part.ProviderOptions)
            }, ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate"));
            if (plan is not null && (plan.Endpoint != current.Endpoint ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(plan.Payload), JsonSerializer.SerializeToElement(current.Payload))))
                throw new NotSupportedException("Deepdub REST requires one voice and configuration per request.");
            plan = current;
        }
        if (plan is null) throw new ArgumentException("Deepdub text cannot be empty.", nameof(request));
        using var message = CreateSpeechMessage(plan);
        yield return new SpeechStreamStartPart { Warnings = plan.Warnings };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSpeechSuccessAsync(response, cancellationToken);
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{plan.Payload["model"]}",
            Headers = response.GetHeaders()
        };
        await foreach (var chunk in ReadChunks(response, cancellationToken))
        {
            var audio = Convert.ToBase64String(chunk);
            if (request.IncludeRawChunks == true) yield return new SpeechRawPart { RawValue = audio };
            yield return new SpeechAudioPart { Audio = audio, AudioFormat = new AudioFormat { Type = MimeType(response, plan.Format), Rate = plan.Rate } };
        }
        yield return new SpeechFinishPart { ProviderMetadata = ProviderId.CreatePrimitiveProviderMetadata() };
    }

    private static Dictionary<string, JsonElement>? MergeOptions(Dictionary<string, JsonElement>? defaults, Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue(ProviderId, out var native) != true) continue;
            if (native.ValueKind != JsonValueKind.Object) throw new JsonException("Deepdub provider options must be a JSON object.");
            foreach (var property in native.EnumerateObject()) values[property.Name] = property.Value.Clone();
        }
        return values.Count == 0 ? null : new() { [ProviderId] = JsonSerializer.SerializeToElement(values) };
    }
}
