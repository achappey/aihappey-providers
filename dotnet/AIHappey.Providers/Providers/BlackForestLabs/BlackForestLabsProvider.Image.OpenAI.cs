using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.BlackForestLabs;

public partial class BlackForestLabsProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ExecuteBflOpenAIImageAsync(
            options.ToImageRequest(ResolveOpenAIModel(options.Model), GetIdentifier()),
            options, options.AdditionalProperties,
            cancellationToken);
        return AddBflImageWarnings(result.ToOpenAIImagesResponse(options), result);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var result = await ExecuteBflOpenAIImageAsync(
            options.ToImageRequest(ResolveOpenAIModel(options.Model), GetIdentifier()),
            options, options.AdditionalProperties,
            cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier(),
            cancellationToken);
        var result = await ExecuteBflOpenAIImageAsync(request, options, options.AdditionalProperties, cancellationToken);
        return AddBflImageWarnings(result.ToOpenAIImagesResponse(options), result);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();

        var request = await options.ToImageRequest(
            ResolveOpenAIModel(options.Model),
            GetIdentifier(),
            cancellationToken);
        var result = await ExecuteBflOpenAIImageAsync(request, options, options.AdditionalProperties, cancellationToken);
        foreach (var streamEvent in result.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    private async Task<ImageResponse> ExecuteBflOpenAIImageAsync<T>(
        ImageRequest request,
        T standardRequest,
        Dictionary<string, JsonElement>? additionalProperties,
        CancellationToken cancellationToken)
    {
        // The shared adapter preserves all OpenAI controls. BFL rejects unknown
        // fields, so forward only mapped standard controls plus RAW extension
        // data. Never filter extension fields using an endpoint allowlist.
        var native = additionalProperties?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()) ?? [];
        var warnings = new List<object>();
        foreach (var field in JsonSerializer.SerializeToElement(standardRequest, JsonOptions).EnumerateObject())
        {
            if (native.ContainsKey(field.Name) || field.Value.ValueKind == JsonValueKind.Null
                || field.Name is "model" or "prompt" or "size" or "n" or "images" or "mask" or "stream" or "response_format")
                continue;

            if (field.Name == "output_format" && request.Model != "flux-3-image")
                native[field.Name] = field.Value.Clone();
            else
                warnings.Add(new { type = "unsupported", feature = field.Name });
        }

        request.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(native, JsonOptions) };
        var result = await ImageRequest(request, cancellationToken);
        result.Warnings = result.Warnings.Concat(warnings).ToList();
        return result;
    }

    private static OpenAIImagesResponse AddBflImageWarnings(OpenAIImagesResponse response, ImageResponse result)
    {
        if (result.Warnings.Any())
            (response.AdditionalProperties ??= new())["warnings"] = JsonSerializer.SerializeToElement(result.Warnings, JsonOptions);
        return response;
    }

    private static string ResolveOpenAIModel(string? model)
    {
        var resolved = string.IsNullOrWhiteSpace(model) || model.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? "flux-2-pro"
            : model.Trim();

        const string providerPrefix = "blackforestlabs/";
        return (resolved.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? resolved[providerPrefix.Length..]
            : resolved).ToLowerInvariant();
    }
}
