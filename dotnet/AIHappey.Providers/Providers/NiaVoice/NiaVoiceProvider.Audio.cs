using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NiaVoice;

public sealed partial class NiaVoiceProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var payload = Options(request.ProviderOptions);
        payload["text"] = request.Text;
        if (request.Language is not null) payload["language"] = request.Language;
        if (request.Voice is not null) payload["voice"] = request.Voice;
        if (request.Speed is not null) payload["speed"] = request.Speed;
        if (request.OutputFormat is not null) payload["format"] = request.OutputFormat;
        var model = LocalModel(request.Model);
        if (model.StartsWith("speech/", StringComparison.Ordinal))
        {
            var parts = model.Split('/', 3);
            if (!payload.ContainsKey("language")) payload["language"] = parts[1];
            if (parts.Length == 3 && !payload.ContainsKey("voice")) payload["voice"] = parts[2];
        }
        var format = payload["format"]?.GetValue<string>() ?? "wav";
        if (format is not ("wav" or "mp3"))
            throw new NotSupportedException("NiaVoice speech supports wav and mp3.");
        using var message = CreateRequest(HttpMethod.Post, "v1/speech", JsonContent(payload));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0)
            throw new InvalidOperationException("NiaVoice returned empty speech audio.");
        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audio), Format = format,
                MimeType = response.Content.Headers.ContentType?.MediaType ?? (format == "mp3" ? "audio/mpeg" : "audio/wav")
            },
            Warnings = request.Instructions is null ? [] : [new { type = "unsupported", feature = "instructions" }],
            ProviderMetadata = new() { [ProviderId] = JsonSerializer.SerializeToElement(response.GetHeaders()) },
            Request = new() { Body = payload },
            Response = new()
            {
                Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{model}", Headers = response.GetHeaders()
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        var speech = await SpeechRequest(new SpeechRequest
        {
            Model = options.Model, Text = options.Input, Voice = options.Voice, Speed = options.Speed,
            OutputFormat = options.ResponseFormat, Instructions = options.Instructions,
            ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(NativeOptions(options.AdditionalProperties)) }
        }, cancellationToken);
        return (Convert.FromBase64String(speech.Audio.Base64), speech.Audio.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var speech = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(speech.Audio) };
        yield return new AudioSpeechStreamDone();
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var audio = request.Audio is JsonElement value && value.ValueKind == JsonValueKind.String
            ? value.GetString() : request.Audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0) throw new ArgumentException("Invalid audio data URL.", nameof(request));
            audio = audio[(comma + 1)..];
        }
        var extension = request.MediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/webm" or "video/webm" => ".webm",
            "video/mp4" => ".mp4",
            _ => throw new NotSupportedException($"NiaVoice transcription does not document {request.MediaType}.")
        };
        return await Transcribe(Convert.FromBase64String(audio), request.MediaType, "audio" + extension,
            Options(request.ProviderOptions), request.Model, cancellationToken);
    }

    private async Task<TranscriptionResponse> Transcribe(byte[] audio, string mediaType, string fileName,
        JsonObject options, string model, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);
        foreach (var option in options)
        {
            if (option.Key == "file")
                throw new ArgumentException("NiaVoice file must be supplied as audio, not provider options.");
            var text = option.Value is JsonValue scalar && scalar.TryGetValue<string>(out var stringValue)
                ? stringValue : option.Value?.ToJsonString() ?? "null";
            form.Add(new StringContent(text), option.Key);
        }
        using var message = CreateRequest(HttpMethod.Post, "v1/transcribe", form);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var raw = document.RootElement.Clone();
        return new TranscriptionResponse
        {
            Text = RequiredString(raw, "text"),
            Language = raw.TryGetProperty("language", out var language) ? language.GetString() : null,
            DurationInSeconds = raw.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number
                ? duration.GetDouble() / 1000 : null,
            ProviderMetadata = new() { [ProviderId] = raw },
            Request = new() { Body = "multipart/form-data" },
            Response = new()
            {
                Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{LocalModel(model)}",
                Headers = response.GetHeaders(), Body = raw
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        if (options.ResponseFormat is not (null or "json" or "verbose_json"))
            throw new NotSupportedException("NiaVoice transcription supports json and verbose_json adapters.");
        var native = NativeOptions(options.AdditionalProperties);
        if (options.Language is not null) native["language"] = options.Language;
        using var buffer = new MemoryStream();
        await options.File.CopyToAsync(buffer, cancellationToken);
        var response = await Transcribe(buffer.ToArray(), options.File.ContentType, options.File.FileName,
            native, options.Model, cancellationToken);
        return options.ResponseFormat == "verbose_json"
            ? new OpenAITranscriptionVerboseResponse
            {
                Text = response.Text, Language = response.Language ?? "",
                Duration = response.DurationInSeconds ?? 0
            }
            : new OpenAITranscriptionResponse { Text = response.Text };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }
}
