using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferenceClub;

public partial class InferenceClubProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        if (request.N is > 1)
            throw new NotSupportedException("InferenceClub generates one video per request.");
        if (request.InputReferences?.Any() == true)
            throw new NotSupportedException("InferenceClub video accepts a first-frame image, not input references.");
        var frames = request.FrameImages?.ToArray() ?? [];
        if (frames.Length > 1 || frames.Any(frame => frame.FrameType != "first_frame"))
            throw new NotSupportedException("InferenceClub video supports only one first-frame image.");
        if (request.Image is not null && frames.Length > 0)
            throw new ArgumentException("Supply either image or a first-frame image, not both.", nameof(request));
        var payload = ProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        Set(payload, "duration", request.Duration);
        Set(payload, "fps", request.Fps);
        Set(payload, "seed", request.Seed);
        if (request.Resolution is not null)
        {
            var dimensions = request.Resolution.Split('x');
            if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width) || !int.TryParse(dimensions[1], out var height))
                throw new ArgumentException("InferenceClub video resolution must be WIDTHxHEIGHT.", nameof(request));
            payload["width"] = width;
            payload["height"] = height;
        }
        var image = request.Image ?? frames.FirstOrDefault()?.Image;
        if (image is not null)
        {
            var (bytes, mediaType) = await ReadFileAsync(
                image is VideoFileUrl url ? url.Url : image.Data, image.MediaType, cancellationToken);
            payload["image"] = Convert.ToBase64String(bytes).ToDataUrl(mediaType);
        }
        payload.TryAdd("async", true);
        payload.TryAdd("delivery", "url");
        using var message = CreateRequest(HttpMethod.Post, "videos/generations", JsonContent.Create(payload, options: JsonOptions));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        JsonElement result;
        string id;
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "video/mp4")
        {
            id = response.Headers.TryGetValues("X-Inference-Request-Id", out var values)
                ? values.First() : throw new InvalidOperationException("InferenceClub video response has no request id.");
            result = JsonSerializer.SerializeToElement(new { id });
        }
        else
        {
            result = await ReadJsonAsync(response, cancellationToken);
            id = JobId(result);
        }
        var token = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new VideoOperation(id, request.Model)));
        return new VideoOperationStartResult
        {
            Operation = "icv1_" + token.TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = ResponseInfo(request.Model),
            Warnings = request.AspectRatio is null && request.GenerateAudio is null ? []
                : [new { type = "unsupported", feature = "aspectRatio/generateAudio", details = "Use native video provider options." }]
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        VideoOperation data;
        try
        {
            if (!operation.StartsWith("icv1_", StringComparison.Ordinal))
                throw new FormatException();
            var encoded = operation[5..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            data = JsonSerializer.Deserialize<VideoOperation>(Convert.FromBase64String(encoded)) ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(data.Id) || string.IsNullOrWhiteSpace(data.Model))
                throw new JsonException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid InferenceClub video operation token.", nameof(operation), ex);
        }
        var result = await GetJobAsync(data.Id, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(result);
        var info = ResponseInfo(data.Model);
        switch (ReadString(result, "status"))
        {
            case "QUEUED":
            case "PROCESSING":
                return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = info };
            case "FAILED":
            case "CANCELED":
                return new VideoOperationErrorResult { Error = result.GetRawText(), ProviderMetadata = metadata, Response = info };
            case "PROCESSED":
                var videos = new List<VideoOperationVideoData>();
                foreach (var output in Outputs(result).Where(o => ReadString(o, "kind") == "video"))
                {
                    var (bytes, type) = await DownloadAsync(
                        ReadString(output, "url") ?? throw new InvalidOperationException("InferenceClub video has no URL."),
                        cancellationToken);
                    videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(bytes), MediaType = type });
                }
                if (videos.Count == 0)
                    throw new InvalidOperationException($"InferenceClub job returned no videos: {result}");
                return new VideoOperationCompletedResult { Videos = videos, ProviderMetadata = metadata, Response = info };
            default:
                throw new InvalidOperationException($"Unknown InferenceClub video status: {result}");
        }
    }

    private sealed record VideoOperation(string Id, string Model);
}
