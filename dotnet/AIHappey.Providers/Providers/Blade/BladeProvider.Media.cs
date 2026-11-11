using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Blade;

public partial class BladeProvider
{
    private static readonly JsonSerializerOptions BladeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string VideoOperationPrefix = "blade1_";

    private sealed record BladeVideoOperation(string JobId, string Model);

    private async Task<(byte[] Audio, string MimeType)> BladeOpenAISpeechRequest(
        AudioSpeechRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        if (options.Input.Length > 4096)
            throw new ArgumentOutOfRangeException(nameof(options.Input), "Blade speech input cannot exceed 4,096 characters.");

        var payload = new Dictionary<string, object?>
        {
            ["input"] = options.Input,
            ["model"] = options.Model
        };
        if (!string.IsNullOrWhiteSpace(options.Voice))
            payload["voice"] = options.Voice;

        using (var serializedOptions = JsonDocument.Parse(JsonSerializer.Serialize(options, BladeJsonOptions)))
        {
            if (serializedOptions.RootElement.TryGetProperty("providerOptions", out var providerOptions)
                && providerOptions.ValueKind == JsonValueKind.Object
                && providerOptions.TryGetProperty(GetIdentifier(), out var bladeOptions)
                && bladeOptions.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in bladeOptions.EnumerateObject())
                    payload[property.Name] = property.Value.Clone();
            }
        }

        using var request = JsonRequest(
            HttpMethod.Post,
            "v1/audio/speech",
            JsonSerializer.Serialize(payload, BladeJsonOptions));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException("speech", response.StatusCode, Encoding.UTF8.GetString(audio));

