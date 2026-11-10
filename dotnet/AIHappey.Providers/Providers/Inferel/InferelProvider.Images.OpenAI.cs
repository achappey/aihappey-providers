using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inferel;

public sealed partial class InferelProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        return (await ImageRequest(ImageOptions(options.ToImageRequest(options.Model, GetIdentifier())), cancellationToken))
            .ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(ImageOptions(options.ToImageRequest(options.Model, GetIdentifier())), cancellationToken);
        foreach (var part in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        return (await ImageRequest(ImageOptions(await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken)), cancellationToken))
            .ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var response = await ImageRequest(ImageOptions(await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken)), cancellationToken);
        foreach (var part in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    private ImageRequest ImageOptions(ImageRequest request)
    {
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) != true) return request;
        var options = Properties(native);
        if (options.Remove(GetIdentifier(), out var nested))
            foreach (var (name, value) in Properties(nested)) options[name] = value;
        // These are gateway transport options, not queue model parameters.
        options.Remove("stream");
        options.Remove("response_format");
        options.Remove("partial_images");
        request.ProviderOptions[GetIdentifier()] = JsonSerializer.SerializeToElement(options);
        return request;
    }
}
