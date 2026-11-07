using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Astica;

public partial class AsticaProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var apiKey = ResolveRequiredApiKey();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var voiceId = ParseVoiceIdFromModel(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is voice-dependent in Astica" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(NormalizeFormat(request.OutputFormat), "wav", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "outputFormat", reason = "astica non-streaming endpoint returns WAV in audio_b64" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        EnsureProviderMetadataObject(metadata);

        var payload = BuildSpeechPayload(apiKey, request.Text, voiceId, metadata, stream: false);

        var raw = await PostJsonAndReadAsync("api/tts", payload, cancellationToken);
        using var doc = JsonDocument.Parse(raw);

        var status = ReadString(doc.RootElement, "status");
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} TTS failed: {raw}");

        var audioBase64 = ReadString(doc.RootElement, "audio_b64");
        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"{ProviderName} TTS response did not include audio_b64: {raw}");

        var responseVoice = ReadString(doc.RootElement, "voice") ?? voiceId;
        var engine = ReadString(doc.RootElement, "engine");
        var audioFormat = NormalizeFormat(ReadString(doc.RootElement, "audio_format")) ?? "wav";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = audioBase64,
                MimeType = "audio/wav",
                Format = audioFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = doc.RootElement.Clone()
            }
        };
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("Text is required.", nameof(request));

        var apiKey = ResolveRequiredApiKey();
        var voiceId = ParseVoiceIdFromModel(request.Model);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        EnsureProviderMetadataObject(metadata);

        var textBuilder = new StringBuilder();
        var warnings = new List<object>();
        var voiceMismatch = !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase);
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("Astica speech parts must be text with non-null content.", nameof(request));

            textBuilder.Append(part.Text);
            if (!string.IsNullOrWhiteSpace(part.Voice)
                && !string.Equals(part.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
                voiceMismatch = true;
        }

        var text = textBuilder.ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(request));

        if (voiceMismatch)
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is voice-dependent in Astica" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (request.OutputFormat is not null)
            warnings.Add(new { type = "ignored", feature = "outputFormat", reason = "Astica streaming audio format depends on the voice engine." });

        var payload = BuildSpeechPayload(apiKey, text, voiceId, metadata, stream: true);
        using var req = CreateSpeechRequest("api/tts", payload, streaming: true);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} streaming TTS failed ({(int)resp.StatusCode}): {error}");
        }

        var audioFormat = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(audioFormat))
            throw new InvalidOperationException($"{ProviderName} streaming TTS response did not include an audio content type.");
        if (!audioFormat.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(audioFormat, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} streaming TTS returned unexpected content type '{audioFormat}'.");

        yield return new SpeechStreamStartPart { Warnings = warnings };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier()),
            Headers = resp.GetHeaders()
        };

        var receivedAudio = false;
        var buffer = new byte[16 * 1024];
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            var chunk = Convert.ToBase64String(buffer, 0, read);
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = chunk };

            yield return new SpeechAudioPart
            {
                Audio = chunk,
                AudioFormat = new() { Type = audioFormat }
            };
        }

        if (!receivedAudio)
            throw new InvalidOperationException($"{ProviderName} streaming TTS returned no audio.");

        cancellationToken.ThrowIfCancellationRequested();
        yield return new SpeechFinishPart();
    }

    private static string ParseVoiceIdFromModel(string model)
    {
        var trimmed = model.Trim();

        if (trimmed.StartsWith($"{ProviderId}/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[(ProviderId.Length + 1)..];

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException($"Model must be '{ProviderId}/[voiceId]'.", nameof(model));

        return trimmed;
    }

    private async Task<string> PostJsonAndReadAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var req = CreateSpeechRequest(path, payload);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} request '{path}' failed ({(int)resp.StatusCode}): {body}");

        return body;
    }

    private static HttpRequestMessage CreateSpeechRequest(string path, object payload, bool streaming = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        request.Headers.Accept.ParseAdd(streaming ? "*/*" : MediaTypeNames.Application.Json);
        return request;
    }

    private static Dictionary<string, object?> BuildSpeechPayload(
        string apiKey,
        string text,
        string voiceId,
        JsonElement metadata,
        bool stream)
    {
        var payload = new Dictionary<string, object?>();
        CopyAdditionalSpeechOptions(metadata, payload);
        payload["tkn"] = apiKey;
        payload["text"] = text;
        payload["voice"] = voiceId;
        payload["stream"] = stream;

        if (ReadMetadataBoolean(metadata, "timestamps") is { } timestamps)
            payload["timestamps"] = timestamps;

        var prompt = ReadMetadataString(metadata, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            payload["prompt"] = prompt.Trim();

        return payload;
    }

    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "wave" => "wav",
            "mpeg" => "mp3",
            _ => value
        };
    }

    private static void CopyAdditionalSpeechOptions(JsonElement metadata, Dictionary<string, object?> payload)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals("timestamps", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("tkn", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("text", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("voice", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("stream", StringComparison.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static bool? ReadMetadataBoolean(JsonElement metadata, string name)
    {
        var value = FindOption(metadata, name);
        return value is null ? null : value.Value.Deserialize<bool?>(JsonSerializerOptions.Web);
    }
}
