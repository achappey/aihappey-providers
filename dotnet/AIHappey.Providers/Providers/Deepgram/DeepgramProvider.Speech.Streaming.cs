using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> SpeechSocketFactory { get; set; }
        = ConnectSpeechSocket;

    private static async Task<WebSocket> ConnectSpeechSocket(
        Uri uri,
        string key,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Token " + key);
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
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var plan = CreateSpeechStreamPlan(request);
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Deepgram API key.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var socket = await SpeechSocketFactory(plan.Uri, key, linked.Token);
        var events = new List<JsonElement>();
        var receivedAudio = false;
        var flushed = false;
        try
        {
            yield return new SpeechStreamStartPart { Warnings = plan.Warnings };

            await SendSpeechMessage(socket, new { type = "Speak", text = plan.Text }, linked.Token);
            await SendSpeechMessage(socket, new { type = "Flush" }, linked.Token);

            while (true)
            {
                var message = await ReceiveSpeechMessage(socket, linked.Token);
                if (message is null)
                    break;

                if (message.Value.Type == WebSocketMessageType.Binary)
                {
                    if (message.Value.Payload.Length == 0)
                        continue;
                    receivedAudio = true;
                    if (request.IncludeRawChunks == true)
                        yield return new SpeechRawPart
                        {
                            RawValue = Convert.ToBase64String(message.Value.Payload)
                        };
                    yield return new SpeechAudioPart
                    {
                        Audio = Convert.ToBase64String(message.Value.Payload),
                        AudioFormat = new() { Type = plan.MimeType, Rate = plan.SampleRate }
                    };
                    continue;
                }

                using var document = JsonDocument.Parse(message.Value.Payload);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                    throw new JsonException("Deepgram returned a speech event without a string type.");

                var type = typeElement.GetString();
                var rawEvent = root.Clone();
                events.Add(rawEvent);
                if (request.IncludeRawChunks == true)
                    yield return new SpeechRawPart { RawValue = rawEvent };

                if (type == "Error")
                    throw new InvalidOperationException($"Deepgram streaming speech failed: {root.GetRawText()}");
                if (type == "Flushed")
                {
                    flushed = true;
                    break;
                }

                if (type == "Metadata")
                {
                    yield return new SpeechResponseMetadataPart
                    {
                        Timestamp = DateTime.UtcNow,
                        ModelId = request.Model,
                        Body = rawEvent
                    };
                }
                else if (type == "Warning")
                {
                    continue;
                }
                else
                {
                    yield return new SpeechResponseMetadataPart
                    {
                        Timestamp = DateTime.UtcNow,
                        ModelId = request.Model,
                        Body = rawEvent
                    };
                }
            }

            linked.Token.ThrowIfCancellationRequested();
            if (!flushed)
                throw new InvalidOperationException("Deepgram speech stream ended before the Flushed event.");
            if (!receivedAudio)
                throw new InvalidOperationException("Deepgram speech stream flushed without audio data.");

            await SendSpeechMessage(socket, new { type = "Close" }, linked.Token);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Speech synthesis complete", linked.Token);

            yield return new SpeechFinishPart
            {
                ProviderMetadata = new()
                {
                    [GetIdentifier()] = JsonSerializer.SerializeToElement(new { events })
                }
            };
        }
        finally
        {
            linked.Cancel();
            socket.Abort();
        }
    }

    private static DeepgramSpeechStreamPlan CreateSpeechStreamPlan(StreamingSpeechRequest request)
    {
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one Deepgram speech text part is required.", nameof(request));

        var text = new StringBuilder();
        JsonElement? providerOptions = null;
        var optionsInitialized = false;
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("Deepgram speech parts must be text with non-null content.", nameof(request));
            if (!string.IsNullOrWhiteSpace(part.Voice))
                throw new NotSupportedException("Deepgram streaming speech selects the voice through the model ID, not text-part voice overrides.");

            var options = MergeDeepgramSpeechOptions(request.ProviderOptions, part.ProviderOptions);
            if (optionsInitialized
                && ((providerOptions is null) != (options is null)
                    || (providerOptions is { } existingOptions
                        && options is { } currentOptions
                        && !JsonElement.DeepEquals(existingOptions, currentOptions))))
                throw new NotSupportedException(
                    "Deepgram streaming speech requires one provider-options configuration across text parts.");
            providerOptions = options;
            optionsInitialized = true;
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Deepgram speech text cannot be empty.", nameof(request));
        var warnings = new List<object>();
        if (request.Voice is not null)
            warnings.Add(new { type = "unsupported", feature = "voice" });

        var metadata = providerOptions.GetValueOrDefault();
        if (providerOptions.HasValue && metadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.deepgram must be an object.", nameof(request));

        var format = GetDeepgramSpeechOptionString(metadata, "encoding")
                     ?? MapDeepgramStreamingEncoding(request.OutputFormat?.Type);
        var encoding = NormalizeDeepgramStreamingEncoding(format);
        var sampleRate = GetDeepgramSpeechOptionInt(metadata, "sample_rate")
                         ?? (request.OutputFormat?.Rate is { } outputRate
                             ? ProviderNumericConversions.ToInt32Exact(outputRate, "outputFormat.rate")
                             : null);
        if (sampleRate is { } rate && rate is not (8000 or 16000 or 24000 or 32000 or 48000))
            throw new ArgumentOutOfRangeException(nameof(request.OutputFormat), "Deepgram streaming sample rate must be one of 8000, 16000, 24000, 32000, or 48000 Hz.");

        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(request.Model)}",
            $"encoding={Uri.EscapeDataString(encoding)}"
        };
        if (sampleRate is { } configuredRate)
            query.Add($"sample_rate={configuredRate.ToString(CultureInfo.InvariantCulture)}");

        var speed = request.Speed ?? GetDeepgramSpeechOptionDouble(metadata, "speed");
        if (speed is { } configuredSpeed)
        {
            if (!double.IsFinite(configuredSpeed) || configuredSpeed is < 0.7 or > 1.5)
                throw new ArgumentOutOfRangeException(nameof(request.Speed), "Deepgram streaming speech speed must be between 0.7 and 1.5.");
            if (request.Model.StartsWith("aura-2-", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(configuredSpeed * 20 - Math.Round(configuredSpeed * 20)) > 0.0001)
                throw new ArgumentOutOfRangeException(nameof(request.Speed), "Deepgram Aura-2 speed must use increments of 0.05.");
            query.Add($"speed={configuredSpeed.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (TryGetDeepgramSpeechProperty(metadata, "mip_opt_out", out var mipOptOut))
            AddDeepgramSpeechQuery(query, "mip_opt_out", mipOptOut);
        if (TryGetDeepgramSpeechProperty(metadata, "tag", out var tags))
            AddDeepgramSpeechQuery(query, "tag", tags);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model", "encoding", "sample_rate", "speed", "mip_opt_out", "tag"
        };
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!known.Contains(property.Name))
                    AddDeepgramSpeechQuery(query, property.Name, property.Value);
            }
        }

        var sampleRateForAudio = sampleRate ?? (encoding == "linear16" ? 24000 : 8000);
        var mimeType = encoding switch
        {
            "linear16" => "audio/L16",
            "mulaw" => "audio/basic",
            "alaw" => "audio/alaw",
            _ => throw new InvalidOperationException("Unsupported Deepgram streaming encoding.")
        };

        return new DeepgramSpeechStreamPlan(
            new Uri("wss://api.deepgram.com/v1/speak?" + string.Join("&", query)),
            text.ToString(),
            encoding,
            sampleRateForAudio,
            mimeType,
            warnings);
    }

    private static JsonElement? MergeDeepgramSpeechOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue("deepgram", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.deepgram must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }

        return values.Count == 0 ? null : JsonSerializer.SerializeToElement(values);
    }

    private static string? GetDeepgramSpeechOptionString(JsonElement metadata, string name)
    {
        if (!TryGetDeepgramSpeechProperty(metadata, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Deepgram speech option '{name}' must be a string.");
        return value.GetString();
    }

    private static int? GetDeepgramSpeechOptionInt(JsonElement metadata, string name)
    {
        if (!TryGetDeepgramSpeechProperty(metadata, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new JsonException($"Deepgram speech option '{name}' must be an integer.");
        return number;
    }

    private static double? GetDeepgramSpeechOptionDouble(JsonElement metadata, string name)
    {
        if (!TryGetDeepgramSpeechProperty(metadata, name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
            throw new JsonException($"Deepgram speech option '{name}' must be a number.");
        return number;
    }

    private static bool TryGetDeepgramSpeechProperty(JsonElement metadata, string name, out JsonElement value)
    {
        value = default;
        if (metadata.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }

    private static string MapDeepgramStreamingEncoding(string? format)
        => (format ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "pcm" or "linear16" or "audio/pcm" or "audio/l16" => "linear16",
            "mulaw" or "ulaw" or "audio/basic" => "mulaw",
            "alaw" or "audio/alaw" => "alaw",
            _ => throw new NotSupportedException(
                $"Deepgram WebSocket speech does not support output format '{format}'. Use linear16/pcm, mulaw, or alaw.")
        };

    private static string NormalizeDeepgramStreamingEncoding(string encoding)
        => encoding.Trim().ToLowerInvariant() switch
        {
            "linear16" or "pcm" or "audio/pcm" or "audio/l16" => "linear16",
            "mulaw" or "ulaw" or "audio/basic" => "mulaw",
            "alaw" or "audio/alaw" => "alaw",
            _ => throw new NotSupportedException(
                $"Deepgram WebSocket speech does not support encoding '{encoding}'.")
        };

    private static void AddDeepgramSpeechQuery(List<string> query, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AddDeepgramSpeechQueryValue(query, name, item);
            return;
        }
        AddDeepgramSpeechQueryValue(query, name, value);
    }

    private static void AddDeepgramSpeechQueryValue(List<string> query, string name, JsonElement value)
    {
        var serialized = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(serialized)}");
    }

    private static async Task SendSpeechMessage(WebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<DeepgramSpeechSocketMessage?> ReceiveSpeechMessage(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketMessageType? type = null;
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (message.Length != 0)
                    throw new InvalidOperationException("Deepgram closed within a fragmented speech message.");
                return null;
            }
            if (type is not null && type != result.MessageType)
                throw new InvalidOperationException("Deepgram changed message type within a fragmented speech message.");
            type = result.MessageType;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("Deepgram speech message exceeded the 8 MiB limit.");
            if (result.EndOfMessage)
                return new DeepgramSpeechSocketMessage(type.Value, message.ToArray());
        }
    }

    private sealed record DeepgramSpeechStreamPlan(
        Uri Uri,
        string Text,
        string Encoding,
        int SampleRate,
        string MimeType,
        List<object> Warnings);

    private sealed record DeepgramSpeechSocketMessage(WebSocketMessageType Type, byte[] Payload);
}
