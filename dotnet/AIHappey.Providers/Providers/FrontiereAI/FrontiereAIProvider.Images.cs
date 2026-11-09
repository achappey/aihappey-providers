using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FrontiereAI;

public sealed partial class FrontiereAIProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var copy = NormalizeOptions(options);
        copy.Stream = false;
        copy.PartialImages = null;
        return SendImageAsync("v1/images/generations",
            new StringContent(JsonSerializer.Serialize(copy, ImageJsonOptions), Encoding.UTF8, "application/json"), cancellationToken);
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var copy = NormalizeOptions(options);
        copy.ImageFiles = options.ImageFiles;
        copy.MaskFile = options.MaskFile;
        copy.Stream = false;
        copy.PartialImages = null;
        return SendImageAsync("v1/images/edits", CreateEditContent(copy, cancellationToken), cancellationToken);
    }

    private async Task<OpenAIImagesResponse> SendImageAsync(string endpoint, HttpContent content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        foreach (var (name, value) in AuthHeaders())
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"FrontiereAI image request failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
        return JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, ImageJsonOptions)
            ?? throw new JsonException("FrontiereAI returned an empty image response.");
    }

    private static HttpContent CreateEditContent(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        if (options.ImageFiles?.Length is not > 0 && options.MaskFile is null)
            return new StringContent(JsonSerializer.Serialize(options, ImageJsonOptions), Encoding.UTF8, "application/json");

        var content = new MultipartFormDataContent();
        try
        {
            foreach (var field in JsonSerializer.SerializeToElement(options, ImageJsonOptions).EnumerateObject())
            {
                if (field.Name == "mask" && options.MaskFile is not null)
                    continue;
                content.Add(new StringContent(
                    field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText(),
                    Encoding.UTF8), field.Name);
            }
            foreach (var file in options.ImageFiles ?? [])
                AddImageFile(content, file, "image[]", cancellationToken);
            if (options.MaskFile is not null)
                AddImageFile(content, options.MaskFile, "mask", cancellationToken);
            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    private static void AddImageFile(MultipartFormDataContent content, Microsoft.AspNetCore.Http.IFormFile file,
        string field, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        file.CopyToAsync(memory, cancellationToken).GetAwaiter().GetResult();
        var image = new ByteArrayContent(memory.ToArray());
        image.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType is { Length: > 0 }
            ? file.ContentType : "application/octet-stream");
        content.Add(image, field, string.IsNullOrWhiteSpace(file.FileName) ? "image.png" : file.FileName);
    }

    // The documented image endpoints are synchronous. These are completed-event adapters.
    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = await ImageBase64Async(image, cancellationToken),
                CreatedAt = response.Created,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                Usage = response.Usage
            };
        }
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new OpenAIImageEditCompleted
            {
                B64Json = await ImageBase64Async(image, cancellationToken),
                CreatedAt = response.Created,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                Usage = response.Usage
            };
        }
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(request.Model),
            ["prompt"] = JsonSerializer.SerializeToElement(request.Prompt),
            ["response_format"] = JsonSerializer.SerializeToElement("b64_json")
        };
        if (request.N is not null)
            payload["n"] = JsonSerializer.SerializeToElement(request.N);
        if (request.Size is not null)
            payload["size"] = JsonSerializer.SerializeToElement(request.Size);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) == true && native.ValueKind == JsonValueKind.Object)
            foreach (var property in native.EnumerateObject())
                payload[property.Name] = property.Value.Clone();

        var files = request.Files?.ToArray() ?? [];
        OpenAIImagesResponse response;
        if (files.Length > 0)
        {
            payload.Remove("response_format");
            var edit = JsonSerializer.Deserialize<OpenAIImageEditRequest>(JsonSerializer.Serialize(payload))!;
            edit.Images = files.Select(ImageReference).ToArray();
            edit.Mask = request.Mask is null ? null : ImageReference(request.Mask);
            response = await OpenAIImageEditRequestAsync(edit, cancellationToken);
        }
        else
        {
            if (request.Mask is not null)
                throw new ArgumentException("An image mask requires a reference image.", nameof(request));
            response = await OpenAIImageGenerationRequestAsync(
                JsonSerializer.Deserialize<OpenAIImageGenerationRequest>(JsonSerializer.Serialize(payload))!, cancellationToken);
        }

        List<string> images = [];
        var format = response.OutputFormat ?? ReadString(JsonSerializer.SerializeToElement(payload), "output_format") ?? "png";
        var mediaType = format.ToLowerInvariant() switch { "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp", _ => "image/png" };
        foreach (var image in response.Data ?? [])
            images.Add($"data:{mediaType};base64,{await ImageBase64Async(image, cancellationToken)}");
        if (images.Count == 0)
            throw new InvalidOperationException("FrontiereAI returned no images.");

        List<object> warnings = [];
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.AspectRatio is not null)
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new() { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow },
            ProviderMetadata = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(response) },
            Usage = response.Usage is null ? null : new()
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            }
        };
    }

    private static OpenAIImageReference ImageReference(ImageFile file)
    {
        var data = file.GetDataOrUrl();
        return new()
        {
            ImageUrl = file.Type == "url" || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? data : $"data:{file.MediaType ?? "image/png"};base64,{data}"
        };
    }

    private async Task<string> ImageBase64Async(OpenAIImageData image, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(image.B64Json))
            return image.B64Json;
        if (Uri.TryCreate(image.Url, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
        {
            using var request = PublicRequest(HttpMethod.Get, url.AbsoluteUri);
            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }
        throw new InvalidOperationException("FrontiereAI returned an image without base64 data or a URL.");
    }

    private T NormalizeOptions<T>(T options)
    {
        var fields = JsonSerializer.SerializeToElement(options).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        if (fields.Remove("providerMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty(GetIdentifier(), out var scoped) && scoped.ValueKind == JsonValueKind.Object)
            foreach (var property in scoped.EnumerateObject())
                fields[property.Name] = property.Value.Clone();
        if (fields.TryGetValue("model", out var model) && model.ValueKind == JsonValueKind.String
            && model.GetString() is { } id && id.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase))
            fields["model"] = JsonSerializer.SerializeToElement(id[(GetIdentifier().Length + 1)..]);
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(fields))!;
    }
}
