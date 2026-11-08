using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.APIyi;

namespace AIHappey.Tests;

public sealed class APIyiProviderTests
{
    [Fact]
    public async Task ListModels_uses_the_authenticated_models_endpoint_and_maps_openai_model_ids()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("api.apiyi.com", request.RequestUri!.Host);
            Assert.Equal("/v1/models", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {"id": "gpt-4o", "object": "model", "created": 1715367049, "owned_by": "openai"},
                    {"object": "model", "owned_by": "openai"}
                  ]
                }
                """);
        });
        var provider = new APIyiProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("apiyi/gpt-4o", model.Id);
        Assert.Equal("gpt-4o", model.Name);
        Assert.Equal("openai", model.OwnedBy);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
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

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
