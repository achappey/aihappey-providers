using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DataForSEO;

namespace AIHappey.Tests;

public sealed class DataForSEOAuthenticationTests
{
    [Fact]
    public async Task Model_discovery_uses_request_scoped_basic_auth_for_each_documented_endpoint()
    {
        var expectedPaths = new[]
        {
            "/v3/ai_optimization/chat_gpt/llm_responses/models",
            "/v3/ai_optimization/claude/llm_responses/models",
            "/v3/ai_optimization/gemini/llm_responses/models",
            "/v3/ai_optimization/perplexity/llm_responses/models"
        };
        var receivedPaths = new List<string>();
        HttpClient client = null!;
        client = new HttpClient(new TestHttpMessageHandler(request =>
        {
            Assert.Equal("api.dataforseo.com", request.RequestUri!.Host);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("bG9naW46cGFzc3dvcmQ=", request.Headers.Authorization?.Parameter);
            Assert.Null(client.DefaultRequestHeaders.Authorization);
            receivedPaths.Add(request.RequestUri.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"tasks":[{"data":{"se":"chat_gpt"},"result":[{"model_name":"gpt-4o"}]}]}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }));
        using (client)
        {
            var provider = new DataForSEOProvider(
                new TestKeyResolver(),
                new AsyncCacheHelper(),
                new TestHttpClientFactory(client));

            var models = await provider.ListModels();

            Assert.Equal(expectedPaths, receivedPaths);
            Assert.Single(models);
            Assert.Equal("gpt-4o", models.Single().Name);
            Assert.Null(client.DefaultRequestHeaders.Authorization);
        }
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "bG9naW46cGFzc3dvcmQ=";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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
