using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepL;
using System.Net;
using System.Text;

namespace AIHappey.Tests;

public sealed class DeepLApiEndpointTests
{
    [Theory]
    [InlineData("free-key:fx", "api-free.deepl.com")]
    [InlineData("pro-key", "api.deepl.com")]
    public async Task Language_discovery_uses_the_endpoint_for_the_api_key_plan(
        string apiKey,
        string expectedHost)
    {
        var requestUris = new List<Uri>();
        string? authorization = null;
        var provider = new DeepLProvider(
            new TestKeyResolver(apiKey),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(request =>
            {
                requestUris.Add(request.RequestUri!);
                authorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"lang":"de","name":"German","usable_as_source":true,"usable_as_target":true,"status":"stable","features":{"formality":{"status":"stable"}}}]""",
                        Encoding.UTF8,
                        "application/json")
                };
            }));

        var models = await provider.ListModels(CancellationToken.None);

        Assert.Equal(3, requestUris.Count);
        Assert.All(requestUris, uri => Assert.Equal(expectedHost, uri.Host));
        Assert.All(requestUris, uri => Assert.Equal("/v3/languages", uri.AbsolutePath));
        Assert.Equal(new[] { "?resource=translate_text", "?resource=translate_document", "?resource=write" }, requestUris.Select(uri => uri.Query));
        Assert.Equal($"DeepL-Auth-Key {apiKey}", authorization);
        Assert.Contains(models, model => model.Name == "DeepL Translate to German");
    }

    private sealed class TestKeyResolver(string apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class TestHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new TestHttpMessageHandler(responseFactory));
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
