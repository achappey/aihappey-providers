using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
{
    private const string Flux2DevEndpoint = "https://inference.datacrunch.io/flux2-dev/runsync";
    private const string Flux2FlexEndpoint = "https://relay.datacrunch.io/bfl/flux-2-flex";
    private const string Flux2ProEndpoint = "https://relay.datacrunch.io/bfl/flux-2-pro";

    public async Task<ImageResponse> Flux2ImageRequest(
        ImageRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetProviderMetadata<VerdaImageProviderMetadata>(GetIdentifier());
        var flux2 = providerMetadata?.Flux2;
        EnsureVerdaMetadataObject(flux2);

        if (!string.IsNullOrWhiteSpace(imageRequest.Size) || !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = "Flux.2 does not accept size/aspect ratio in this integration; values were ignored."
            });
        }

        if (imageRequest.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Flux.2 returns one image per request; generated a single image."
            });
        }

        if (imageRequest.Seed is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "Flux.2 seed is not supported in this integration; value was ignored."
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Flux.2 does not accept a separate mask input; value was ignored."
            });
        }

        var referenceImages = imageRequest.Files?.Select(ToDataUrl).ToList();
        if (referenceImages?.Count == 0)
            referenceImages = null;

        var payload = CreateVerdaImagePayload(providerMetadata?.AdditionalProperties, flux2);
        ApplyVerdaMetadataOption<int?>(payload, flux2, "steps");
        ApplyVerdaMetadataOption<float?>(payload, flux2, "guidance");
        var outputFormat = GetVerdaMetadataString(payload, "output_format") ?? "jpeg";
        payload["prompt"] = imageRequest.Prompt;
        payload["output_format"] = payload.TryGetValue("output_format", out var requestedFormat)
            ? requestedFormat
            : outputFormat;
        payload["enable_base64_output"] = true;

        var endpoint = imageRequest.Model switch
        {
            "flux2-dev" => Flux2DevEndpoint,
            "flux-2-flex" => Flux2FlexEndpoint,
            "flux-2-pro" => Flux2ProEndpoint,
            _ => throw new NotSupportedException($"Verda image model '{imageRequest.Model}' is not supported.")
        };

        if (referenceImages is not null)
            payload["reference_images"] = referenceImages;

        var json = JsonSerializer.Serialize(payload, Flux1Json);

        using var resp = await _client.PostAsync(
            endpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception(ExtractVerdaError(raw) ?? $"{resp.StatusCode}: {raw}");

        var mimeType = MapOutputFormatToMimeType(outputFormat);
        var images = ExtractFluxImages(raw, mimeType);
        if (images.Count == 0)
            throw new Exception("Verda returned no images.");

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

    private static void EnsureVerdaMetadataObject(JsonElement? metadata)
    {
        if (metadata is { ValueKind: not JsonValueKind.Object and not JsonValueKind.Null })
            throw new JsonException("Verda model metadata must be a JSON object.");
    }

    private static Dictionary<string, object?> CreateVerdaImagePayload(
        Dictionary<string, JsonElement>? providerOptions,
        JsonElement? modelOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (providerOptions is not null)
        {
            foreach (var (name, value) in providerOptions)
                payload[name] = value.Clone();
        }

        if (modelOptions is { ValueKind: JsonValueKind.Object } options)
        {
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static void ApplyVerdaMetadataOption<T>(
        Dictionary<string, object?> payload,
        JsonElement? modelOptions,
        string propertyName)
    {
        if (modelOptions is not { ValueKind: JsonValueKind.Object } options
            || !options.TryGetProperty(propertyName, out var value))
            return;

        var parsed = value.Deserialize<T>(JsonSerializerOptions.Web);
        if (parsed is null)
            payload.Remove(propertyName);
        else
            payload[propertyName] = parsed;
    }

    private static T? ReadVerdaMetadataOption<T>(JsonElement? modelOptions, string propertyName)
    {
        if (modelOptions is not { ValueKind: JsonValueKind.Object } options
            || !options.TryGetProperty(propertyName, out var value))
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static string? GetVerdaMetadataString(
        Dictionary<string, object?> payload,
        string propertyName)
    {
        if (!payload.TryGetValue(propertyName, out var value))
            return null;

        if (value is JsonElement { ValueKind: JsonValueKind.String } element)
            return element.GetString();

        return value as string;
    }
}
