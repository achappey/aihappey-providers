using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Raven;

public sealed partial class RavenProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, request, Headers(), BaseUrl + "v1/embeddings", cancellationToken);
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, request.ToOpenAIEmbeddingRequest(GetIdentifier()), Headers(),
            BaseUrl + "v1/embeddings", cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }
}
