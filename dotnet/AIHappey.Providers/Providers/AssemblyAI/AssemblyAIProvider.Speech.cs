using System.Net.ServerSentEvents;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider
{
    private static readonly HashSet<int> AssemblyAISampleRates = [8000, 16000, 22050, 24000, 44100, 48000];

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var plan = CreateAssemblyAISpeechPlan(
            request.Model,
            request.Text,
            request.Voice,
            request.Language,
            request.OutputFormat,
            null,
            request.Speed,
            metadata,
            streaming: false);
        SetAssemblyAIOption(plan.Payload, "stream", false);

        using var httpRequest = CreateAssemblyAISpeechRequest("https://tts.us.assemblyai.com/v1/speech", plan.Payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"AssemblyAI speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}",
                null,
                response.StatusCode);

        var mimeType = response.Content.Headers.ContentType?.MediaType ?? plan.MimeType;
        var headers = response.GetHeaders();
        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = AssemblyAIFormatFromMimeType(mimeType, plan.Encoding)
            },
            Warnings = CreateAssemblyAISpeechWarnings(request.Instructions),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new() { Body = plan.Payload },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = headers.TryGetValue("X-TTS-Model", out var model)
                    ? model
                    : request.Model
            }
        };
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one AssemblyAI speech text part is required.", nameof(request));

        var text = new StringBuilder();
        AssemblyAISpeechPlan? plan = null;
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("AssemblyAI speech parts must be text with non-null content.", nameof(request));

            var options = MergeAssemblyAISpeechOptions(request.ProviderOptions, part.ProviderOptions);
            var current = CreateAssemblyAISpeechPlan(
                request.Model,
                "validation",
                part.Voice ?? request.Voice,
                request.Language,
                request.OutputFormat?.Type,
                request.OutputFormat?.Rate,
                request.Speed,
                options,
                streaming: true);
            if (plan is not null && !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(plan.Payload),
                    JsonSerializer.SerializeToElement(current.Payload)))
                throw new NotSupportedException(
                    "AssemblyAI speech events require one effective voice and configuration across text parts.");
            plan = current;
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("AssemblyAI speech text cannot be empty.", nameof(request));
        SetAssemblyAIOption(plan!.Payload, "text", text.ToString());

        using var httpRequest = CreateAssemblyAISpeechRequest("https://tts.us.assemblyai.com/v1/speech/events", plan.Payload);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"AssemblyAI streaming speech failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null,
                response.StatusCode);

        var headers = response.GetHeaders();
        var modelId = headers.TryGetValue("X-TTS-Model", out var responseModel) ? responseModel : request.Model;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var receivedAudio = false;
        var receivedFlushDone = false;
        var receivedTermination = false;
        var events = new List<JsonElement>();
        yield return new SpeechStreamStartPart { Warnings = CreateAssemblyAISpeechWarnings(request.Instructions) };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = modelId,
            Headers = headers
        };

        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Data))
                continue;

            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
                throw new JsonException("AssemblyAI speech event must contain a string type.");

            var eventType = typeElement.GetString()!;
            var rawEvent = root.Clone();
            events.Add(rawEvent);
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = rawEvent };

            if (eventType.Equals("Error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"AssemblyAI speech failed: {root.GetRawText()}");
            if (eventType.Equals("Audio", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("audio", out var audio)
                    || audio.ValueKind != JsonValueKind.String)
                    throw new JsonException("AssemblyAI Audio event must contain base64 audio.");
                var audioBytes = Convert.FromBase64String(audio.GetString()!);
                if (audioBytes.Length == 0)
                    continue;

                receivedAudio = true;
                yield return new SpeechAudioPart
                {
                    Audio = Convert.ToBase64String(audioBytes),
                    AudioFormat = new() { Type = plan.MimeType, Rate = plan.SampleRate },
                    ProviderMetadata = new() { [GetIdentifier()] = rawEvent }
                };
            }
            else if (eventType.Equals("FlushDone", StringComparison.OrdinalIgnoreCase))
            {
                receivedFlushDone = true;
            }
            else if (eventType.Equals("Termination", StringComparison.OrdinalIgnoreCase))
            {
                receivedTermination = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("AssemblyAI speech events completed without audio.");
        if (!receivedFlushDone || !receivedTermination)
            throw new InvalidOperationException("AssemblyAI speech events ended before FlushDone and Termination.");

        yield return new SpeechFinishPart
        {
            ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(new { events }) }
        };
    }

    private HttpRequestMessage CreateAssemblyAISpeechRequest(string endpoint, Dictionary<string, object?> payload)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(AssemblyAI)} API key.");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("Authorization", key);
        return request;
    }

    private AssemblyAISpeechPlan CreateAssemblyAISpeechPlan(
        string model,
        string text,
        string? voice,
        string? language,
        string? outputFormat,
        double? sampleRate,
        float? speed,
        JsonElement metadata,
        bool streaming)
    {
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null and not JsonValueKind.Object)
            throw new JsonException("AssemblyAI speech provider options must be an object.");
        if (speed is { } configuredSpeed && configuredSpeed != 1)
            throw new NotSupportedException("AssemblyAI speech only supports speed 1.");

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        SetAssemblyAIOption(payload, "text", text);
        SetAssemblyAIOption(payload, "model", NormalizeAssemblyAIModel(model));
        if (!string.IsNullOrWhiteSpace(voice))
            SetAssemblyAIOption(payload, "voice_id", voice);
        if (!string.IsNullOrWhiteSpace(language))
            SetAssemblyAIOption(payload, "language", language);

        var encoding = ReadAssemblyAIOption(payload, "encoding") as string ?? "pcm_s16le";
        var format = outputFormat?.Trim().ToLowerInvariant();
        if (format is not null)
        {
            if (streaming)
            {
                encoding = format switch
                {
                    "pcm" or "audio/pcm" or "audio/l16" or "pcm_s16le" => "pcm_s16le",
                    "mulaw" or "ulaw" or "audio/basic" or "pcm_mulaw" => "pcm_mulaw",
                    "alaw" or "audio/alaw" or "pcm_alaw" => "pcm_alaw",
                    _ => throw new NotSupportedException(
                        $"AssemblyAI speech events do not support output format '{outputFormat}'.")
                };
                RemoveAssemblyAIOption(payload, "container");
            }
            else if (format is "wav" or "wave" or "audio/wav")
            {
                SetAssemblyAIOption(payload, "container", "wav");
                RemoveAssemblyAIOption(payload, "encoding");
                encoding = "wav";
            }
            else if (format is "mp3" or "audio/mpeg")
            {
                SetAssemblyAIOption(payload, "encoding", "mp3");
                RemoveAssemblyAIOption(payload, "container");
                encoding = "mp3";
            }
            else
            {
                encoding = format switch
                {
                    "pcm" or "audio/pcm" or "audio/l16" or "pcm_s16le" => "pcm_s16le",
                    "mulaw" or "ulaw" or "audio/basic" or "pcm_mulaw" => "pcm_mulaw",
                    "alaw" or "audio/alaw" or "pcm_alaw" => "pcm_alaw",
                    _ => throw new NotSupportedException(
                        $"AssemblyAI speech does not support output format '{outputFormat}'.")
                };
                SetAssemblyAIOption(payload, "encoding", encoding);
                RemoveAssemblyAIOption(payload, "container");
            }
        }
        else if (streaming)
        {
            if (encoding is not ("pcm_s16le" or "pcm_mulaw" or "pcm_alaw"))
                throw new NotSupportedException($"AssemblyAI speech events do not support encoding '{encoding}'.");
            RemoveAssemblyAIOption(payload, "container");
        }

        var rate = ProviderNumericConversions.ToInt32Exact(sampleRate, "outputFormat.rate")
                   ?? ReadAssemblyAISampleRate(payload)
                   ?? 24000;
        if (!AssemblyAISampleRates.Contains(rate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "AssemblyAI sample rate must be 8000, 16000, 22050, 24000, 44100, or 48000 Hz.");
        SetAssemblyAIOption(payload, "sample_rate", rate);

        var mimeType = encoding switch
        {
            "pcm_s16le" => "audio/pcm",
            "pcm_mulaw" => "audio/basic",
            "pcm_alaw" => "audio/alaw",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            _ => "application/octet-stream"
        };

        return new(payload, encoding, rate, mimeType);
    }

    private static JsonElement MergeAssemblyAISpeechOptions(
        Dictionary<string, JsonElement>? requestOptions,
        Dictionary<string, JsonElement>? partOptions)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var options in new[] { requestOptions, partOptions })
        {
            if (options?.TryGetValue("assemblyai", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.assemblyai must be an object.");
            foreach (var property in native.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(merged);
    }

    private static List<object> CreateAssemblyAISpeechWarnings(string? instructions)
    {
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        return warnings;
    }

    private static string NormalizeAssemblyAIModel(string model)
        => model.StartsWith("assemblyai/", StringComparison.OrdinalIgnoreCase)
            ? model["assemblyai/".Length..]
            : model;

    private static object? ReadAssemblyAIOption(Dictionary<string, object?> payload, string name)
        => payload.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static int? ReadAssemblyAISampleRate(Dictionary<string, object?> payload)
    {
        var value = ReadAssemblyAIOption(payload, "sample_rate");
        return value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var rate) => rate,
            int rate => rate,
            long rate when rate is >= int.MinValue and <= int.MaxValue => (int)rate,
            _ => null
        };
    }

    private static void RemoveAssemblyAIOption(Dictionary<string, object?> payload, string name)
    {
        foreach (var key in payload.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
            payload.Remove(key);
    }

    private static void SetAssemblyAIOption(Dictionary<string, object?> payload, string name, object value)
    {
        RemoveAssemblyAIOption(payload, name);
        payload[name] = value;
    }

    private static string AssemblyAIFormatFromMimeType(string mimeType, string fallback)
        => mimeType.Trim().ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/basic" => "mulaw",
            "audio/alaw" => "alaw",
            "audio/pcm" or "audio/l16" => "pcm",
            _ => fallback
        };

    private sealed record AssemblyAISpeechPlan(
        Dictionary<string, object?> Payload,
        string Encoding,
        int SampleRate,
        string MimeType);
}
