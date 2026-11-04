using System.Text.Json;
using AIHappey.Common.Extensions;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider 
{

    private static readonly JsonSerializerOptions imageSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = GetTogetherImageProviderMetadata(imageRequest);
        var payload = new Dictionary<string, object?>();

        if (metadata is { ValueKind: JsonValueKind.Object } providerMetadata)
        {
            foreach (var property in providerMetadata.EnumerateObject())
            {
                if (!IsTogetherImageMetadataProperty(property.Name))
                    payload[property.Name] = property.Value.Clone();
            }
        }

        payload["prompt"] = imageRequest.Prompt;
        payload["model"] = imageRequest.Model;
        payload["n"] = imageRequest.N;
        payload["steps"] = ReadTogetherImageMetadata<int?>(metadata, "steps");
        payload["height"] = imageRequest.GetImageHeight() ?? 1024;
        payload["width"] = imageRequest.GetImageWidth() ?? 1024;
        payload["guidance_scale"] = ReadTogetherImageMetadata<float?>(metadata, "guidance_scale");
        payload["negative_prompt"] = ReadTogetherImageMetadata<string?>(metadata, "negative_prompt");
        payload["disable_safety_checker"] = ReadTogetherImageMetadata<bool?>(metadata, "disable_safety_checker");
        payload["seed"] = imageRequest.Seed;
        payload["response_format"] = "base64";
        payload["output_format"] = "png";

        // Null-ignore settings do not apply to dictionary entries. Remove CLR nulls
        // from optional known fields, but preserve raw JsonElement null metadata.
        foreach (var key in payload.Where(entry => entry.Value is null).Select(entry => entry.Key).ToArray())
            payload.Remove(key);

        var jsonBody = JsonSerializer.Serialize(payload, imageSettings);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "v1/images/generations"
        )
        {
            Content = new StringContent(
                jsonBody,
                Encoding.UTF8,
                "application/json"
            )
        };

        // Step 3: Send request
        using var resp = await _client.SendAsync(request, cancellationToken);
        var jsonResponse = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {jsonResponse}");
        List<string> images = [];
        List<object> warnings = [];
        using var doc = JsonDocument.Parse(jsonResponse);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data array returned from Together API.");

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64Prop))
                continue;

            var b64 = b64Prop.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(Common.Extensions.ImageExtensions.ToDataUrl(b64, "image/png"));
        }

        if (images.Count == 0)
            throw new Exception("No valid image data returned from Together API.");

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files"
            });
        }

        return new()
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };

    }

    private JsonElement? GetTogetherImageProviderMetadata(ImageRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(GetIdentifier(), out var metadata)
            || metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Provider options for '{GetIdentifier()}' must be an object.");

        return metadata;
    }

    private static bool IsTogetherImageMetadataProperty(string propertyName)
        => propertyName.Equals("steps", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("negative_prompt", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("guidance_scale", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("disable_safety_checker", StringComparison.OrdinalIgnoreCase);

    private static T? ReadTogetherImageMetadata<T>(JsonElement? metadata, string propertyName)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } providerMetadata)
            return default;

        JsonElement? value = null;
        foreach (var property in providerMetadata.EnumerateObject())
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                value = property.Value;

        return value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } propertyValue
            ? propertyValue.Deserialize<T>(JsonSerializerOptions.Web)
            : default;
    }
}
