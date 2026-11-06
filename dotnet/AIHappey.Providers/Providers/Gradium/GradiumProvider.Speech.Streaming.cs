using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Gradium;

public partial class GradiumProvider
{
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> SpeechSocketFactory { get; set; }
        = ConnectSpeechSocket;

    private static async Task<WebSocket> ConnectSpeechSocket(
        Uri uri,
        string key,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("x-api-key", key);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async IAsyncEnumerable<StreamingSpeechPart> StreamSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var plan = CreateSpeechStreamPlan(request);
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var uri = new UriBuilder(_client.BaseAddress!)
        {
            Scheme = "wss",
            Path = "/api/speech/tts",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
        using var socket = await SpeechSocketFactory(uri, key, linked.Token);
        var events = new List<JsonElement>();
        var receivedAudio = false;

        try
        {
            yield return new SpeechStreamStartPart { Warnings = plan.Warnings };
            await SendSpeechMessage(socket, plan.Setup, linked.Token);

            var ready = await ReceiveSpeechMessage(socket, linked.Token);
            if (ready is null)
                throw new InvalidOperationException("Gradium closed the speech stream before its ready event.");

            using (var document = ParseSpeechStreamJson(ready))
            {
                var root = document.RootElement;
                var eventType = ReadEventType(root);
                events.Add(root.Clone());
                if (request.IncludeRawChunks == true)
                    yield return new SpeechRawPart { RawValue = root.Clone() };
                if (eventType == "error")
                    throw new InvalidOperationException($"Gradium streaming TTS failed: {ReadSpeechStreamError(root)}");
                if (eventType != "ready")
                    throw new InvalidOperationException($"Gradium speech stream expected a ready event, received '{eventType}'.");

                yield return new SpeechResponseMetadataPart
                {
                    Timestamp = DateTime.UtcNow,
                    ModelId = request.Model,
                    Body = root.Clone()
                };
            }

            await SendSpeechMessage(socket, new { type = "text", text = plan.Text }, linked.Token);
            await SendSpeechMessage(socket, new { type = "end_of_stream" }, linked.Token);

            while (true)
            {
                var message = await ReceiveSpeechMessage(socket, linked.Token);
                if (message is null)
                    throw new InvalidOperationException("Gradium speech stream ended before end_of_stream.");

                using var document = ParseSpeechStreamJson(message);
                var root = document.RootElement;
                var eventType = ReadEventType(root);
                var rawEvent = root.Clone();
                events.Add(rawEvent);
                if (request.IncludeRawChunks == true)
                    yield return new SpeechRawPart { RawValue = rawEvent };

                switch (eventType)
                {
                    case "audio":
                        if (!root.TryGetProperty("audio", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                            throw new JsonException("Gradium speech audio event is missing its base64 audio field.");
                        byte[] audio;
                        try
                        {
                            audio = Convert.FromBase64String(encoded.GetString()!);
                        }
                        catch (FormatException ex)
                        {
                            throw new JsonException("Gradium speech event contains invalid base64 audio.", ex);
                        }
                        if (audio.Length > 0)
                        {
                            receivedAudio = true;
                            yield return new SpeechAudioPart
                            {
                                Audio = Convert.ToBase64String(audio),
                                AudioFormat = new AudioFormat
                                {
                                    Type = plan.MimeType,
                                    Rate = plan.SampleRate
                                },
                                ProviderMetadata = new() { [GetIdentifier()] = rawEvent }
                            };
                        }
                        break;

                    case "text":
                    case "flushed":
                        yield return new SpeechResponseMetadataPart
                        {
                            Timestamp = DateTime.UtcNow,
                            ModelId = request.Model,
                            Body = rawEvent
                        };
                        break;

                    case "end_of_stream":
                        if (!receivedAudio)
                            throw new InvalidOperationException("Gradium speech stream completed without audio data.");
                        await CloseSpeechSocket(socket, linked.Token);
                        yield return new SpeechFinishPart
                        {
                            ProviderMetadata = new()
                            {
                                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { events })
                            }
                        };
                        yield break;

                    case "error":
                        throw new InvalidOperationException($"Gradium streaming TTS failed: {ReadSpeechStreamError(root)}");

                    case "ready":
                        break;

                    default:
                        throw new InvalidOperationException($"Unsupported Gradium speech stream event type '{eventType}'.");
                }
            }
        }
        finally
        {
            linked.Cancel();
            socket.Abort();
        }
    }

    private static GradiumSpeechStreamPlan CreateSpeechStreamPlan(StreamingSpeechRequest request)
    {
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one Gradium speech text part is required.", nameof(request));

        var (baseModelName, modelVoiceId) = ParseModelAndVoice(request.Model);
        var text = new StringBuilder();
        JsonElement? effectiveSetup = null;
        string? effectiveFormat = null;
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("Gradium speech parts must be text with non-null content.", nameof(request));

            var metadata = MergeSpeechOptions(request.ProviderOptions, part.ProviderOptions);
            var metadataElement = JsonSerializer.SerializeToElement(metadata);
            var optionVoice = ReadOptionalString(metadataElement, "voice_id");
            var optionModel = ReadOptionalString(metadataElement, "model_name");
            var optionFormat = ReadOptionalString(metadataElement, "output_format");
            var jsonConfig = GetOptionalValue(metadataElement, "json_config");

            var voice = (modelVoiceId ?? part.Voice ?? request.Voice ?? optionVoice)?.Trim();
            if (string.IsNullOrWhiteSpace(voice))
                throw new ArgumentException("Gradium requires a voice_id in the model id, request voice, or providerOptions.gradium.voice_id.", nameof(request));
            var modelName = string.IsNullOrWhiteSpace(optionModel) ? baseModelName : optionModel.Trim();
            var outputFormat = NormalizeOutputFormat(request.OutputFormat?.Type ?? optionFormat) ?? "wav";
            outputFormat = NormalizeStreamingOutputFormat(
                outputFormat,
                ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate"));

            var setup = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (name, value) in metadata)
            {
                if (!IsMappedMetadataProperty(name))
                    setup[name] = value.Clone();
            }
            setup["type"] = JsonSerializer.SerializeToElement("setup");
            setup["voice_id"] = JsonSerializer.SerializeToElement(voice);
            setup["model_name"] = JsonSerializer.SerializeToElement(modelName);
            setup["output_format"] = JsonSerializer.SerializeToElement(outputFormat);
            if (jsonConfig is { } config)
                setup["json_config"] = config.Clone();

            var currentSetup = JsonSerializer.SerializeToElement(setup);
            if (effectiveSetup is not null && !JsonElement.DeepEquals(effectiveSetup.Value, currentSetup))
                throw new NotSupportedException("Gradium speech streaming requires one effective voice and setup configuration across text parts.");

            effectiveSetup = currentSetup;
            effectiveFormat = outputFormat;
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Gradium speech text cannot be empty.", nameof(request));

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is voice-dependent in Gradium" });

        var sampleRate = GetSampleRate(effectiveFormat!);
        return new GradiumSpeechStreamPlan(
            effectiveSetup!.Value,
            text.ToString(),
            effectiveFormat!,
            GetMimeType(effectiveFormat!),
            sampleRate,
            warnings);
    }

    private static Dictionary<string, JsonElement> MergeSpeechOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            var key = options?.Keys.FirstOrDefault(name => string.Equals(name, ProviderId, StringComparison.OrdinalIgnoreCase));
            if (key is null)
                continue;
            var native = options![key];
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.gradium must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }

    private static JsonElement? GetOptionalValue(JsonElement metadata, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(metadata, propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value;
    }

    private static string? ReadOptionalString(JsonElement metadata, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(metadata, propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Gradium speech option '{propertyName}' must be a string.");
        return value.GetString();
    }

    private static string NormalizeStreamingOutputFormat(string format, int? requestedRate)
    {
        var value = format.Trim().ToLowerInvariant() switch
        {
            "wave" or "audio/wav" or "audio/x-wav" => "wav",
            "ogg" or "audio/ogg" or "audio/opus" => "opus",
            "audio/pcm" or "audio/l16" => "pcm",
            "mulaw_8000" => "ulaw_8000",
            var normalized => normalized
        };
        if (value == "pcm" && requestedRate is { } rate)
        {
            if (rate is not (8000 or 16000 or 22050 or 24000 or 44100 or 48000))
                throw new ArgumentOutOfRangeException(nameof(requestedRate), "Gradium PCM output rate must be one of 8000, 16000, 22050, 24000, 44100, or 48000 Hz.");
            return $"pcm_{rate.ToString(CultureInfo.InvariantCulture)}";
        }
        if (value is not ("wav" or "pcm" or "pcm_8000" or "pcm_16000" or "pcm_22050" or "pcm_24000" or "pcm_44100" or "pcm_48000" or "opus" or "ulaw_8000" or "alaw_8000"))
            throw new NotSupportedException($"Gradium does not document streaming output format '{format}'.");
        if (requestedRate is { } explicitRate && GetSampleRate(value) is { } formatRate && explicitRate != formatRate)
            throw new NotSupportedException("Gradium outputFormat.rate must match the selected native output format.");
        return value;
    }

    private static int? GetSampleRate(string format)
    {
        if (format.StartsWith("pcm_", StringComparison.Ordinal))
            return int.Parse(format["pcm_".Length..], CultureInfo.InvariantCulture);
        return format switch
        {
            "wav" or "pcm" or "opus" => 48000,
            "ulaw_8000" or "alaw_8000" => 8000,
            _ => null
        };
    }

    private static string GetMimeType(string format)
        => format switch
        {
            "wav" => "audio/wav",
            "opus" => "audio/ogg",
            "ulaw_8000" => "audio/basic",
            "alaw_8000" => "audio/alaw",
            _ => "audio/pcm"
        };

    private static async Task SendSpeechMessage(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, SpeechJsonOptions);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveSpeechMessage(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (message.Length != 0)
                    throw new InvalidOperationException("Gradium closed within a fragmented speech event.");
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Gradium speech stream returned a non-text event.");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("Gradium speech event exceeded the 8 MiB limit.");
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    private static JsonDocument ParseSpeechStreamJson(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Gradium returned invalid speech stream JSON.", ex);
        }
    }

    private static string ReadEventType(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            throw new JsonException("Gradium speech event did not include a string type.");
        return type.GetString()!;
    }

    private static string ReadSpeechStreamError(JsonElement root)
        => root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? root.GetRawText()
            : root.GetRawText();

    private static async Task CloseSpeechSocket(WebSocket socket, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Speech synthesis complete", cancellationToken);
    }

    private sealed record GradiumSpeechStreamPlan(
        JsonElement Setup,
        string Text,
        string OutputFormat,
        string MimeType,
        int? SampleRate,
        List<object> Warnings);
}
