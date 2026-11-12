using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SpeakAI;

public sealed partial class SpeakAIProvider
{
    private static readonly string[] ControlOptions = ["pollTimeoutSeconds", "deleteMedia"];

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.Audio is JsonElement value && value.ValueKind == JsonValueKind.String
            ? value.GetString() : request.Audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Invalid audio data URL.", nameof(request));
            audio = audio[(comma + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audio);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        return await Transcribe(bytes, request.MediaType, "audio" + ExtensionFor(request.MediaType),
            Options(request.ProviderOptions), request.Model, cancellationToken);
    }

    private async Task<TranscriptionResponse> Transcribe(byte[] audio, string mediaType, string fileName,
        JsonObject options, string? model, CancellationToken ct)
    {
        var isVideo = mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var kind = isVideo ? "video" : "audio";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            options["pollTimeoutSeconds"]?.GetValue<int>() ?? 1800, 30, 14_400));
        var deleteMedia = options["deleteMedia"]?.GetValue<bool>() == true;

        var payload = (JsonObject)options.DeepClone();
        foreach (var control in ControlOptions)
            payload.Remove(control);

        var uploadUrl = await UploadFile(audio, mediaType, fileName, kind, ct);
        payload["url"] = uploadUrl.Split('?')[0];
        payload.TryAdd("name", fileName);
        payload.TryAdd("mediaType", kind);

        var created = Unwrap(await Send(HttpMethod.Post, "media/upload", payload, ct));
        var mediaId = GetString(created, "mediaId")
            ?? throw new JsonException("SpeakAI upload response did not contain mediaId.");

        try
        {
            var state = await WaitUntilProcessed(mediaId, timeout, ct);
            var transcript = await Send(HttpMethod.Get, $"media/transcript/{Uri.EscapeDataString(mediaId)}", null, ct);
            return ConvertTranscription(transcript, mediaId, state, model, payload);
        }
        finally
        {
            if (deleteMedia)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await Send(HttpMethod.Delete, $"media/{Uri.EscapeDataString(mediaId)}", null, cleanup.Token);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<string> UploadFile(byte[] audio, string mediaType, string fileName, string kind,
        CancellationToken ct)
    {
        var query = $"media/upload/signedurl?filename={Uri.EscapeDataString(fileName)}" +
                    $"&mimeType={Uri.EscapeDataString(mediaType)}&mediaType={kind}&isVideo={(kind == "video" ? "true" : "false")}";
        var signed = Unwrap(await Send(HttpMethod.Get, query, null, ct));
        var uploadUrl = GetString(signed, "preSignedUrl", "signedUrl", "url")
            ?? throw new JsonException("SpeakAI signed URL response did not contain a URL.");

        // The pre-signed storage URL is authorized by its signature; the API key must not be sent to it.
        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(audio) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await _client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        return uploadUrl;
    }

    private async Task<string> WaitUntilProcessed(string mediaId, TimeSpan timeout, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var started = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = Unwrap(await Send(HttpMethod.Get, $"media/status/{Uri.EscapeDataString(mediaId)}", null, ct));
            var state = GetString(status, "state") ?? "";

            if (state is "processed" or "complete")
                return state;

            if (state == "failed")
                throw new InvalidOperationException($"SpeakAI media {mediaId} failed to process: {status.GetRawText()}");

            if (DateTime.UtcNow - started > timeout)
                throw new TimeoutException(
                    $"SpeakAI media {mediaId} was not processed within {timeout.TotalMinutes} minutes. Last state='{state}'.");

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(8, delay.TotalSeconds * 1.5));
        }
    }

    private static TranscriptionResponse ConvertTranscription(JsonElement raw, string mediaId, string state,
        string? model, JsonObject requestBody)
    {
        var data = Unwrap(raw);
        if (data.ValueKind == JsonValueKind.Array)
            data = data.GetArrayLength() > 0 ? data[0] : default;

        var insight = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("insight", out var nested)
            ? nested : data;

        var segments = new List<TranscriptionSegment>();
        if (insight.ValueKind == JsonValueKind.Object
            && insight.TryGetProperty("transcript", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var text = GetString(item, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var (start, end) = ReadTimes(item);
                segments.Add(new TranscriptionSegment { Text = text, StartSecond = start, EndSecond = end });
            }
        }

        double? duration = null;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("duration", out var d)
            && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("inSecond", out var seconds)
            && seconds.ValueKind == JsonValueKind.Number)
            duration = seconds.GetDouble();
        else if (segments.Count > 0)
            duration = (double)segments.Max(s => s.EndSecond);

        return new TranscriptionResponse
        {
            Text = string.Join(" ", segments.Select(s => s.Text)),
            Segments = segments,
            Language = GetString(data, "sourceLanguage"),
            DurationInSeconds = duration,
            ProviderMetadata = new()
            {
                [ProviderId] = JsonSerializer.SerializeToElement(new { mediaId, state, transcript = raw })
            },
            Request = new() { Body = requestBody.ToJsonString() },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = $"{ProviderId}/{LocalModel(model)}",
                Body = raw
            }
        };
    }

    // Sentences carry instances as an array of {startInSec,endInSec}; some payloads use a single object.
    private static (float Start, float End) ReadTimes(JsonElement item)
    {
        if (!item.TryGetProperty("instances", out var instances))
            return (0, 0);

        var list = instances.ValueKind == JsonValueKind.Array ? instances.EnumerateArray().ToList() : [instances];
        var starts = list.Select(i => Seconds(i, "startInSec")).Where(v => v.HasValue).ToList();
        var ends = list.Select(i => Seconds(i, "endInSec")).Where(v => v.HasValue).ToList();
        var start = starts.Count > 0 ? starts.Min()!.Value : 0f;
        var end = ends.Count > 0 ? ends.Max()!.Value : start;
        return (start, Math.Max(start, end));

        static float? Seconds(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number ? (float)value.GetDouble() : null;
    }

    private static string ExtensionFor(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/aac" => ".aac",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            _ => mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".mp3"
        };

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        var native = NativeOptions(options.AdditionalProperties);
        if (options.Language is not null)
            native.TryAdd("sourceLanguage", options.Language);

        using var buffer = new MemoryStream();
        await options.File.CopyToAsync(buffer, cancellationToken);
        var fileName = string.IsNullOrWhiteSpace(options.File.FileName)
            ? "audio" + ExtensionFor(options.File.ContentType)
            : options.File.FileName;
        var response = await Transcribe(buffer.ToArray(), options.File.ContentType, fileName, native,
            options.Model, cancellationToken);

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
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }
}
