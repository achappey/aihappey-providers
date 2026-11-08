using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AIBramha;

namespace AIHappey.Tests;

public sealed class AIBramhaModelCatalogTests
{
    [Fact]
    public async Task ListModels_maps_the_documented_category_provider_and_context_length()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("/api/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(
                """{"success":true,"models":[{"id":"gpt-4o","name":"GPT-4o","category":"text","provider":"OpenAI","context_length":128000},{"id":"dall-e-3","name":"DALL-E 3","category":"image","provider":"OpenAI","context_length":4000}]}"""));
        });
        var provider = new AIBramhaProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var models = await provider.ListModels();

        var textModel = Assert.Single(models, model => model.Id == "aibramha/gpt-4o");
        Assert.Equal("text", textModel.Type);
        Assert.Equal("OpenAI", textModel.OwnedBy);
        Assert.Equal(128000, textModel.ContextWindow);

        var imageModel = Assert.Single(models, model => model.Id == "aibramha/dall-e-3");
        Assert.Equal("image", imageModel.Type);
        Assert.Equal("OpenAI", imageModel.OwnedBy);
        Assert.Equal(4000, imageModel.ContextWindow);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
