using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferenceClub;

public partial class InferenceClubProvider
{
    private async Task<(byte[] Audio, string MimeType, JsonElement Metadata)> GenerateAudioAsync(
        Dictionary<string, object?> payload, string model, string text, string? format, CancellationToken cancellationToken)
    {
        var descriptor = (await ListModels(cancellationToken))
            .FirstOrDefault(m => m.Id == model.ToModelId(GetIdentifier()))
            ?? throw new ArgumentException($"InferenceClub model '{model}' is not currently available.", nameof(model));
        var music = descriptor.Tags?.Contains("music") == true;
        var voice = descriptor.Tags?.Contains("voice-cloning") == true;
        if (descriptor.Type != "speech")
            throw new ArgumentException($"InferenceClub model '{model}' does not generate audio.", nameof(model));
        payload["model"] = model;
        payload[music ? "prompt" : "input"] = text;
        Set(payload, music ? "audio_format" : "response_format", format);
        using var request = CreateRequest(HttpMethod.Post,
            music ? "music/generations" : voice ? "voice/generations" : "audio/speech",
            JsonContent.Create(payload, options: JsonOptions));
        using var response = await _client.SendAsync(request, cancellationToken);
        var type = response.Content.Headers.ContentType?.MediaType;
        if (response.IsSuccessStatusCode && type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                throw new InvalidOperationException("InferenceClub returned empty audio.");
            return (bytes, type, JsonSerializer.SerializeToElement(response.GetHeaders()));
        }
        var result = await WaitForResultAsync(await ReadJsonAsync(response, cancellationToken), cancellationToken);
        var output = Outputs(result).FirstOrDefault();
        var url = ReadString(output, "url")
            ?? throw new InvalidOperationException($"InferenceClub returned no audio: {result}");
        var downloaded = await DownloadAsync(url, cancellationToken);
        return (downloaded.Bytes, downloaded.MediaType, result);
    }

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var payload = ProviderOptions(request.ProviderOptions);
        Set(payload, "voice", request.Voice);
        Set(payload, "language", request.Language);
        Set(payload, "instructions", request.Instructions);
        Set(payload, "speed", request.Speed);
        var (audio, type, metadata) = await GenerateAudioAsync(
            payload, request.Model, request.Text, request.OutputFormat, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = type,
                Format = type switch { "audio/mpeg" => "mp3", "audio/ogg" => "opus", _ => type.Split('/').Last() }
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(metadata),
            Response = new ResponseData { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Body = metadata }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        var payload = Object(JsonSerializer.SerializeToElement(options, JsonOptions));
        payload.Remove("stream_format");
        var (audio, type, _) = await GenerateAudioAsync(payload, options.Model, options.Input, options.ResponseFormat, cancellationToken);
        return (audio, type);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    private async Task<JsonElement> TranscribeAsync(MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, "audio/transcriptions", form);
        using var response = await _client.SendAsync(message, cancellationToken);
        var result = await ReadJsonAsync(response, cancellationToken);
        if (ReadString(result, "text") is null)
            throw new InvalidOperationException($"InferenceClub transcription has no text: {result}");
        return result;
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var payload = ProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload.TryAdd("response_format", "json");
        AddFields(form, JsonSerializer.SerializeToElement(payload, JsonOptions));
        var (bytes, type) = await ReadFileAsync(request.Audio.ToString(), request.MediaType, cancellationToken);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        form.Add(file, "file", "audio." + (type == "audio/mpeg" ? "mp3" : type.Split('/').Last()));
        var result = await TranscribeAsync(form, cancellationToken);
        return new TranscriptionResponse
        {
            Text = result.GetProperty("text").GetString()!,
            Language = ReadString(result, "language"),
            DurationInSeconds = result.TryGetProperty("duration", out var duration) ? duration.GetDouble()
                : result.TryGetProperty("usage", out var usage) && usage.TryGetProperty("seconds", out var seconds) ? seconds.GetDouble() : null,
            Segments = result.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array
                ? segments.EnumerateArray().Select(s => new TranscriptionSegment
                {
                    Text = s.GetProperty("text").GetString()!,
                    StartSecond = s.GetProperty("start").GetDouble(),
                    EndSecond = s.GetProperty("end").GetDouble()
                }).ToArray() : [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = new ResponseData { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Body = result }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        if (options.ResponseFormat is not (null or "json" or "verbose_json"))
            throw new NotSupportedException("InferenceClub transcription supports json and verbose_json only.");
        using var form = new MultipartFormDataContent();
        AddFields(form, JsonSerializer.SerializeToElement(options, JsonOptions), "stream");
        await AddUploadAsync(form, options.File, "file", cancellationToken);
        var result = await TranscribeAsync(form, cancellationToken);
        // The network downgrades verbose_json for models without timestamp support.
        return result.TryGetProperty("duration", out _) && result.TryGetProperty("language", out _)
            ? result.Deserialize<OpenAITranscriptionVerboseResponse>(JsonOptions)!
            : result.Deserialize<OpenAITranscriptionResponse>(JsonOptions)!;
    }
}
