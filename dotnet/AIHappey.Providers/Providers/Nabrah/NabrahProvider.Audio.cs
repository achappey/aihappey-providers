using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Nabrah;

public partial class NabrahProvider
{
    private static string LocalModel(string? model)
        => model?.StartsWith("nabrah/", StringComparison.OrdinalIgnoreCase) == true ? model[7..] : model ?? "";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Nabrah API key.");
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-API-Key", key);
        return request;
    }

    private static JsonObject Options(JsonElement options)
    {
        if (options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new JsonObject();
        if (options.ValueKind != JsonValueKind.Object)
            throw new JsonException("Nabrah provider options must be a JSON object.");
        var result = JsonNode.Parse(options.GetRawText())!.AsObject();
        if (result.Remove("nabrah", out var scoped))
            foreach (var item in Options(JsonSerializer.SerializeToElement(scoped)))
                result[item.Key] = item.Value?.DeepClone();
        return result;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Nabrah request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
    }

    private static JsonObject SpeechPayload(SpeechRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var payload = Options(request.GetProviderMetadata<JsonElement>("nabrah"));
        payload["model"] = string.IsNullOrWhiteSpace(request.Model) ? "nabrah_tts_v1" : LocalModel(request.Model);
        payload["input"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["response_format"] = request.OutputFormat;
        if (request.Speed.HasValue)
            payload["speed"] = request.Speed.Value;
        payload["stream"] = stream;
        return payload;
    }

    private static SpeechRequest ToNativeSpeechRequest(AudioSpeechRequest options)
    {
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is { Count: > 0 } properties)
        {
            var native = properties.TryGetValue("nabrah", out var scoped)
                ? Options(scoped)
                : Options(JsonSerializer.SerializeToElement(properties));
            request.ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["nabrah"] = JsonSerializer.SerializeToElement(native)
            };
        }
        return request;
    }

    private async Task<SpeechResponse> SpeechRequestInternal(SpeechRequest request, CancellationToken ct)
    {
        var payload = SpeechPayload(request, false);
        using var http = CreateRequest(HttpMethod.Post, "api/ext/tts/generations",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new InvalidOperationException("Nabrah returned empty audio.");
        var format = payload["response_format"]?.GetValue<string>() ?? "mp3";
        var mime = format.ToLowerInvariant() switch { "wav" => "audio/wav", "pcm" => "audio/pcm", "mp3" => "audio/mpeg", _ => response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream" };
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Language)) warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions)) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed.HasValue) warnings.Add(new { type = "unsupported", feature = "speed" });
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(bytes), Format = format, MimeType = mime },
            Warnings = warnings,
            Request = new() { Body = payload },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = $"nabrah/{payload["model"]}" }
        };
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = SpeechPayload(ToNativeSpeechRequest(options), true);
        if (payload["response_format"] is null) payload["response_format"] = "mp3";
        using var http = CreateRequest(HttpMethod.Post, "api/ext/tts/generations",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16384];
        int count;
        var receivedAudio = false;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            receivedAudio = true;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, count) };
        }
        if (!receivedAudio) throw new InvalidOperationException("Nabrah returned empty audio.");
        yield return new AudioSpeechStreamDone();
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.Audio is JsonElement value && value.ValueKind == JsonValueKind.String ? value.GetString() : request.Audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0) throw new ArgumentException("Invalid audio data URL.", nameof(request));
            audio = audio[(comma + 1)..];
        }
        return await Transcribe(Convert.FromBase64String(audio), request.MediaType, "audio" + Extension(request.MediaType),
            Options(request.ProviderOptions?.GetValueOrDefault("nabrah") ?? default), request.Model, cancellationToken);
    }

    private async Task<TranscriptionResponse> Transcribe(byte[] audio, string mediaType, string fileName, JsonObject options, string? model, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "audio_file", fileName);
        foreach (var option in options)
        {
            if (option.Key == "audio_file") continue;
            if (option.Value is JsonArray array)
            {
                foreach (var item in array)
                    form.Add(new StringContent(FormValue(item)), option.Key);
            }
            else
                form.Add(new StringContent(FormValue(option.Value)), option.Key);
        }

        using var upload = CreateRequest(HttpMethod.Post, "api/ext/stt/async-transcribe", form);
        using var created = await _client.SendAsync(upload, ct);
        await EnsureSuccess(created, ct);
        var job = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync(ct));
        var id = StringProperty(job, "id") ?? throw new JsonException("Nabrah transcription response did not contain an id.");
        var deadline = DateTime.UtcNow.AddHours(3);
        while (true)
        {
            var status = StringProperty(job, "status") ?? throw new JsonException("Nabrah transcription job did not contain a status.");
            if (status == "completed") break;
            if (status is "failed" or "canceled")
                throw new InvalidOperationException($"Nabrah transcription job {id} {status}: {job.GetRawText()}");
            if (status is not ("queued" or "in_progress"))
                throw new InvalidOperationException($"Unknown Nabrah transcription status: {status}");
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Nabrah transcription job {id} timed out.");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var poll = CreateRequest(HttpMethod.Get, $"api/ext/stt/jobs/{Uri.EscapeDataString(id)}");
            using var response = await _client.SendAsync(poll, ct);
            await EnsureSuccess(response, ct);
            job = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        }
        if (!job.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new JsonException("Nabrah completed transcription has no result.");
        var text = StringProperty(result, "text_result") ?? throw new JsonException("Nabrah transcription result has no text_result.");
        double? duration = result.TryGetProperty("audio_duration", out var durationValue)
            && durationValue.ValueKind == JsonValueKind.Number && durationValue.TryGetDouble(out var seconds) ? seconds : null;
        return new TranscriptionResponse
        {
            Text = text,
            DurationInSeconds = duration,
            ProviderMetadata = new() { ["nabrah"] = job },
            Request = new() { Body = options.ToJsonString() },
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = $"nabrah/{LocalModel(model)}", Body = job }
        };
    }

    private static string FormValue(JsonNode? value)
        => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "";

    private static string? StringProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Extension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" => ".wav", "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg", "audio/webm" => ".webm", "audio/flac" => ".flac",
        "audio/mp4" => ".m4a", _ => ".bin"
    };

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await options.File.CopyToAsync(buffer, cancellationToken);
        var native = options.AdditionalProperties?.TryGetValue("nabrah", out var scoped) == true
            ? Options(scoped) : Options(JsonSerializer.SerializeToElement(options.AdditionalProperties));
        var response = await Transcribe(buffer.ToArray(), options.File.ContentType, options.File.FileName, native, options.Model, cancellationToken);
        return options.ResponseFormat == "verbose_json"
            ? new OpenAITranscriptionVerboseResponse { Text = response.Text, Language = response.Language ?? "", Duration = response.DurationInSeconds ?? 0 }
            : new OpenAITranscriptionResponse { Text = response.Text };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrEmpty(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }
}
