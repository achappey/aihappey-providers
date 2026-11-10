using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.InferenceClub;

public partial class InferenceClubProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        var payload = ProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        Set(payload, "n", request.N);
        Set(payload, "size", request.Size);
        Set(payload, "seed", request.Seed);
        payload.TryAdd("response_format", "b64_json");
        var files = request.Files?.ToArray() ?? [];
        JsonElement result;
        if (files.Length == 0)
        {
            if (request.Mask is not null)
                throw new ArgumentException("An image edit mask requires a source image.", nameof(request));
            result = await WaitForResultAsync(
                await SendJsonAsync(HttpMethod.Post, "images/generations", payload, cancellationToken), cancellationToken);
        }
        else
        {
            using var form = new MultipartFormDataContent();
            AddFields(form, JsonSerializer.SerializeToElement(payload, JsonOptions));
            foreach (var file in files)
                await AddImageAsync(form, file is ImageFileUrl url ? url.Url : file.Data, file.MediaType,
                    files.Length == 1 ? "image" : "image[]", cancellationToken);
            if (request.Mask is not null)
                await AddImageAsync(form, request.Mask is ImageFileUrl mask ? mask.Url : request.Mask.Data,
                    request.Mask.MediaType, "mask", cancellationToken);
            result = await SendEditAsync(form, cancellationToken);
        }
        var images = await ImageDataAsync(result, cancellationToken);
        return new ImageResponse
        {
            Images = images.Select(image => image.DataUrl).ToArray(),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result),
            Response = ResponseInfo(request.Model),
            Warnings = request.AspectRatio is null ? [] : [new { type = "unsupported", feature = "aspectRatio", details = "Use size or native provider options." }]
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        var payload = Object(JsonSerializer.SerializeToElement(options, JsonOptions));
        payload["stream"] = false;
        payload.TryAdd("response_format", "b64_json");
        var result = await WaitForResultAsync(
            await SendJsonAsync(HttpMethod.Post, "images/generations", payload, cancellationToken), cancellationToken);
        return await ToOpenAIImagesAsync(result, cancellationToken);
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        if (options.ImageFiles?.Length is not > 0 && options.Images?.Length is not > 0)
            throw new ArgumentException("InferenceClub image edits require source images.", nameof(options));
        using var form = new MultipartFormDataContent();
        AddFields(form, JsonSerializer.SerializeToElement(options, JsonOptions), "images", "mask", "stream");
        form.Add(new StringContent("false"), "stream");
        if (options.AdditionalProperties?.ContainsKey("response_format") != true)
            form.Add(new StringContent("b64_json"), "response_format");
        if (options.ImageFiles?.Length > 0)
            foreach (var file in options.ImageFiles)
                await AddUploadAsync(form, file, options.ImageFiles.Length == 1 ? "image" : "image[]", cancellationToken);
        else
            foreach (var reference in options.Images!)
                await AddImageAsync(form, reference.ImageUrl, null, options.Images.Length == 1 ? "image" : "image[]", cancellationToken);
        if (options.MaskFile is not null)
            await AddUploadAsync(form, options.MaskFile, "mask", cancellationToken);
        else if (options.Mask is not null)
            await AddImageAsync(form, options.Mask.ImageUrl, null, "mask", cancellationToken);
        return await ToOpenAIImagesAsync(await SendEditAsync(form, cancellationToken), cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json!, CreatedAt = response.Created };
        }
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new OpenAIImageEditCompleted { B64Json = image.B64Json!, CreatedAt = response.Created };
        }
    }

    private async Task<JsonElement> SendEditAsync(MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Post, "images/edits", form);
        using var response = await _client.SendAsync(message, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static void AddFields(MultipartFormDataContent form, JsonElement fields, params string[] excluded)
    {
        foreach (var field in fields.EnumerateObject())
        {
            if (excluded.Contains(field.Name))
                continue;
            if (field.Name == "timestamp_granularities" && field.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in field.Value.EnumerateArray())
                    form.Add(new StringContent(item.GetString()!), "timestamp_granularities[]");
            }
            else
                form.Add(new StringContent(field.Value.ValueKind == JsonValueKind.String
                    ? field.Value.GetString()! : field.Value.GetRawText()), field.Name);
        }
    }

    private async Task AddImageAsync(MultipartFormDataContent form, string? data, string? mediaType,
        string field, CancellationToken cancellationToken)
    {
        var (bytes, type) = await ReadFileAsync(data, mediaType, cancellationToken);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(content, field, "image." + (type == "image/jpeg" ? "jpg" : type.Split('/').Last()));
    }

    private static async Task AddUploadAsync(MultipartFormDataContent form, IFormFile file,
        string field, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, cancellationToken);
        var content = new ByteArrayContent(memory.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        form.Add(content, field, string.IsNullOrWhiteSpace(file.FileName) ? "upload" : file.FileName);
    }

    private async Task<List<(string Base64, string DataUrl)>> ImageDataAsync(JsonElement result, CancellationToken cancellationToken)
    {
        var images = new List<(string, string)>();
        foreach (var item in Outputs(result))
        {
            var base64 = ReadString(item, "b64_json");
            var mediaType = ReadString(item, "content_type") ?? "image/png";
            if (string.IsNullOrWhiteSpace(base64))
            {
                var (bytes, type) = await DownloadAsync(
                    ReadString(item, "url") ?? throw new InvalidOperationException($"InferenceClub image has no data: {item}"),
                    cancellationToken);
                base64 = Convert.ToBase64String(bytes);
                mediaType = type;
            }
            images.Add((base64, base64.ToDataUrl(mediaType)));
        }
        if (images.Count == 0)
            throw new InvalidOperationException($"InferenceClub returned no images: {result}");
        return images;
    }

    private async Task<OpenAIImagesResponse> ToOpenAIImagesAsync(JsonElement result, CancellationToken cancellationToken)
    {
        var images = await ImageDataAsync(result, cancellationToken);
        var response = result.Deserialize<OpenAIImagesResponse>(JsonOptions)
            ?? throw new InvalidOperationException("InferenceClub image response was empty.");
        if (!result.TryGetProperty("created", out _))
            response.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var original = response.Data;
        response.Data = images.Select((image, index) =>
        {
            var item = original?.ElementAtOrDefault(index) ?? new OpenAIImageData();
            item.B64Json = image.Base64;
            return item;
        }).ToList();
        return response;
    }
}
