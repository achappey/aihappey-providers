using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // DashScope (sync) endpoint (Singapore / intl)
    private const string DefaultDashScopeBaseUrl = "https://dashscope-intl.aliyuncs.com";
    private const string QwenImageSyncPath = "/api/v1/services/aigc/multimodal-generation/generation";

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (IsWan26Model(imageRequest.Model))
            return await Wan26ImageRequest(imageRequest, providerMetadata, imageRequest.Model, warnings, now, cancellationToken);

        var inputImages = imageRequest.Files?.ToList() ?? [];
        var isQwenImage3 = imageRequest.Model.StartsWith("qwen-image-3.0", StringComparison.OrdinalIgnoreCase);

        if (inputImages.Count > 0 && !isQwenImage3)
        {
            warnings.Add(new { type = "unsupported", feature = "files" });
            inputImages = [];
        }

        if (isQwenImage3 && inputImages.Count > 3)
            throw new ArgumentException("Qwen Image 3.0 supports between 1 and 3 reference images.", nameof(imageRequest));

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        // NOTE: per requirements, we do not validate/limit sizes.
        // We only reshape "WxH" => "W*H" for DashScope.
        var dashScopeSize = MapGenericSizeToDashScope(imageRequest.Size);

        var content = new List<object>();
        foreach (var image in inputImages)
            content.Add(new { image = ToDashScopeImageDataUrl(image) });
        content.Add(new { text = imageRequest.Prompt });

        var payload = new
        {
            model = imageRequest.Model,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content
                    }
                }
            },
            parameters = BuildImageParameters(
                providerMetadata,
                GetImageApiContract(imageRequest.Model),
                dashScopeSize,
                imageRequest.Seed,
                imageRequest.N)
        };

        // Singapore-only: always use intl base.
        var baseUrl = DefaultDashScopeBaseUrl;

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}{QwenImageSyncPath}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var (imageUrls, _) = ExtractImagesAndTextFromSyncResponse(raw);
        if (imageUrls.Count == 0)
            throw new Exception("DashScope response did not contain an image URL.");

        List<string> images = [];
        foreach (var imageUrl in imageUrls)
        {
            var bytes = await _client.GetByteArrayAsync(imageUrl, cancellationToken);
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Png));
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string? MapGenericSizeToDashScope(string? genericSize)
    {
        if (string.IsNullOrWhiteSpace(genericSize))
            return null;

        // request uses "1664x928" while DashScope expects "1664*928"
        return genericSize.Trim().ToLowerInvariant().Replace('x', '*');
    }

    private static Dictionary<string, object?> BuildImageParameters(
        JsonElement metadata,
        ImageApiContract apiContract,
        string? size,
        int? seed,
        int? count)
    {
        ValidateProviderMetadata(metadata, apiContract);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            if (size is not null)
                parameters["size"] = size;
            if (seed is not null)
                parameters["seed"] = seed;
            if (count is not null)
                parameters["n"] = count;
            return parameters;
        }

        foreach (var property in metadata.EnumerateObject())
            AddImageOption(parameters, property, apiContract, includeMaxImages: false);

        parameters["size"] = size;
        parameters["seed"] = seed;
        parameters["n"] = count;
        return parameters;
    }

    private enum ImageApiContract
    {
        Qwen,
        ZImage,
        Wan26
    }

    private static ImageApiContract GetImageApiContract(string modelName)
        => IsWan26Model(modelName)
            ? ImageApiContract.Wan26
            : string.Equals(modelName, "z-image-turbo", StringComparison.OrdinalIgnoreCase)
                ? ImageApiContract.ZImage
                : ImageApiContract.Qwen;

    private static void ValidateProviderMetadata(JsonElement metadata, ImageApiContract apiContract)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Alibaba provider options must be a JSON object.");

        foreach (var property in metadata.EnumerateObject())
            ValidateMappedImageOption(property.Name, property.Value, apiContract);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        if (!found)
            value = default;

        return found;
    }

    private static bool? TryGetOption(JsonElement options, string propertyName)
    {
        if (!TryGetProperty(options, propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.Deserialize<bool>();
    }

    private static void AddImageOption(
        Dictionary<string, object?> parameters,
        JsonProperty property,
        ImageApiContract apiContract,
        bool includeMaxImages)
    {
        ValidateMappedImageOption(property.Name, property.Value, apiContract);

        var mappedName = property.Name switch
        {
            var name when apiContract is ImageApiContract.Qwen or ImageApiContract.Wan26
                && string.Equals(name, "negativePrompt", StringComparison.OrdinalIgnoreCase) => "negative_prompt",
            var name when string.Equals(name, "promptExtend", StringComparison.OrdinalIgnoreCase) => "prompt_extend",
            var name when apiContract is ImageApiContract.Qwen or ImageApiContract.Wan26
                && string.Equals(name, "watermark", StringComparison.OrdinalIgnoreCase) => "watermark",
            var name when apiContract == ImageApiContract.Qwen
                && string.Equals(name, "promptExtendMode", StringComparison.OrdinalIgnoreCase) => "prompt_extend_mode",
            var name when apiContract == ImageApiContract.Qwen
                && string.Equals(name, "enableThinking", StringComparison.OrdinalIgnoreCase) => "enable_thinking",
            var name when apiContract == ImageApiContract.Wan26
                && includeMaxImages && string.Equals(name, "maxImages", StringComparison.OrdinalIgnoreCase) => "max_images",
            var name when apiContract == ImageApiContract.Wan26
                && string.Equals(name, "enableInterleave", StringComparison.OrdinalIgnoreCase) => null,
            var name when apiContract == ImageApiContract.Wan26
                && string.Equals(name, "maxImages", StringComparison.OrdinalIgnoreCase) => null,
            _ => property.Name
        };

        if (mappedName is null)
            return;

        if (property.Value.ValueKind == JsonValueKind.Null
            && !string.Equals(mappedName, property.Name, StringComparison.Ordinal))
            return;

        parameters[mappedName] = property.Value;
    }

    private static void ValidateMappedImageOption(
        string propertyName,
        JsonElement value,
        ImageApiContract apiContract)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return;

        if ((apiContract is ImageApiContract.Qwen or ImageApiContract.Wan26)
            && string.Equals(propertyName, "negativePrompt", StringComparison.OrdinalIgnoreCase)
            || apiContract == ImageApiContract.Qwen
            && string.Equals(propertyName, "promptExtendMode", StringComparison.OrdinalIgnoreCase))
        {
            _ = value.Deserialize<string>();
        }
        else if (string.Equals(propertyName, "promptExtend", StringComparison.OrdinalIgnoreCase)
            || (apiContract is ImageApiContract.Qwen or ImageApiContract.Wan26) && string.Equals(propertyName, "watermark", StringComparison.OrdinalIgnoreCase)
            || apiContract == ImageApiContract.Qwen && string.Equals(propertyName, "enableThinking", StringComparison.OrdinalIgnoreCase)
            || apiContract == ImageApiContract.Wan26 && string.Equals(propertyName, "enableInterleave", StringComparison.OrdinalIgnoreCase))
        {
            _ = value.Deserialize<bool>();
        }
        else if (apiContract == ImageApiContract.Wan26 && string.Equals(propertyName, "maxImages", StringComparison.OrdinalIgnoreCase))
        {
            _ = value.Deserialize<int>();
        }
    }
}
