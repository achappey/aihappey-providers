using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inferel;

public sealed partial class InferelProvider
{
    private static HashSet<string> ParameterNames(JsonElement model)
    {
        foreach (var name in new[] { "parameters", "input_schema", "parameter_schema" })
        {
            if (!model.TryGetProperty(name, out var schema)) continue;
            if (schema.ValueKind == JsonValueKind.Array)
                return schema.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String
                    ? p.GetString()! : RequiredText(p, "name")).ToHashSet(StringComparer.Ordinal);
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("properties", out var properties)) schema = properties;
                return schema.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            }
            throw new JsonException($"Inferel returned an invalid parameter schema: {schema}");
        }
        return [];
    }

    private async Task<(Dictionary<string, JsonElement> Payload, HashSet<string> Parameters)> MediaPayload(
        string model, string prompt, string kind, Dictionary<string, JsonElement>? options, string key, CancellationToken ct)
    {
        var payload = options?.TryGetValue(GetIdentifier(), out var native) == true ? Properties(native) : [];
        var parameters = ParameterNames(await ModelDetail(model, key, ct));
        var promptField = parameters.Contains("text") ? "text" : parameters.Contains("prompt") ? "prompt"
            : kind == "image" ? "text" : "prompt";
        if (!payload.ContainsKey(promptField))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            payload[promptField] = JsonSerializer.SerializeToElement(prompt);
        }
        return (payload, parameters);
    }

    private static void Option<T>(Dictionary<string, JsonElement> payload, HashSet<string> parameters,
        List<object> warnings, string feature, T value, params string[] names)
    {
        if (value is null) return;
        if (names.Any(payload.ContainsKey)) return;
        var name = names.FirstOrDefault(parameters.Contains);
        if (name is null)
            warnings.Add(new { type = "unsupported", feature, details = "Use this model's native Inferel provider options." });
        else
            payload[name] = JsonSerializer.SerializeToElement(value);
    }

    private static void Dimensions(Dictionary<string, JsonElement> payload, HashSet<string> parameters,
        List<object> warnings, string? size)
    {
        if (size is null) return;
        if (parameters.Contains("size"))
        {
            Option(payload, parameters, warnings, "size", size, "size");
            return;
        }
        if (parameters.Contains("width") && parameters.Contains("height"))
        {
            var parts = size.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || width <= 0
                || !int.TryParse(parts[1], out var height) || height <= 0)
                throw new ArgumentException("Inferel image/video dimensions must be WIDTHxHEIGHT.");
            Option(payload, parameters, warnings, "width", width, "width");
            Option(payload, parameters, warnings, "height", height, "height");
            return;
        }
        Option(payload, parameters, warnings, "resolution", size, "resolution");
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        var key = Key();
        var (payload, parameters) = await MediaPayload(request.Model, request.Prompt, "image",
            request.ProviderOptions, key, cancellationToken);
        var warnings = new List<object>();
        Dimensions(payload, parameters, warnings, request.Size);
        Option(payload, parameters, warnings, "aspectRatio", request.AspectRatio, "aspect_ratio");
        Option(payload, parameters, warnings, "seed", request.Seed, "seed");
        Option(payload, parameters, warnings, "n", request.N, "n", "num_images");
        var files = request.Files?.ToArray() ?? [];
        if (files.Length > 0)
            await InputImages(payload, parameters, files.Select(file =>
                (file is ImageFileUrl url ? url.Url : file.Data!, file.MediaType)).ToArray(), key, cancellationToken);
        if (request.Mask is { } mask)
            await InputFile(payload, parameters, ["mask_url", "mask"],
                mask is ImageFileUrl url ? url.Url : mask.Data!, mask.MediaType, key, cancellationToken);

        var result = await Submit(request.Model, payload, key, cancellationToken);
        if (!Terminal(result))
        {
            var id = RequiredText(result, "request_id");
            result = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                ct => Job(id, key, ct), Terminal, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), null, cancellationToken);
        }
        if (RequiredText(result, "status") != "success")
            throw new InvalidOperationException($"Inferel image generation failed: {result.GetRawText()}");
        var images = new List<string>();
        foreach (var output in Outputs(result, "image"))
        {
            var (bytes, type) = await Download(RequiredText(output, "url"), "image/png", cancellationToken);
            images.Add($"data:{type};base64,{Convert.ToBase64String(bytes)}");
        }
        if (images.Count == 0) throw new JsonException($"Inferel job returned no images: {result}");
        return new ImageResponse
        {
            Images = images, Warnings = warnings, ProviderMetadata = Metadata(result), Response = Info(request.Model)
        };
    }

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        var frames = request.FrameImages?.ToArray() ?? [];
        if (frames.GroupBy(frame => frame.FrameType).Any(group => group.Count() > 1)
            || request.Image is not null && frames.Any(frame => frame.FrameType == "first_frame"))
            throw new ArgumentException("Supply at most one image per video frame.", nameof(request));
        var key = Key();
        var (payload, parameters) = await MediaPayload(request.Model, request.Prompt, "video",
            request.ProviderOptions, key, cancellationToken);
        var warnings = new List<object>();
        Dimensions(payload, parameters, warnings, request.Resolution);
        Option(payload, parameters, warnings, "aspectRatio", request.AspectRatio, "aspect_ratio");
        Option(payload, parameters, warnings, "seed", request.Seed, "seed");
        Option(payload, parameters, warnings, "n", request.N, "n", "num_videos");
        Option(payload, parameters, warnings, "duration", request.Duration, "duration");
        Option(payload, parameters, warnings, "fps", request.Fps, "fps");
        Option(payload, parameters, warnings, "generateAudio", request.GenerateAudio, "generate_audio");
        if (request.Image is { } image)
            await InputFile(payload, parameters, ["image_url", "image", "first_frame_url"],
                image is VideoFileUrl url ? url.Url : image.Data!, image.MediaType, key, cancellationToken);
        foreach (var frame in frames)
        {
            var names = frame.FrameType switch
            {
                "first_frame" => new[] { "first_frame_url", "image_url", "image" },
                "last_frame" => ["last_frame_url", "end_image_url"],
                _ => throw new NotSupportedException($"Unsupported Inferel frame type: {frame.FrameType}.")
            };
            await InputFile(payload, parameters, names,
                frame.Image is VideoFileUrl url ? url.Url : frame.Image.Data!, frame.Image.MediaType, key, cancellationToken);
        }
        if (request.InputReferences?.Any() == true)
            await InputImages(payload, parameters, request.InputReferences.Select(file =>
                (file is VideoFileUrl url ? url.Url : file.Data!, file.MediaType)).ToArray(), key, cancellationToken);
        var result = await Submit(request.Model, payload, key, cancellationToken);
        var id = RequiredText(result, "request_id");
        return new VideoOperationStartResult
        {
            Operation = "ifv1_" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new VideoOperation(id, NativeModel(request.Model)))).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Warnings = warnings, ProviderMetadata = Metadata(result), Response = Info(request.Model)
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        VideoOperation data;
        try
        {
            if (!operation.StartsWith("ifv1_", StringComparison.Ordinal)) throw new FormatException();
            var encoded = operation[5..].Replace('-', '+').Replace('_', '/');
            data = JsonSerializer.Deserialize<VideoOperation>(Convert.FromBase64String(
                encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))) ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(data.Id) || string.IsNullOrWhiteSpace(data.Model)) throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new ArgumentException("Invalid Inferel video operation token.", nameof(operation), ex);
        }
        var result = await Job(data.Id, Key(), cancellationToken);
        var metadata = Metadata(result);
        var info = Info(data.Model);
        if (!Terminal(result))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = info };
        if (RequiredText(result, "status") != "success")
            return new VideoOperationErrorResult { Error = result.GetRawText(), ProviderMetadata = metadata, Response = info };
        var videos = new List<VideoOperationVideoData>();
        foreach (var output in Outputs(result, "video"))
        {
            var (bytes, type) = await Download(RequiredText(output, "url"), "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(bytes), MediaType = type });
        }
        if (videos.Count == 0) throw new JsonException($"Inferel job returned no videos: {result}");
        return new VideoOperationCompletedResult { Videos = videos, ProviderMetadata = metadata, Response = info };
    }

    private Task<JsonElement> Submit(string model, Dictionary<string, JsonElement> payload, string key, CancellationToken ct)
        => SendJson(HttpMethod.Post, QueueUrl + "requests", key, new { model = NativeModel(model), payload }, ct);

    private Task<JsonElement> Job(string id, string key, CancellationToken ct)
        => SendJson(HttpMethod.Get, QueueUrl + "requests/" + Uri.EscapeDataString(id), key, null, ct);

    private static bool Terminal(JsonElement result)
        => RequiredText(result, "status") is "success" or "failed" or "cancelled";

    private static IEnumerable<JsonElement> Outputs(JsonElement result, string kind)
        => result.GetProperty("outcome").GetProperty("media_urls").EnumerateArray()
            .Where(item => RequiredText(item, "type") == kind);

    private static Dictionary<string, JsonElement> Metadata(JsonElement result) => new() { ["inferel"] = result.Clone() };
    private static HeaderResponseData Info(string model)
        => new() { ModelId = $"inferel/{NativeModel(model)}", Timestamp = DateTime.UtcNow };
    private sealed record VideoOperation(string Id, string Model);

    private async Task InputImages(Dictionary<string, JsonElement> payload, HashSet<string> parameters,
        (string Data, string? Type)[] files, string key, CancellationToken ct)
    {
        var name = new[] { "image_urls", "images", "reference_image_urls" }.FirstOrDefault(parameters.Contains);
        if (name is null && files.Length == 1)
        {
            await InputFile(payload, parameters, ["image_url", "image"], files[0].Data, files[0].Type, key, ct);
            return;
        }
        if (name is null) throw new NotSupportedException("This Inferel model does not expose a supported image-list parameter. Use native provider options.");
        if (payload.ContainsKey(name)) return;
        var urls = new List<string>();
        foreach (var (data, type) in files) urls.Add(await InputUrl(data, type, key, ct));
        payload[name] = JsonSerializer.SerializeToElement(urls);
    }

    private async Task InputFile(Dictionary<string, JsonElement> payload, HashSet<string> parameters,
        string[] names, string data, string? type, string key, CancellationToken ct)
    {
        if (names.Any(payload.ContainsKey)) return;
        var name = names.FirstOrDefault(parameters.Contains)
            ?? throw new NotSupportedException($"This Inferel model does not expose {string.Join("/", names)}. Use native provider options.");
        payload[name] = JsonSerializer.SerializeToElement(await InputUrl(data, type, key, ct));
    }

    private async Task<string> InputUrl(string data, string? mediaType, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return data;
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Inferel input files must be URLs or base64-encoded data.");
            mediaType = data[5..data.IndexOf(';')];
            data = data[(comma + 1)..];
        }
        var fileType = mediaType switch
        {
            "image/jpeg" => "jpeg", "image/jpg" => "jpg", "image/png" => "png",
            "video/mp4" => "mp4", "audio/mpeg" => "mp3", "audio/wav" => "wav",
            _ => throw new NotSupportedException($"Inferel does not support uploading '{mediaType}'. Use a public URL.")
        };
        var bytes = Convert.FromBase64String(data);
        var upload = await SendJson(HttpMethod.Post, QueueUrl + "upload-url", key, new { file_type = fileType }, ct);
        using var request = new HttpRequestMessage(HttpMethod.Put, RequiredText(upload, "upload_url"))
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType!);
        using var response = await _mediaClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Inferel media upload failed: {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
        return RequiredText(upload, "public_url");
    }

    private async Task<(byte[] Bytes, string Type)> Download(string url, string fallback, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new JsonException("Inferel returned an invalid media URL.");
        using var response = await _mediaClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Inferel media download failed: {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
        return (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? fallback);
    }
}
