using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;

namespace AIHappey.Tests;

public sealed class GoogleModelDiscoveryTests
{
    [Fact]
    public async Task Decision_models_are_classified_without_changing_language_models()
    {
        var provider = new GoogleAIProvider(
            new TestKeyResolver(), new AsyncCacheHelper(), NullLogger<GoogleAIProvider>.Instance,
            new TestHttpClientFactory(request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"models":[{"name":"models/gemini-decision-1","displayName":"Gemini Decision 1"},{"name":"models/gemini-3.8-flash","displayName":"Gemini Flash"}]}""",
                        Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal("decision", models.Single(model => model.Id == "google/gemini-decision-1").Type);
        Assert.Equal("language", models.Single(model => model.Id == "google/gemini-3.8-flash").Type);
    }

    [Fact]
    public async Task Model_discovery_continues_when_beta_agent_listing_is_unavailable()
    {
        var provider = new GoogleAIProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            NullLogger<GoogleAIProvider>.Instance,
            new TestHttpClientFactory(request =>
            {
                var path = request.RequestUri?.AbsolutePath
                    ?? throw new InvalidOperationException("Request URI is required.");
                if (path.EndsWith("/v1beta/models", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"models":[{"name":"models/gemini-3.8-flash","displayName":"Gemini 3.8 Flash"}]}""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                if (path.EndsWith("/v1beta/agents", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);

                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("google/gemini-3.8-flash", model.Id);
        Assert.Equal("Gemini 3.8 Flash", model.Name);
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
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