        return (audio, response.Content.Headers.ContentType?.MediaType ?? "audio/wav");
    }

    private async IAsyncEnumerable<StreamingTranscriptionPart> BladeTranscriptionStreaming(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Audio);

        var mediaType = ResolveStreamingTranscriptionMediaType(request.InputAudioFormat?.Type);
        var result = await BladeTranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = mediaType,
            ProviderOptions = request.ProviderOptions
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var warnings = request.InputAudioFormat?.Rate is null
            ? []
            : new List<object> { new { type = "unsupported", feature = "inputAudioFormat.rate" } };

        yield return new TranscriptionStreamStartPart { Warnings = warnings };
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = result.Response.Timestamp,
            ModelId = result.Response.ModelId,
            Headers = result.Response.Headers,
            Body = result.Response.Body
        };

        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = result.Response.Body };

        var segments = result.Segments.ToList();
        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TranscriptionFinalPart
            {
                Id = $"segment_{index}",
                Text = segments[index].Text,
                StartSecond = segments[index].StartSecond,
                EndSecond = segments[index].EndSecond
            };
        }

        if (!string.IsNullOrEmpty(result.Text))
            yield return new TranscriptionDeltaPart
            {
                Delta = result.Text,
                ProviderMetadata = result.ProviderMetadata
            };

        yield return new TranscriptionFinishPart
        {
            Text = result.Text,
            Segments = segments,
            Language = result.Language,
            DurationInSeconds = result.DurationInSeconds,
            ProviderMetadata = result.ProviderMetadata
        };
    }

    private async Task<SpeechResponse> BladeSpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (request.Text.Length > 4096)
            throw new ArgumentOutOfRangeException(nameof(request.Text), "Blade speech input cannot exceed 4,096 characters.");

        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["model"] = request.Model,
            ["voice"] = string.IsNullOrWhiteSpace(request.Voice) ? "tara" : request.Voice
        };
        MergeProviderOptions(payload, request.ProviderOptions);
        var body = JsonSerializer.Serialize(payload, BladeJsonOptions);

        using var httpRequest = JsonRequest(HttpMethod.Post, "v1/audio/speech", body);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException("speech", response.StatusCode, Encoding.UTF8.GetString(bytes));

        var headers = response.GetHeaders();
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = response.Content.Headers.ContentType?.MediaType ?? "audio/wav",
                Format = "wav"
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                contentType = response.Content.Headers.ContentType?.ToString(),
                contentLength = bytes.LongLength
            }),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    private async Task<TranscriptionResponse> BladeTranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var (audioBytes, mimeType) = DecodeAudio(request.Audio, request.MediaType);
        if (audioBytes.Length > 25 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request.Audio), "Blade transcription files cannot exceed 25 MB.");
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        multipart.Add(file, "file", $"audio{ExtensionForMimeType(mimeType)}");
        multipart.Add(new StringContent(request.Model), "model");

        var requestFields = new Dictionary<string, object?> { ["model"] = request.Model };
        MergeProviderOptions(requestFields, request.ProviderOptions);
        foreach (var (name, value) in requestFields)
        {
            if (name.Equals("model", StringComparison.OrdinalIgnoreCase))
                continue;

            var rawValue = value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
                JsonElement element => element.GetRawText(),
                string textItem => textItem,
                _ => JsonSerializer.Serialize(value, BladeJsonOptions)
            };
            multipart.Add(new StringContent(rawValue), name);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = multipart
        };
        AddHeaders(httpRequest, RequestHeaders());

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException("transcription", response.StatusCode, raw);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var segments = ReadTranscriptionSegments(root);
        var text = ReadString(root, "text") ?? string.Join(" ", segments.Select(segment => segment.Text).Where(value => value.Length > 0));
        var headers = response.GetHeaders();

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadString(root, "language"),
            DurationInSeconds = ReadFloat(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(new
                {
                    model = request.Model,
                    file = $"audio{ExtensionForMimeType(mimeType)}",
                    providerOptions = request.ProviderOptions is not null
                        && request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
                            ? options
                            : (JsonElement?)null
                }, BladeJsonOptions)
            }
        };
    }

    private async Task<VideoOperationStartResult> BladeStartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.Prompt.Length > 2000)
            throw new ArgumentOutOfRangeException(nameof(request.Prompt), "Blade video prompts cannot exceed 2,000 characters.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };
        if (request.Fps is not null) payload["fps"] = request.Fps;
        if (request.Seed is not null) payload["seed"] = request.Seed;
        MergeProviderOptions(payload, request.ProviderOptions);

        var root = await BladeJsonRequest(HttpMethod.Post, "v1/video/generations", payload, cancellationToken);
        var jobId = ReadString(root, "id");
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Blade video generation response did not contain a job id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(jobId, request.Model),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<VideoOperationStatusResult> BladeGetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken)
    {
        var data = DecodeVideoOperation(operation);
        var root = await BladeJsonRequest(
            HttpMethod.Get,
            $"v1/video/generations/{Uri.EscapeDataString(data.JobId)}",
            null,
            cancellationToken);

        var status = ReadString(root, "status") ?? ReadString(root, "state") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = data.Model.ToModelId(GetIdentifier())
        };

        if (IsPendingVideoStatus(status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (!IsCompletedVideoStatus(status))
        {
            return new VideoOperationErrorResult
            {
                Error = ReadString(root, "error") ?? $"Blade video generation ended with status '{status}' (id={data.JobId}).",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var url = ReadVideoUrl(root);
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult
            {
                Error = $"Blade video job completed without a download URL (id={data.JobId}).",
                ProviderMetadata = metadata,
                Response = response
            };

        using var download = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!download.IsSuccessStatusCode)
            throw CreateApiException("video download", download.StatusCode, Encoding.UTF8.GetString(bytes));

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = download.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private async Task<JsonElement> BladeDocumentRequest(
        string endpoint,
        object payload,
        CancellationToken cancellationToken)
        => await BladeJsonRequest(HttpMethod.Post, endpoint, payload, cancellationToken);

    private async Task<JsonElement> BladeJsonRequest(
        HttpMethod method,
        string endpoint,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = JsonRequest(method, endpoint, payload is null ? null : JsonSerializer.Serialize(payload, BladeJsonOptions));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw CreateApiException(endpoint, response.StatusCode, raw);

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private HttpRequestMessage JsonRequest(HttpMethod method, string endpoint, string? body)
    {
        var request = new HttpRequestMessage(method, endpoint);
        AddHeaders(request, RequestHeaders());
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json);
        return request;
    }

    private static void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private static void MergeProviderOptions(
        IDictionary<string, object?> payload,
        IReadOnlyDictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("blade", out var options) != true
            || options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

   private static (byte[] Bytes, string MimeType) DecodeAudio(object audio, string? mediaType)
{
    if (audio is not string value)
        throw new ArgumentException("Blade transcription audio must be a base64-encoded string.", nameof(audio));

    value = value.Trim();
    var resolvedMimeType = mediaType;
    var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);

    if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && marker >= 0)
    {
        resolvedMimeType = value[5..marker];
        value = value[(marker + ";base64,".Length)..];
    }

    try
    {
        return (Convert.FromBase64String(value), ResolveTranscriptionMimeType(resolvedMimeType));
    }
    catch (FormatException exception)
    {
        throw new ArgumentException("Blade transcription audio must be base64-encoded.", nameof(audio), exception);
    }
}
    private static string ExtensionForMimeType(string mimeType)
        => mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/ogg" => ".ogg",
            _ => ".wav"
        };

    private static string ResolveTranscriptionMimeType(string? mediaType)
    {
        var normalized = mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "wav" or "wave" or "audio/wav" or "audio/x-wav" or "audio/wave" => "audio/wav",
            "mp3" or "mpeg" or "audio/mp3" or "audio/mpeg" => "audio/mpeg",
            "m4a" or "audio/m4a" or "audio/x-m4a" or "audio/mp4" => "audio/mp4",
            "flac" or "audio/flac" or "audio/x-flac" => "audio/flac",
            "ogg" or "audio/ogg" => "audio/ogg",
            _ => throw new NotSupportedException(
                $"Blade transcription requires WAV, MP3, M4A, FLAC or OGG audio; received '{mediaType}'.")
        };
    }

    private static string ResolveStreamingTranscriptionMediaType(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("InputAudioFormat.Type is required.", nameof(format));

        var normalized = format.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp3" or "mpeg" or "audio/mpeg" or "audio/mp3" => "audio/mpeg",
            "wav" or "wave" or "audio/wav" or "audio/x-wav" or "audio/wave" => "audio/wav",
            "flac" or "audio/flac" or "audio/x-flac" => "audio/flac",
            "m4a" or "mp4" or "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "audio/mp4",
            "ogg" or "opus" or "audio/ogg" or "audio/opus" => "audio/ogg",
            _ => throw new NotSupportedException($"Blade transcription does not support input audio format '{format}'.")
        };
    }

    private static List<TranscriptionSegment> ReadTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];

        return values.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new TranscriptionSegment
            {
                Text = ReadString(item, "text") ?? string.Empty,
                StartSecond = ReadFloat(item, "start") ?? 0,
                EndSecond = ReadFloat(item, "end") ?? 0
            })
            .ToList();
    }

    private static string? ReadVideoUrl(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "video_url", "download_url", "url", "output_url" })
            {
                var value = ReadString(root, property);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            foreach (var property in new[] { "output", "outputs", "result", "video", "data" })
            {
                if (root.TryGetProperty(property, out var nested))
                {
                    var url = ReadVideoUrl(nested);
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var url = ReadVideoUrl(item);
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        return null;
    }

    private static bool IsPendingVideoStatus(string status)
        => status.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("processing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletedVideoStatus(string status)
        => status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase);

    private static string EncodeVideoOperation(string jobId, string model)
    {
        var json = JsonSerializer.Serialize(new BladeVideoOperation(jobId, model), BladeJsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return VideoOperationPrefix + encoded;
    }

    private static BladeVideoOperation DecodeVideoOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!operation.StartsWith(VideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Blade video operation token is invalid.", nameof(operation));

        var encoded = operation[VideoOperationPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            var data = JsonSerializer.Deserialize<BladeVideoOperation>(
                Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
                BladeJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Blade video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The Blade video operation token is invalid.", nameof(operation), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The Blade video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static InvalidOperationException CreateApiException(string operation, HttpStatusCode status, string body)
        => new(string.IsNullOrWhiteSpace(body)
            ? $"Blade {operation} failed ({(int)status})."
            : $"Blade {operation} failed ({(int)status}): {body}");

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? ReadFloat(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetSingle(out var number)
                ? number
                : null;
}
