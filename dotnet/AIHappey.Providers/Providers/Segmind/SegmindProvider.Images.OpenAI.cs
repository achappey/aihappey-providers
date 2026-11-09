using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Segmind;

public partial class SegmindProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        var result = response.ToOpenAIImagesResponse(options);
        result.OutputFormat = GetSegmindOutputFormat(response);
        return result;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (streamEvent is OpenAIImageGenerationCompleted completed)
                completed.OutputFormat = GetSegmindOutputFormat(response);
            yield return streamEvent;
        }
    }

    private static string GetSegmindOutputFormat(AIHappey.Vercel.Models.ImageResponse response)
    {
        var image = response.Images!.First();
        return image["data:image/".Length..image.IndexOf(';')];
    }
}
