using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WavenAI;

public sealed partial class WavenAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var model = LocalModel(request.Model).Split('/', 2);
        var options = NativeOptions(request.ProviderOptions);
        Set(options, "model", model[0]);
        Set(options, "text", request.Text);
        var voice = model.Length > 1 ? model[1] : request.Voice;
        if (model[0] == "chatterbox")
        {
            if (voice is not null and not "default") Set(options, "gallery_voice_id", voice);
        }
        else Set(options, "speaker", voice);
        Set(options, "speed", request.Speed);
        Set(options, "language", request.Language);
        Set(options, "format", request.OutputFormat);
        if (request.Speed is < 0.25 or > 4)
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "WavenAI speed must be between 0.25 and 4.");
        var format = options.TryGetValue("format", out var output) ? output.GetString()! : "wav";
        if (format is not ("wav" or "mp3" or "ogg"))
            throw new NotSupportedException("WavenAI native speech supports wav, mp3 and ogg. Use the OpenAI speech endpoint for other formats.");
        var longForm = Flag(options, "long_form", remove: true) || request.Text.Length > 5000
            || options.ContainsKey("chunk_max_chars") || options.ContainsKey("pause_ms")
            || options.ContainsKey("allow_partial") || options.ContainsKey("background");
        using var message = CreateRequest(HttpMethod.Post, longForm ? "generate-long" : "generate");
        ApplyNativeHeaders(message, options);
        var form = new MultipartFormDataContent();
        message.Content = form;
        AddNativeFields(form, options);
        var timestamp = DateTime.UtcNow;
        var result = await SendJson(message, cancellationToken);
        if (result.TryGetProperty("job_id", out _))
        {
            result = await WaitForJob(result, "generate-long/jobs", cancellationToken);
            if (result.TryGetProperty("result", out var completed) && completed.ValueKind == JsonValueKind.Object)
            {
                // Keep job-level partial-result and failure details in provider metadata.
                var metadata = result;
                return await DownloadSpeech(completed, metadata, request, options, format, timestamp, cancellationToken);
            }
        }
        return await DownloadSpeech(result, result, request, options, format, timestamp, cancellationToken);
    }

    private async Task<SpeechResponse> DownloadSpeech(JsonElement result, JsonElement metadata, SpeechRequest request,
        Dictionary<string, JsonElement> options, string format, DateTime timestamp, CancellationToken cancellationToken)
    {
        var file = ReadString(result, "file")
            ?? throw new InvalidOperationException($"WavenAI generation did not return an audio file: {result.GetRawText()}");
        var url = new Uri(ApiBase, file);
        if (url.Scheme != Uri.UriSchemeHttps || url.Host != ApiBase.Host || url.Port != ApiBase.Port
            || !url.AbsolutePath.StartsWith("/api/v1/audio/", StringComparison.Ordinal))
            throw new InvalidOperationException("WavenAI returned an audio URL outside its authenticated audio endpoint.");
        using var download = CreateRequest(HttpMethod.Get, url.AbsoluteUri);
        using var response = await _client.SendAsync(download, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WavenAI audio download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        if (bytes.Length == 0) throw new InvalidOperationException("WavenAI returned empty speech audio.");
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions", details = "WavenAI engines do not support style instructions." });
        if (metadata.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True
            || result.TryGetProperty("truncated", out truncated) && truncated.ValueKind == JsonValueKind.True)
            warnings.Add(new { type = "other", message = "WavenAI returned partial speech because a later chunk failed." });
        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = response.Content.Headers.ContentType?.MediaType
                    ?? (format == "mp3" ? "audio/mpeg" : $"audio/{format}"),
                Format = format
            },
            Warnings = warnings,
            Request = new() { Body = options },
            Response = new() { Timestamp = timestamp, ModelId = request.Model.ToModelId(ProviderId), Body = metadata },
            ProviderMetadata = new() { [ProviderId] = metadata.Clone() }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = JsonSerializer.SerializeToElement(options).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var model = LocalModel(options.Model).Split('/', 2);
        Set(payload, "model", model[0]);
        if (model.Length > 1) Set(payload, "voice", model[1]);
        using var message = CreateRequest(HttpMethod.Post, "audio/speech");
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WavenAI speech failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        if (audio.Length == 0) throw new InvalidOperationException("WavenAI returned empty speech audio.");
        return (audio, response.Content.Headers.ContentType?.MediaType
            ?? options.ResponseFormat switch
            {
                "wav" => "audio/wav", "ogg" or "opus" => "audio/ogg",
                "flac" => "audio/flac", "aac" => "audio/aac", "pcm" => "audio/pcm",
                _ => "audio/mpeg"
            });
    }
}
